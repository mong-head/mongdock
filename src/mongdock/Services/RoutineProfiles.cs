using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 루틴 항목의 브라우저 계정(크로미움 프로필) — 0.6.1. 크롬·엣지·웨일(<see cref="ChromiumProfiles"/> 가 읽는 것)만.
/// 앱 항목은 그 브라우저, 링크 항목은 기본 브라우저가 크로미움 계열일 때. 실행은 --profile-directory="디렉터리".
/// 고른 프로필이 없어졌으면 기본(마지막에 쓴 계정)으로 열고 로그 한 줄.
/// </summary>
internal static class RoutineProfiles
{
    /// <summary>시험 그림용 (report-test): 이 PC 의 실제 계정 이름·사진 대신 예시 목록. null = 실제.</summary>
    internal static Func<string, IReadOnlyList<ChromiumProfiles.Profile>>? ProfilesForTest { get; set; }

    /// <summary>항목이 여는 크로미움 계열 브라우저 exe (앱 = 그 exe, 링크 = 기본 브라우저). 아니면 null.</summary>
    public static string? BrowserExe(RoutineItem item)
    {
        try
        {
            string? exe = item.Kind switch
            {
                RoutineItemKind.App when string.IsNullOrEmpty(item.Aumid) => Environment.ExpandEnvironmentVariables(item.Target.Trim().Trim('"')),
                RoutineItemKind.Url when item.GameExe is not { Length: > 0 } => Browsers.For(item), // 링크: 고른 브라우저(없으면 기본) 의 계정
                _ => null,
            };
            if (ProfilesForTest is not null && exe is not null) return exe;
            return exe is not null && ChromiumProfiles.UserDataDir(exe) is not null ? exe : null;
        }
        catch { return null; }
    }

    /// <summary>항목의 브라우저 계정 목록 (크로미움이 아니면 빈 목록).</summary>
    public static IReadOnlyList<ChromiumProfiles.Profile> Profiles(RoutineItem item)
    {
        if (BrowserExe(item) is not { } exe) return Array.Empty<ChromiumProfiles.Profile>();
        return ProfilesForTest?.Invoke(exe) ?? ChromiumProfiles.Read(exe);
    }

    /// <summary>실행 인자 --profile-directory="…" (계정을 골랐고 아직 있을 때만). 없어졌으면 null + 로그.</summary>
    public static string? Arg(RoutineItem item)
    {
        if (string.IsNullOrEmpty(item.Profile)) return null;
        var list = Profiles(item);
        if (list.Count == 0 || !list.Any(p => string.Equals(p.Dir, item.Profile, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Info("루틴: 고른 브라우저 계정이 없어 기본 계정으로 엶");
            return null;
        }
        return $"--profile-directory=\"{item.Profile}\"";
    }

    /// <summary>창의 계정 디렉터리 (크롬 창 속성 PKEY_AppUserModel_RelaunchCommand 의 --profile-directory). 모르면 null.</summary>
    public static string? OfWindow(IntPtr hwnd, string? exePath)
    {
        if (ChromiumProfiles.UserDataDir(exePath) is null) return null;
        try { return ChromiumProfiles.ParseProfileDir(Native.Shell32.GetWindowRelaunchCommand(hwnd)); }
        catch { return null; }
    }
}
