using System.IO;
using Microsoft.Win32;
using Mongdock;
using Mongdock.Services;

namespace StartupTest;

/// <summary>
/// 시작 시 실행·설치 방식 판별 시험. 패키지가 아닌 프로세스에서 돈다 (스토어판 실제 동작은 #9 MSIX 설치 시험에서).
/// - 일반판 Run 키(StartupService): 시험용 값 이름 "mongdock-selftest" 로 켜기/끄기 → 실제 "mongdock" 값은 건드리지 않음.
/// - 스토어판 서비스(PackagedStartupService)가 패키지 밖에서 만들어져도 예외 없이 "꺼짐·바꿀 수 없음".
/// 실패하면 종료 코드 1.
/// </summary>
internal static class Program
{
    private const string TestValue = "mongdock-selftest";
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        Check("패키지 아님", AppInfo.IsPackaged, false);
        Check("설치 방식", AppInfo.InstallKind, "zip·개발 빌드");

        // ── 일반판 Run 키 ──
        var run = new StartupService(TestValue);
        int changed = 0;
        run.Changed += (_, _) => changed++;
        run.SetEnabled(true);
        Check("켜면 등록됨", run.IsEnabled, true);
        Check("켜면 상태 Enabled", run.State, StartupState.Enabled);
        Check("Run 값 = 따옴표 친 exe 경로", ReadValue(), $"\"{Environment.ProcessPath}\"");
        Check("바꿀 수 있음", run.CanChange, true);
        run.SetEnabled(false);
        Check("끄면 해제됨", run.IsEnabled, false);
        Check("끄면 상태 Disabled", run.State, StartupState.Disabled);
        Check("Run 값 없음", ReadValue(), null);
        Check("Changed 2번", changed, 2);

        // ── 패키지 밖의 스토어판 서비스 ──
        var packaged = new PackagedStartupService();
        Check("스토어판(패키지 밖): 꺼짐", packaged.State, StartupState.Disabled);
        Check("스토어판(패키지 밖): 바꿀 수 없음", packaged.CanChange, false);
        packaged.SetEnabled(true); // 예외 없이 무시
        Check("스토어판(패키지 밖): 켜도 그대로", packaged.IsEnabled, false);

        BackupTests();

        Console.WriteLine(_failed == 0 ? "시작 시 실행·백업 시험: 모두 통과" : $"시작 시 실행·백업 시험: {_failed}개 실패");
        return _failed == 0 ? 0 : 1;
    }

    // ── 업데이트 전 설정 백업 (UpdateBackup) — 임시 폴더에서만 ──
    private static void BackupTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mongdock-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string cur = Mongdock.Services.WhatsNew.CurrentText;
            File.WriteAllText(Path.Combine(dir, "calendars.json"), "{}");
            // 1) 옛 버전에서 올라옴 → .bak-v0.4.1 (settings·calendars, 없는 파일은 건너뜀)
            string old = "{\"settingsVersion\":3,\"lastSeenVersion\":\"0.4.1\"}";
            File.WriteAllText(Path.Combine(dir, "settings.json"), old);
            UpdateBackup.BeforeLoad(dir, old);
            Check("이전 버전 백업 settings", File.Exists(Path.Combine(dir, "settings.json.bak-v0.4.1")), true);
            Check("이전 버전 백업 calendars", File.Exists(Path.Combine(dir, "calendars.json.bak-v0.4.1")), true);
            Check("없는 파일은 백업 안 함", File.Exists(Path.Combine(dir, "notifications-hidden.json.bak-v0.4.1")), false);
            Check("최근 백업 표시", UpdateBackup.LatestLabel(dir), "v0.4.1");
            // 2) 같은 버전·이관 필요 없음 → 백업 안 함
            string same = $"{{\"settingsVersion\":{Mongdock.Models.Settings.CurrentVersion},\"lastRunVersion\":\"{cur}\"}}";
            int before = Directory.GetFiles(dir).Length;
            UpdateBackup.BeforeLoad(dir, same);
            Check("같은 버전이면 백업 없음", Directory.GetFiles(dir).Length, before);
            // 3) 버전 정보 없는 옛 파일 → .bak-pre-v<지금>
            UpdateBackup.BeforeLoad(dir, "{}");
            Check("버전 모름 → pre", File.Exists(Path.Combine(dir, $"settings.json.bak-pre-v{cur}")), true);
            // 4) 정리: 최근 3개만 (사람이 만든 .bak-notif 는 남김)
            File.WriteAllText(Path.Combine(dir, "settings.json.bak-notif"), "x");
            foreach (var v in new[] { "0.1.0", "0.2.0", "0.3.0" })
            {
                Thread.Sleep(20);
                UpdateBackup.BeforeLoad(dir, $"{{\"lastRunVersion\":\"{v}\"}}");
            }
            var ours = Directory.GetFiles(dir, "settings.json.bak-*").Select(Path.GetFileName).Where(n => n!.Contains(".bak-v") || n.Contains(".bak-pre-v")).ToList();
            Check("최근 3개만 남김", ours.Count, 3);
            Check("가장 오래된 것(0.4.1) 지워짐", File.Exists(Path.Combine(dir, "settings.json.bak-v0.4.1")), false);
            Check("사람이 만든 백업은 그대로", File.Exists(Path.Combine(dir, "settings.json.bak-notif")), true);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string? ReadValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRunKey);
        return key?.GetValue(TestValue) as string;
    }

    private const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static void Check<T>(string name, T actual, T expected)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected)) { Console.WriteLine($"  통과  {name}"); return; }
        _failed++;
        Console.WriteLine($"  실패  {name}\n        기대: {expected}\n        실제: {actual}");
    }
}
