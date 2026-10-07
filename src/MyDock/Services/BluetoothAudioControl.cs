using System.Runtime.InteropServices;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 블루투스 오디오 기기 연결/해제 (ToothTray 방식).
/// 재생 엔드포인트(모든 상태)의 DeviceTopology 커넥터를 따라가 블루투스 KS 필터(<c>{2}.\\?\bthenum#…</c> / <c>bthhfenum#…</c>)를 찾고,
/// 그 필터의 IKsControl 로 KSPROPSETID_BtAudio / KSPROPERTY_ONESHOT_RECONNECT(0)·DISCONNECT(1) 를 GET 으로 보낸다.
/// 기기 매칭은 엔드포인트의 PKEY_Device_ContainerId ↔ Aep ContainerId.
/// COM 호출은 블로킹이므로 MTA 백그라운드 스레드(Task.Run)에서만 호출할 것.
/// </summary>
internal static class BluetoothAudioControl
{
    /// <summary>블루투스 KS 필터 하나 (엔드포인트 하나당 0개 이상).</summary>
    internal sealed record Filter(Guid ContainerId, string EndpointName, int EndpointState, string FilterId);

    internal enum Outcome { Sent, NoFilter, Failed }

    private const string BthPrefix = @"{2}.\\?\bth"; // bthenum (A2DP) / bthhfenum (HFP)

