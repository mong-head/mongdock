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
        "ms-appinstaller", "mhtml", "its", "ms-its", "hcp", "ms-word", "ms-excel", "ms-powerpoint", "ms-visio", "ms-access", "ms-project", "ms-publisher", "ms-spd", "ms-infopath",
    };

    /// <summary>protocol 활성화로 열어도 되는 주소인지 (스킴이 있고 막힌 스킴이 아님, 한 줄).</summary>
    internal static bool SafeProtocol(string? launch)
    {
        if (string.IsNullOrWhiteSpace(launch) || launch.Length > 2048 || launch.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0) return false;
        int colon = launch.IndexOf(':');
        if (colon < 2) return false; // "C:\…" 같은 드라이브 경로도 막음
        string scheme = launch[..colon];
        if (!scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-') || !char.IsAsciiLetter(scheme[0])) return false; // 「.」 없음 — exefile 같은 ProgID 이름 막기
        return !BlockedSchemes.Contains(scheme);
    }

    /// <summary>Claude 데스크톱 알림인지 (스토어판·설치 파일판).</summary>
    internal static bool IsClaude(NotificationItem item)
    {
        string aumid = item.Aumid ?? "";
        return aumid.StartsWith("Claude_pzs8sxrjxfjjc!", StringComparison.OrdinalIgnoreCase) // 스토어(MSIX)판
            || aumid.StartsWith("com.squirrel.AnthropicClaude", StringComparison.OrdinalIgnoreCase); // 설치 파일판
    }

    /// <summary>
    /// Claude 알림을 누르면 열 그 세션의 링크 (사용자 10-26 — claude://claude.ai/epitaxy/local_… 가 그 세션을 여는 것 확인).
    /// 알림에 id 가 있으면(launch 에 local_…) 그것, 없으면 알림 제목으로 실행 중 세션을 찾음(ClaudeSessions). 못 찾으면 null → 창만 앞으로.
    /// 파일을 읽으므로 UI 스레드 밖에서 부를 것.
    /// </summary>
    internal static string? ClaudeLink(NotificationItem item)
    {
        if (!IsClaude(item)) return null;
        string? id = null;
        if (!string.IsNullOrEmpty(item.Launch))
        {
            var m = System.Text.RegularExpressions.Regex.Match(item.Launch, @"(?:^|[?&=/])(local_[A-Za-z0-9-]{1,64})(?:$|[&/])");
            id = m.Success ? m.Groups[1].Value : null;
        }
        id ??= ClaudeSessions.Find(item.Title);
        return id is not null && ClaudeSessions.IsSessionId(id) ? "claude://claude.ai/epitaxy/" + id : null;
    }

    /// <summary>윈도우 LaunchUri 처럼 등록된 URL 프로토콜(HKCR\스킴 의 "URL Protocol" 값)만. http·https 는 늘 됨.</summary>
    private static bool IsUrlProtocol(string scheme)
    {
        if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 알림의 화면으로 보내는 일을 맡았으면 true (protocol 은 바로, launch 인자는 별도 STA 스레드에서 활성화 서버를 찾아 부름 —
    /// 못 찾거나 실패하면 그 스레드에서 fallback 을 부름. UI 스레드를 막지 않음). 맡을 수 없으면 false → 호출한 쪽이 지금처럼.
    /// </summary>
    public static bool TryActivate(NotificationItem item, Action<bool> fallback)
    {
        try
        {
            string? launch = item.Launch;
            // Claude 데스크톱 (사용자 10-26): 알림 제목으로 실행 중 세션을 찾아 그 세션 링크로, 못 찾거나 여럿이면 지금처럼 창만 앞으로.
            // 세션 파일을 읽으므로 별도 스레드에서 (UI 를 막지 않음)
            if (IsClaude(item) && IsUrlProtocol("claude"))
            {
                var ct = new Thread(() =>
                {
                    string? link = null;
                    try { link = ClaudeLink(item); } catch (Exception ex) { Log.Warn($"Claude 세션 찾기 실패: {ex.GetType().Name}"); }
                    if (link is null)
                    {
                        Log.Info("알림 클릭: Claude 세션을 못 찾음(또는 여럿) → 창만 앞으로");
                        fallback(true);
                        return;
                    }
                    try
                    {
                        Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
                        Log.Info("알림 클릭 → Claude 그 세션"); // id·제목은 남기지 않음
                    }
                    catch (Exception ex) { Log.Warn($"Claude 세션 열기 실패: {ex.GetType().Name}"); fallback(true); }
                }) { IsBackground = true, Name = "mongdock-toast-claude" };
                ct.SetApartmentState(ApartmentState.STA);
                ct.Start();
                return true;
            }
            if (string.Equals(item.ActivationType, "protocol", StringComparison.OrdinalIgnoreCase))
            {
                if (launch is null || !SafeProtocol(launch) || !IsUrlProtocol(launch[..launch.IndexOf(':')]))
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
                if (!ok) Fallback(allowLaunch: true);
                else Interlocked.Exchange(ref settled, 1);
            }) { IsBackground = true, Name = "mongdock-toast-activate" };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            // 앱의 활성화 서버가 응답하지 않으면(멈춘 앱) 3초 뒤 앱 창이라도 앞으로 — 한 번만
            // 늦게 뜨는 앱(활성화 서버가 앱을 띄우는 중)일 수 있어 새로 실행하지 않고 이미 있는 창만 앞으로 (두 번 실행 방지, 리뷰 10-23)
            _ = Task.Delay(3000).ContinueWith(_ => { if (Volatile.Read(ref settled) == 0) { Log.Warn("알림 활성화: 앱이 3초 안에 답하지 않음 → 있는 창만 앞으로"); Fallback(allowLaunch: false); } });
            return true;

            void Fallback(bool allowLaunch)
            {
                if (Interlocked.Exchange(ref settled, 1) != 0) return;
                try { fallback(allowLaunch); } catch (Exception ex) { Log.Warn($"알림 앱 열기 실패: {ex.GetType().Name}"); }
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
                // 그 앱 항목 안에서만 (못 찾으면 앱이 하나뿐인 패키지일 때만 전체에서)
                bool single = doc.Descendants().Count(e => e.Name.LocalName == "Application") == 1;
                var attr = (appEl ?? (single ? doc.Root : null))?.Descendants().Select(e => e.Attribute("ToastActivatorCLSID")).FirstOrDefault(a => a is not null);
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
