using System.IO;
using System.Media;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>알림 소리 선택지 하나. Path: 원본 wav 경로 ("" = 무음).</summary>
public sealed record NotificationSoundOption(string Label, string Path);

/// <summary>
/// 윈도우 알림(토스트) 소리 = 소리 구성표의 "알림"(Notification.Default) 하나. 모든 앱이 같이 씀 → 앱별 소리는 불가.
/// 값: HKCU\AppEvents\Schemes\Apps\.Default\Notification.Default\.Current 기본값 (wav 경로, "" = 무음). 제어판 소리(mmsys.cpl)와 같은 값.
///
/// 실시간 반영 (2026-10 실험, Windows 11 26200): 알림을 띄우는 셸 쪽이 이 경로를 기억해 두고 알림마다 그 경로의 파일을 새로 연다.
/// 레지스트리 값만 바꾸거나 WM_SETTINGCHANGE("AppEvents") 를 보내도 다음 알림은 여전히 예전 경로의 파일을 열었다
/// (두 wav 에 배치 oplock 을 걸어 어느 쪽이 열리는지로 확인). 그래서:
///   레지스트리는 몽독 소유 파일 %APPDATA%\mongdock\sounds\notification.wav 를 한 번 가리키게 하고,
///   이후 소리 변경은 이 파일 "내용"을 덮어써서 → 셸이 경로를 다시 읽기 전(첫 변경 직후, 보통 다시 로그인 전)에도 다음 알림부터 새 소리.
/// 원래 값은 처음 바꿀 때 한 번 settings 에 백업, "원래대로" 로 복원.
/// </summary>
public static class NotificationSoundService
{
    private const string EventKey = @"AppEvents\Schemes\Apps\.Default\Notification.Default";
    private const string CurrentKey = EventKey + @"\.Current";
    private const string DefaultKey = EventKey + @"\.Default";

    /// <summary>C:\Windows\Media 에서 알림용으로 어울리는 것 (파일명, 표시 이름). 없는 파일은 빠짐.</summary>
    private static readonly (string File, string Label)[] MediaCandidates =
    {
        ("Windows Notify System Generic.wav", Loc.T("윈도우 알림")),
        ("Windows Notify Messaging.wav", Loc.T("메시지")),
        ("Windows Notify Email.wav", Loc.T("메일")),
        ("Windows Notify Calendar.wav", Loc.T("캘린더")),
        ("Windows Proximity Notification.wav", Loc.T("띠링")),
        ("Windows Message Nudge.wav", Loc.T("톡톡")),
        ("Windows Background.wav", Loc.T("작은 알림")),
        ("Windows Ding.wav", Loc.T("딩")),
        ("Windows Information Bar.wav", Loc.T("정보 표시줄")),
        ("Windows Notify.wav", Loc.T("알림 (Windows 10)")),
        ("chimes.wav", Loc.T("차임 (클래식)")),
        ("chord.wav", Loc.T("화음 (클래식)")),
        ("ding.wav", Loc.T("딩 (클래식)")),
        ("notify.wav", Loc.T("알림 (클래식)")),
        ("tada.wav", Loc.T("짜잔")),
    };

    private static SoundPlayer? _player;

    /// <summary>몽독이 내용을 바꿔 끼우는 소리 파일. 레지스트리 .Current 는 (몽독이 바꾼 뒤엔) 이 경로.</summary>
    public static string ManagedSoundPath { get; } =
        System.IO.Path.Combine(AppInfo.DataDirectory, "sounds", "notification.wav");