    /// <summary>블루투스 KS 필터 목록. 실패한 엔드포인트는 건너뜀.</summary>
    public static List<Filter> Enumerate(Action<string>? log = null)
    {
        var result = new List<Filter>();
        IMMDeviceEnumerator? en = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
            Walk(en, log, (f, _) => { result.Add(f); return true; });
        }
        finally
        {
            Release(en);
        }
        return result;
    }

    /// <summary>
    /// containerId 에 속한 모든 블루투스 KS 필터에 ONESHOT_RECONNECT/DISCONNECT 를 보냄.
    /// Sent = 하나 이상 S_OK (드라이버가 시도했다는 뜻 — 실제 연결 결과는 DeviceWatcher 로 확인).
    /// </summary>
    public static (Outcome Outcome, string Detail) Send(Guid containerId, bool connect, Action<string>? log = null)
    {
        if (containerId == Guid.Empty) return (Outcome.NoFilter, "컨테이너 ID 없음");
        IMMDeviceEnumerator? en = null;
        int found = 0, ok = 0;
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
            var enumerator = en;
            Walk(en, log, (f, _) =>
            {
                if (f.ContainerId != containerId || !seen.Add(f.FilterId)) return true;
                found++;
                int hr = SendTo(enumerator, f.FilterId, connect ? BtAudioNative.KSPROPERTY_ONESHOT_RECONNECT : BtAudioNative.KSPROPERTY_ONESHOT_DISCONNECT);
                if (hr >= 0) ok++;
                else errors.Add($"0x{hr:X8} {f.FilterId}");
                return true;
            });
        }
        catch (Exception ex)
        {
            return (Outcome.Failed, ex.Message);
        }
        finally
        {
            Release(en);
        }
        if (found == 0) return (Outcome.NoFilter, "블루투스 오디오 필터 없음");
        string detail = $"필터 {found}개 중 {ok}개 성공" + (errors.Count > 0 ? " / " + string.Join(", ", errors) : "");
        return (ok > 0 ? Outcome.Sent : Outcome.Failed, detail);
    }

    private static int SendTo(IMMDeviceEnumerator en, string filterId, uint propertyId)
    {
        IMMDevice? dev = null;
        object? obj = null;
        try
        {
            int hr = en.GetDevice(filterId, out dev);
            if (hr < 0 || dev is null) return hr < 0 ? hr : unchecked((int)0x80004005);
            var iid = BtAudioNative.IID_IKsControl;
            hr = dev.Activate(ref iid, BtAudioNative.CLSCTX_ALL, IntPtr.Zero, out obj);
            if (hr < 0 || obj is not IKsControl ks) return hr < 0 ? hr : unchecked((int)0x80004002);
            var prop = new KSPROPERTY
            {
                Set = BtAudioNative.KSPROPSETID_BtAudio,
                Id = propertyId,
                Flags = BtAudioNative.KSPROPERTY_TYPE_GET,
            };
            return ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005);
        }
        finally
        {
            Release(obj);
            Release(dev);
        }
    }

    /// <summary>재생 엔드포인트 → 토폴로지 커넥터 → 연결 상대가 블루투스 KS 필터면 visit. visit 이 false 면 중단.</summary>
    private static void Walk(IMMDeviceEnumerator en, Action<string>? log, Func<Filter, IMMDevice, bool> visit)
    {
        if (en.EnumAudioEndpoints(BtAudioNative.eRender, BtAudioNative.DEVICE_STATEMASK_ALL, out IMMDeviceCollection? col) < 0 || col is null) return;
        try
        {
            col.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (col.Item(i, out IMMDevice? dev) < 0 || dev is null) continue;
                object? topoObj = null;
                try
                {
                    dev.GetState(out int state);
                    Guid container = Guid.Empty;
                    string name = "";
                    if (dev.OpenPropertyStore(BtAudioNative.STGM_READ, out IPropertyStore? store) >= 0 && store is not null)
                    {
                        try
                        {
                            container = ReadGuid(store, BtAudioNative.PKEY_Device_ContainerId);
                            name = ReadString(store, CoreAudio.PKEY_Device_FriendlyName) ?? "";
                        }
                        finally { Release(store); }
                    }

                    var iid = BtAudioNative.IID_IDeviceTopology;
                    if (dev.Activate(ref iid, BtAudioNative.CLSCTX_ALL, IntPtr.Zero, out topoObj) < 0 || topoObj is not IDeviceTopology topo) continue;
                    if (topo.GetConnectorCount(out uint cc) < 0) continue;
                    for (uint c = 0; c < cc; c++)
                    {
                        string? otherId = ConnectedDeviceId(topo, c);
                        if (otherId is null || !otherId.StartsWith(BthPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!visit(new Filter(container, name, state, otherId), dev)) return;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"블루투스 오디오 엔드포인트 조회 실패 #{i}: {ex.Message}");
                }
                finally
                {
                    Release(topoObj);
                    Release(dev);
                }
            }
        }
        finally
        {
            Release(col);
        }
    }

    private static string? ConnectedDeviceId(IDeviceTopology topo, uint index)
    {
        IConnector? conn = null, other = null;
        IDeviceTopology? otherTopo = null;
        try
        {
            if (topo.GetConnector(index, out conn) < 0 || conn is null) return null;
            if (conn.GetConnectedTo(out other) < 0 || other is null) return null; // E_NOTFOUND 이면 연결 없음
            if (other is not IPart part) return null;
            if (part.GetTopologyObject(out otherTopo) < 0 || otherTopo is null) return null;
            return otherTopo.GetDeviceId(out string? id) >= 0 ? id : null;
        }
        finally
        {
            Release(otherTopo);
            Release(other);
            Release(conn);
        }
    }

    private static Guid ReadGuid(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out PROPVARIANT pv) < 0) return Guid.Empty;
        try
        {
            return pv.vt == BtAudioNative.VT_CLSID && pv.p != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(pv.p) : Guid.Empty;
        }
        finally
        {
            BtAudioNative.PropVariantClear(ref pv);
        }
    }

    private static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out PROPVARIANT pv) < 0) return null;
        try
        {
            return pv.vt == PROPVARIANT.VT_LPWSTR && pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) : null;
        }
        finally
        {
            BtAudioNative.PropVariantClear(ref pv);
        }
    }

    private static void Release(object? o)
    {
        try
        {
            if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
        catch
        {
            // 이미 해제된 RCW 등은 무시
        }
    }
}
