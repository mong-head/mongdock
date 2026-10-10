using System.IO;
using System.Windows.Media;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>루틴·루틴 항목 아이콘 (#24-A). 고른 아이콘(기호·그림)이 없으면 담긴 항목 아이콘 4개를 2x2로. UI 스레드.</summary>
internal static class RoutineIcons
{
    private static readonly Dictionary<string, ImageSource> Cache = new();

    /// <summary>항목 아이콘을 얻을 핀 (앱 = exe·AUMID, 웹 = 기본 브라우저, 파일·폴더 = 그 경로의 셸 아이콘).</summary>
    public static PinItem? IconPin(RoutineItem item) => item.Kind switch
    {
        RoutineItemKind.App when !string.IsNullOrWhiteSpace(item.Aumid) => new PinItem { Kind = PinKind.Aumid, Target = item.Aumid! },
        RoutineItemKind.App when item.Target.Length > 0 => new PinItem { Kind = PinKind.Exe, Target = item.Target },
        RoutineItemKind.Url => RoutineService.DefaultBrowser() is { } browser ? new PinItem { Kind = PinKind.Exe, Target = browser } : null,
        RoutineItemKind.Path when item.Target.Length > 0 => new PinItem { Kind = PinKind.Exe, Target = item.Target },
        _ => null,
    };

    public static ImageSource? ItemIcon(AppServices services, RoutineItem item, IconStyle style)
    {
        try { return IconPin(item) is { } pin ? services.Icons.GetIcon(pin, style) : null; }
        catch (Exception ex)
        {
            Log.Warn($"루틴 항목 아이콘 실패: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>이 PC 에 없는 항목 (경로·앱이 없음) — 회색으로 보이고 실행할 때 건너뜀.</summary>
    public static bool Missing(RoutineItem item)
    {
        try
        {
            return item.Kind switch
            {
                RoutineItemKind.App => string.IsNullOrWhiteSpace(item.Aumid) && !File.Exists(Environment.ExpandEnvironmentVariables(item.Target)),
                RoutineItemKind.Path => !File.Exists(Environment.ExpandEnvironmentVariables(item.Target)) && !Directory.Exists(Environment.ExpandEnvironmentVariables(item.Target)),
                _ => false,
            };
        }
        catch (ArgumentException) { return true; }
    }

    /// <summary>루틴 아이콘: 고른 기호·그림, 아니면 항목 아이콘 2x2.</summary>
    public static ImageSource Icon(AppServices services, RoutineDef routine, IconStyle style)
    {
        if (PinIconRenderer.Render(routine.Icon) is { } chosen) return chosen;
        var items = routine.Items.Take(4).ToList();
        string key = style + "|" + string.Join("|", items.Select(i => $"{i.Kind}:{i.Aumid}:{i.Target}"));
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var img = MacIconRenderer.RoutineGrid(items.Select(i => ItemIcon(services, i, style)).ToList());
        img.Freeze();
        if (Cache.Count > 48) Cache.Clear();
        Cache[key] = img;
        return img;
    }

    /// <summary>독 핀·판 칸 다시 그릴 때 구분용 (아이콘이 바뀌는 값).</summary>
    public static string Key(RoutineDef r) =>
        $"{r.Icon?.Mode}|{r.Icon?.Color}|{r.Icon?.Glyph}|{r.Icon?.Text}|{r.Icon?.File}|" + string.Join("|", r.Items.Take(4).Select(i => $"{i.Kind}:{i.Aumid}:{i.Target}"));

    /// <summary>데스크톱 설명 ("새 데스크톱" 등) — 툴팁·편집 창.</summary>
    public static string DesktopText(RoutineDesktop d) => d.Mode switch
    {
        RoutineDesktopMode.Current => Loc.T("지금 데스크톱"),
        RoutineDesktopMode.Index => Loc.F($"데스크톱 {d.Index}"),
        _ => Loc.T("새 데스크톱"),
    };

    /// <summary>툴팁 "업무 시작 · 4개 · 새 데스크톱".</summary>
    public static string Tooltip(RoutineDef r) => $"{r.Name} · {Loc.F($"{r.Items.Count}개")}"; // 늘 새 데스크톱이라 데스크톱은 빼고

    /// <summary>위치 요약 "모니터 2 · 왼쪽 반".</summary>
    public static string PlacementText(RoutineItem item)
    {
        string? monitor = (item.Monitor?.Mode ?? RoutineMonitorMode.Keep) switch
        {
            RoutineMonitorMode.Primary => Loc.T("주 모니터"),
            RoutineMonitorMode.Index => Loc.F($"모니터 {item.Monitor!.Index}"),
            _ => null,
        };
        string place = PlacementName(item.Placement?.Mode ?? RoutinePlacementMode.Keep);
        return monitor is null ? place : $"{monitor} · {place}";
    }

    public static string PlacementName(RoutinePlacementMode mode) => mode switch
    {
        RoutinePlacementMode.Max => Loc.T("최대화"),
        RoutinePlacementMode.Left => Loc.T("왼쪽 반"),
        RoutinePlacementMode.Right => Loc.T("오른쪽 반"),
        RoutinePlacementMode.Top => Loc.T("위 반"),
        RoutinePlacementMode.Bottom => Loc.T("아래 반"),
        RoutinePlacementMode.Saved => Loc.T("저장한 위치"),
        RoutinePlacementMode.Min => Loc.T("최소화"),
        _ => Loc.T("그대로"),
    };
}
