using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Mongdock.Services;

/// <summary>
/// 몽독 알림 배너·알림 목록을 누르면 윈도우 알림처럼 그 알림의 화면으로 (mong-head/mongdock-issues#34, 사용자 2026-10-18).
/// 토스트 XML 의 activationType·launch 를 그대로 앱에 넘김:
/// - protocol: launch 주소를 엶 (예: ms-screensketch:, windowsdefender:, powertoys:) — 위험한 스킴(file·셸 명령 등)은 안 엶.
/// - foreground + launch 인자: 앱의 알림 활성화 COM 서버(ToastActivatorCLSID — 레지스트리 AppUserModelId\…\CustomActivator 또는 패키지 매니페스트)에
///   INotificationActivationCallback.Activate(AUMID, launch) — 윈도우가 하는 것과 같음.
/// - 둘 다 못 하면 false → 호출한 쪽이 지금처럼 앱 창을 앞으로/실행.
/// 한계: launch 없이 앱 안 이벤트로만 처리하는 알림(일렉트론 앱 다수 — Claude 데스크톱 알림이 이 경우)은 바깥에서 그 화면으로 보낼 방법이 없음.
/// </summary>
internal static class ToastActivation
{
    /// <summary>열지 않는 스킴 (파일·스크립트·셸 명령·진단 도구 — 알림 내용으로 실행되면 위험).</summary>
    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "javascript", "vbscript", "data", "shell", "ms-msdt", "search-ms", "search", "ms-search", "ms-officecmd", "ms-cxh", "ms-cxh-full", "res", "mk",
    };

    /// <summary>protocol 활성화로 열어도 되는 주소인지 (스킴이 있고 막힌 스킴이 아님, 한 줄).</summary>
    internal static bool SafeProtocol(string? launch)
    {
        if (string.IsNullOrWhiteSpace(launch) || launch.Length > 2048 || launch.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0) return false;
        int colon = launch.IndexOf(':');
        if (colon < 2) return false; // "C:\…" 같은 드라이브 경로도 막음
        string scheme = launch[..colon];
        if (!scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') || !char.IsAsciiLetter(scheme[0])) return false;
        return !BlockedSchemes.Contains(scheme);
    }

    /// <summary>
    /// 알림의 화면으로 보내는 일을 맡았으면 true (protocol 은 바로, launch 인자는 별도 STA 스레드에서 활성화 서버를 찾아 부름 —
    /// 못 찾거나 실패하면 그 스레드에서 fallback 을 부름. UI 스레드를 막지 않음). 맡을 수 없으면 false → 호출한 쪽이 지금처럼.
    /// </summary>
    public static bool TryActivate(NotificationItem item, Action fallback)
    {
        try
        {
            string? launch = item.Launch;
            if (string.Equals(item.ActivationType, "protocol", StringComparison.OrdinalIgnoreCase))
            {
                if (!SafeProtocol(launch))
                {
                    Log.Warn($"알림 클릭: 열지 않는 주소 형식 ({item.Aumid})");
                    return false;
                }
                string scheme = launch![..launch!.IndexOf(':')];
                var t = new Thread(() =>
                {
                    try { Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true })?.Dispose(); }
                    catch (Exception ex) { Log.Warn($"알림 주소 열기 실패 ({scheme}:): {ex.GetType().Name}"); }
                }) { IsBackground = true, Name = "mongdock-toast-protocol" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                Log.Info($"알림 클릭 → 알림 주소 열기 ({scheme}:)"); // 주소 전체는 남기지 않음 (개인 정보가 들어 있을 수 있음)
                return true;
            }
            // 백그라운드 활성화(답장 등 버튼용)는 나중 — 클릭은 foreground 로 봄
            if (string.IsNullOrEmpty(launch)) return false;
            int settled = 0;
            var th = new Thread(() =>
            {
                bool ok = FindActivator(item.Aumid) is { } clsid && CallActivator(clsid, item.Aumid, launch);
                if (ok) Log.Info($"알림 클릭 → 앱 알림 활성화 (인자 {launch.Length}자): {item.Aumid}");
                else Log.Info($"알림 클릭: 알림 활성화 서버 없음·실패 → 앱 열기: {item.Aumid}");
                if (!ok) Fallback();
                else Interlocked.Exchange(ref settled, 1);
            }) { IsBackground = true, Name = "mongdock-toast-activate" };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            // 앱의 활성화 서버가 응답하지 않으면(멈춘 앱) 3초 뒤 앱 창이라도 앞으로 — 한 번만
            _ = Task.Delay(3000).ContinueWith(_ => { if (Volatile.Read(ref settled) == 0) { Log.Warn("알림 활성화: 앱이 3초 안에 답하지 않음 → 앱 열기"); Fallback(); } });
            return true;

            void Fallback()
            {
                if (Interlocked.Exchange(ref settled, 1) != 0) return;
                try { fallback(); } catch (Exception ex) { Log.Warn($"알림 앱 열기 실패: {ex.GetType().Name}"); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"알림 활성화 실패: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>앱의 알림 활성화 CLSID — 레지스트리(데스크톱 앱) 또는 패키지 매니페스트(desktop:ToastNotificationActivation).</summary>
    internal static Guid? FindActivator(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Classes\AppUserModelId\" + aumid);
                if (key?.GetValue("CustomActivator") is string s && Guid.TryParse(s, out var g)) return g;
            }
            catch { }
        }
        if (aumid.Contains('!') && PackageManifest(aumid) is { } manifest)
        {
            try
            {
                var doc = XDocument.Load(manifest);
                string app = aumid[(aumid.IndexOf('!') + 1)..];
                var appEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application" && string.Equals((string?)e.Attribute("Id"), app, StringComparison.OrdinalIgnoreCase));
                var attr = (appEl ?? doc.Root)?.Descendants().Select(e => e.Attribute("ToastActivatorCLSID")).FirstOrDefault(a => a is not null);
                if (attr is not null && Guid.TryParse(attr.Value, out var g)) return g;
            }
            catch (Exception ex) { Log.Warn($"패키지 매니페스트 읽기 실패: {ex.GetType().Name}"); }
        }
        return null;
    }

    /// <summary>패키지 앱의 AppxManifest.xml (설치 위치는 PackageManager 로).</summary>
    private static string? PackageManifest(string aumid)
    {
        try
        {
            string family = aumid[..aumid.IndexOf('!')];
            var pm = new Windows.Management.Deployment.PackageManager();
            var pkg = pm.FindPackagesForUser(string.Empty, family).FirstOrDefault();
            string? dir = pkg?.InstalledLocation?.Path;
            string? path = dir is null ? null : Path.Combine(dir, "AppxManifest.xml");
            return path is not null && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static bool CallActivator(Guid clsid, string aumid, string launch)
    {
        object? obj = null;
        try
        {
            AllowSetForegroundWindow(ASFW_ANY); // 앱이 자기 창을 앞으로 가져올 수 있게 (윈도우 알림 클릭과 같은 권한)
            var iid = typeof(INotificationActivationCallback).GUID;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out obj);
            if (hr != 0 || obj is not INotificationActivationCallback cb)
            {
                Log.Warn($"알림 활성화 COM 만들기 실패 0x{hr:X8}");
                return false;
            }
            hr = cb.Activate(aumid, launch, IntPtr.Zero, 0);
            if (hr != 0) Log.Warn($"알림 활성화 호출 실패 0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"알림 활성화 호출 예외: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INotificationActivationCallback
    {
        [PreserveSig]
        int Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs, IntPtr data, uint count);
    }

    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const int ASFW_ANY = -1;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
