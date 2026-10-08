using System.Runtime.InteropServices;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// StatusService 확장: 입력(녹음) 장치 — 기본 입력 장치의 음량·음소거(IAudioEndpointVolume + 변경 알림),
/// 입력 레벨(IAudioMeterInformation), 활성 입력 장치 목록·기본 입력 장치 변경.
/// 출력 쪽과 같은 규칙: COM 호출은 UI 스레드, 알림 스레드에서는 값만 받아 Update 로 넘긴다.
/// 기본 입력 장치 = eConsole 역할 (윈도우 사운드 설정의 "기본 장치").
/// </summary>
public sealed partial class StatusService
{
    private IAudioEndpointVolume? _inputEndpoint;
    private IAudioMeterInformation? _inputMeter;
    private VolumeCallback? _inputCallback;
    private bool _inputReconnectQueued;

    private IReadOnlyList<AudioDevice> _inputs = Array.Empty<AudioDevice>();
    private bool _hasInput;
    private double _inputVolume;
    private bool _inputMuted;

    public IReadOnlyList<AudioDevice> InputDevices { get { lock (_gate) return _inputs; } }
    public bool HasInput { get { lock (_gate) return _hasInput; } }
    public double InputVolume { get { lock (_gate) return _inputVolume; } }
    public bool InputMuted { get { lock (_gate) return _inputMuted; } }

    private void ConnectDefaultInput()
    {
        DisconnectInput();
        bool connected = false;
        if (_enumerator is not null)
        {
            IMMDevice? device = null;
            try
            {
                if (_enumerator.GetDefaultAudioEndpoint(CoreAudio.eCapture, CoreAudio.eConsole, out device) == 0 && device is not null)
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out object? o) == 0 && o is IAudioEndpointVolume ep)
                    {
                        _inputEndpoint = ep;
                        _inputCallback = new VolumeCallback(SetInputValues);
                        int hr = ep.RegisterControlChangeNotify(_inputCallback);
                        if (hr != 0) Log.Warn($"입력 음량 변경 알림 등록 실패 hr=0x{hr:X8}");
                        connected = true;
                    }
                    // 레벨 미터는 없어도 됨 (막대만 안 움직임)
                    var miid = typeof(IAudioMeterInformation).GUID;
                    if (device.Activate(ref miid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out object? m) == 0 && m is IAudioMeterInformation meter)
                        _inputMeter = meter;
                }
            }
            catch (Exception ex)
            {
                Log.Error("기본 입력 장치 연결 실패", ex);
            }
            finally
            {
                if (device is not null) Marshal.ReleaseComObject(device);
            }
        }
        Update(() => { if (_hasInput == connected) return false; _hasInput = connected; return true; });
        if (connected) ReadInputVolume();
    }

    private void DisconnectInput()
    {
        if (_inputEndpoint is not null)
        {
            try { if (_inputCallback is not null) _inputEndpoint.UnregisterControlChangeNotify(_inputCallback); } catch { }
            try { Marshal.ReleaseComObject(_inputEndpoint); } catch { }
            _inputEndpoint = null;
        }
        _inputCallback = null;
        if (_inputMeter is not null)
        {
            try { Marshal.ReleaseComObject(_inputMeter); } catch { }
            _inputMeter = null;
        }
    }

    private void ReadInputVolume()
    {
        var ep = _inputEndpoint;
        if (ep is null) return;
        if (ep.GetMasterVolumeLevelScalar(out float level) != 0) return;
        ep.GetMute(out bool mute);
        SetInputValues(level, mute);
    }

    private void SetInputValues(double level, bool mute) => Update(() =>
    {
        level = Math.Round(Math.Clamp(level, 0, 1), 3);
        if (Math.Abs(_inputVolume - level) < 0.0005 && _inputMuted == mute) return false;
        _inputVolume = level;
        _inputMuted = mute;
        return true;
    });

    public void SetInputVolume(double volume)
    {
        try
        {
            var g = Guid.Empty;
            _inputEndpoint?.SetMasterVolumeLevelScalar((float)Math.Clamp(volume, 0, 1), ref g);
            ReadInputVolume();
        }
        catch (Exception ex) { Log.Error("입력 음량 설정 실패", ex); }
    }

    public void SetInputMuted(bool muted)
    {
        try
        {
            var g = Guid.Empty;
            _inputEndpoint?.SetMute(muted, ref g);
            ReadInputVolume();
        }
        catch (Exception ex) { Log.Error("입력 음소거 설정 실패", ex); }
    }

    /// <summary>
    /// 기본 입력 장치의 지금 피크 레벨 0~1. UI 스레드에서 짧은 주기로 부름 (Changed 를 내지 않음).
    /// 어떤 앱도 녹음 스트림을 열고 있지 않으면 윈도우가 0 을 준다 — 몽독이 직접 마이크를 열지는 않음(개인 정보 표시가 뜨지 않게).
    /// </summary>
    public double GetInputPeak()
    {
        var m = _inputMeter;
        if (m is null) return 0;
        try
        {
            return m.GetPeakValue(out float peak) == 0 && !float.IsNaN(peak) ? Math.Clamp(peak, 0, 1) : 0;
        }
        catch
        {
            return 0;
        }
    }

    internal void QueueInputReconnect()
    {
        var d = _dispatcher;
        if (d is null) return;
        lock (_gate)
        {
            if (_inputReconnectQueued) return;
            _inputReconnectQueued = true;
        }
        d.InvokeAsync(() =>
        {
            lock (_gate) _inputReconnectQueued = false;
            if (_started) ConnectDefaultInput();
        });
    }

    /// <summary>활성 입력 장치 목록 (UI 스레드). 기본 장치가 바뀌거나 없어졌는데 알림을 놓쳤으면 다시 연결.</summary>
    private void RefreshInputDevices()
    {
        var list = ReadEndpoints(CoreAudio.eCapture, CoreAudio.eConsole);
        if (list is null) return;
        bool reconnect;
        lock (_gate) reconnect = _hasInput != list.Count > 0;
        Update(() =>
        {
            if (_inputs.SequenceEqual(list)) return false;
            _inputs = list;
            return true;
        });
        if (reconnect) ConnectDefaultInput();
    }
}
