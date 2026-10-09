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

        Console.WriteLine(_failed == 0 ? "시작 시 실행 시험: 모두 통과" : $"시작 시 실행 시험: {_failed}개 실패");
        return _failed == 0 ? 0 : 1;
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
