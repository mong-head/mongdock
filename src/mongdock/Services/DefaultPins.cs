using System.IO;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>처음 실행이고 가져올 MyDockFinder 설정도 없을 때의 기본 고정 앱.</summary>
public static class DefaultPins
{
    private const string SettingsAumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    /// <summary>
    /// 새 설치의 첫 핀: Finder, Launchpad 다음에 윈도우 작업 표시줄 고정 앱을 작업 표시줄 순서대로 (<see cref="TaskbarPins"/>,
    /// 파일 탐색기 제외·중복 제거). 작업 표시줄 고정 앱이 하나도 없으면 <see cref="Create"/> 와 같음.
    /// </summary>
    public static List<PinItem> CreateInitial(ISettingsService settings)
    {
        var taskbar = TaskbarPins.Read(settings);
        if (taskbar.Count == 0) return Create();
        var pins = Base();
        int added = TaskbarPins.AddMissing(pins, taskbar);
        Log.Info($"기본 고정 앱: Finder·Launchpad + 작업 표시줄 고정 앱 {added}개");
        return pins;
    }

    private static List<PinItem> Base() => new()
    {
        new PinItem { Name = "Finder", Kind = PinKind.Exe, Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") },
        new PinItem { Name = "Launchpad", Kind = PinKind.Special, Target = "launchpad" },
    };

    /// <summary>Finder(탐색기), Launchpad, 브라우저(크롬 우선, 없으면 엣지), 설정 — 설치 확인된 것만.</summary>
    public static List<PinItem> Create()
    {
        var pins = new List<PinItem>();
        try
        {
            pins.AddRange(Base());

            string? browser = FirstExisting(
                @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
                @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
                @"%LocalAppData%\Google\Chrome\Application\chrome.exe");
            string browserName = "Google Chrome";
            if (browser is null)
            {
                browser = FirstExisting(
                    @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
                    @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe");
                browserName = "Microsoft Edge";
            }
            if (browser is not null) pins.Add(new PinItem { Name = browserName, Kind = PinKind.Exe, Target = browser });

            string? settings = AppsFolder.RestoreAumidCase(SettingsAumid);
            if (settings is not null) pins.Add(new PinItem { Name = AppsFolder.GetAppDisplayName(settings) ?? "설정", Kind = PinKind.Aumid, Target = settings });
        }
        catch (Exception ex)
        {
            Log.Error("기본 고정 앱 만들기 실패", ex);
        }
        Log.Info($"기본 고정 앱: {string.Join(", ", pins.Select(p => p.Name))}");
        return pins;
    }

    private static string? FirstExisting(params string[] paths) =>
        paths.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);
}