    public static string MediaDirectory { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    /// <summary>C:\Windows\Media 의 알림용 후보 (있는 것만). "무음"·"윈도우 기본값"·"직접 고르기"·"원래대로" 는 UI 가 따로 붙임.</summary>
    public static IReadOnlyList<NotificationSoundOption> GetCandidates()
    {
        var list = new List<NotificationSoundOption>();
        foreach (var (file, label) in MediaCandidates)
        {
            string path = System.IO.Path.Combine(MediaDirectory, file);
            if (File.Exists(path)) list.Add(new NotificationSoundOption(label, path));
        }
        return list;
    }

    /// <summary>구성표의 원래(.Default) 알림 소리 경로 (환경 변수 펼침). 없으면 null.</summary>
    public static string? GetWindowsDefault() => ReadValue(DefaultKey, expand: true);

    /// <summary>지금 레지스트리 .Current 값 (환경 변수 펼침, "" = 무음). 키가 없으면 null.</summary>
    public static string? GetCurrentRegistryValue() => ReadValue(CurrentKey, expand: true);

    /// <summary>레지스트리가 몽독 소유 파일을 가리키는지 (= 몽독이 고른 소리가 적용 중).</summary>
    public static bool IsManagedActive()
    {
        string? cur = GetCurrentRegistryValue();
        return !string.IsNullOrEmpty(cur) && SamePath(cur, ManagedSoundPath);
    }

    /// <summary>
    /// 지금 실제로 들리는 알림 소리의 원본 경로 ("" = 무음, null = 알 수 없음).
    /// 몽독이 적용 중이면 settings.Sound, 아니면 레지스트리 값.
    /// </summary>
    public static string? GetEffectiveSound(NotificationSettings n)
        => IsManagedActive() ? n.Sound : GetCurrentRegistryValue();

    /// <summary>
    /// 알림 소리 적용. sourcePath = 원본 wav ("" = 무음). 처음이면 원래 값을 n.OriginalSound 에 백업.
    /// n 만 바꾸고 저장은 호출자(Settings.Save) 몫. 실패하면 false (로그 남김, 레지스트리는 건드리지 않거나 원래대로).
    /// </summary>
    public static bool Apply(NotificationSettings n, string sourcePath)
    {
        try
        {
            if (sourcePath.Length > 0 && !File.Exists(sourcePath))
            {
                Log.Warn($"알림 소리 파일 없음: {sourcePath}");
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(CurrentKey, writable: true);
            if (n.OriginalSound is null)
            {
                // 원본 그대로(펼치지 않은 %SystemRoot% 등) 백업. 한 번만.
                n.OriginalSound = key.GetValue("", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                Log.Info($"알림 소리 원래 값 백업: \"{n.OriginalSound}\"");
            }

            WriteManagedFile(sourcePath);

            string? cur = key.GetValue("") as string;
            if (cur is null || !SamePath(cur, ManagedSoundPath))
                key.SetValue("", ManagedSoundPath, RegistryValueKind.String);

            n.Sound = sourcePath;
            Log.Info($"알림 소리 적용: \"{sourcePath}\" (→ {ManagedSoundPath})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"알림 소리 적용 실패: {sourcePath}", ex);
            return false;
        }
    }

    /// <summary>
    /// "원래대로": 백업해 둔 원래 값을 레지스트리에 되돌리고 n.Sound = null.
    /// 셸이 아직 몽독 파일 경로를 기억하고 있을 수 있으니 그 파일 내용도 원래 소리로 맞춰 둠 (다시 로그인 전까지도 원래 소리).
    /// </summary>
    public static bool Restore(NotificationSettings n)
    {
        if (n.OriginalSound is null)
        {
            n.Sound = null;
            return true;
        }
        try
        {
            string original = n.OriginalSound;
            string expanded = Environment.ExpandEnvironmentVariables(original);
            try
            {
                if (File.Exists(ManagedSoundPath))
                    WriteManagedFile(expanded.Length > 0 && File.Exists(expanded) ? expanded : "");
            }
            catch (Exception ex) { Log.Warn("알림 소리: 몽독 파일을 원래 소리로 맞추기 실패", ex); }

            using var key = Registry.CurrentUser.CreateSubKey(CurrentKey, writable: true);
            key.SetValue("", original, original.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
            n.Sound = null;
            Log.Info($"알림 소리 원래대로: \"{original}\"");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("알림 소리 원래대로 실패", ex);
            return false;
        }
    }

    /// <summary>미리 듣기: 한 번 재생 (비동기). "" 이거나 파일이 없으면 아무것도 안 함.</summary>
    public static void Preview(string? path)
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _player = null;
            if (string.IsNullOrEmpty(path)) return;
            string p = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(p)) return;
            _player = new SoundPlayer(p);
            _player.Play();
        }
        catch (Exception ex)
        {
            Log.Warn($"알림 소리 미리 듣기 실패: {path}", ex);
        }
    }

    /// <summary>표시 이름: 후보면 그 이름, "" 면 무음, 아니면 파일 이름.</summary>
    public static string Describe(string? path)
    {
        if (path is null) return Loc.T("알 수 없음");
        if (path.Length == 0) return Loc.T("무음");
        string expanded = Environment.ExpandEnvironmentVariables(path);
        foreach (var o in GetCandidates())
            if (SamePath(o.Path, expanded)) return o.Label;
        return System.IO.Path.GetFileNameWithoutExtension(expanded);
    }

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(a)),
                System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ───────────────────────── 내부 ─────────────────────────

    private static string? ReadValue(string subKey, bool expand)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            if (key is null) return null;
            var opt = expand ? RegistryValueOptions.None : RegistryValueOptions.DoNotExpandEnvironmentNames;
            return key.GetValue("", "", opt) as string ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn($"알림 소리 레지스트리 읽기 실패: {subKey}", ex);
            return null;
        }
    }

    /// <summary>몽독 소리 파일 내용을 source 로 교체 ("" = 무음 wav). 셸이 재생 중이라 잠겨 있으면 잠깐 기다렸다 다시.</summary>
    private static void WriteManagedFile(string sourcePath)
    {
        string dir = System.IO.Path.GetDirectoryName(ManagedSoundPath)!;
        Directory.CreateDirectory(dir);
        EnsureAppContainerRead(dir);

        byte[] data = sourcePath.Length == 0 ? SilentWav() : File.ReadAllBytes(sourcePath);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // 같은 파일을 제자리에서 덮어씀 (새 파일로 바꿔치기하면 ACL 상속은 같지만 셸이 연 핸들과 충돌할 수 있어 단순 덮어쓰기)
                using var fs = new FileStream(ManagedSoundPath, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
                fs.Write(data, 0, data.Length);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// 알림 소리를 재생하는 셸 프로세스가 AppContainer 일 수 있으므로 "ALL APPLICATION PACKAGES"(S-1-15-2-1) 에 읽기 권한을 줌.
    /// (C:\Windows\Media 의 wav 들도 같은 권한을 가짐.) 이미 있으면 그대로.
    /// </summary>
    private static void EnsureAppContainerRead(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            var sid = new SecurityIdentifier("S-1-15-2-1");
            foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (r.IdentityReference == sid && r.AccessControlType == AccessControlType.Allow
                    && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute)
                    return;
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            Log.Warn("알림 소리 폴더 권한 설정 실패 (소리가 안 날 수 있음)", ex);
        }
    }

    /// <summary>0.1초 무음 PCM wav (16kHz, 16bit, mono).</summary>
    private static byte[] SilentWav()
    {
        const int sampleRate = 16000, samples = 1600, dataBytes = samples * 2;
        var ms = new MemoryStream(44 + dataBytes);
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes); w.Write(new byte[dataBytes]);
        w.Flush();
        return ms.ToArray();
    }
}
