using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 둘러보기 장면용 판 (TourScenes): 예시 앱·예시 설정(메모리 전용)으로 그리기만 하는 판.
/// 장면 모드에서는 열린 판(Current)이 되지 않고, 검색 창을 닫지 않고, 앱 목록을 새로 만들지 않음. 아이콘은 예시 아이콘.
/// </summary>
internal sealed partial class AllAppsPanel
{
    /// <summary>장면을 만드는 동안만 true (생성자에서 판 등록·목록 불러오기를 건너뜀).</summary>
    internal static bool SceneMode { get; set; }

    /// <summary>장면을 만드는 동안만: 예시 앱 아이콘 (사용자 앱 아이콘을 찾지 않음).</summary>
    internal static Func<AppEntry, ImageSource?>? ExampleIcons { get; set; }

    /// <summary>
    /// 장면 준비: 예시 앱으로 다시 그림. mode = "favorites"(즐겨찾기 + 추천) | "folder"(폴더 하나 펼침) | "unused"(잠자는 앱 + 선택 막대).
    /// expand = 펼칠 폴더 id. 그 뒤 SceneFavRow·SceneExpanded·SceneSelectionBar 로 자리를 읽음.
    /// </summary>
    internal void SceneSetup(IReadOnlyList<AppEntry> apps, string mode, string? expand = null)
    {
        _apps = apps;
        _expanded = mode == "folder" ? expand : null;
        _allOpen = mode is "unused" or "all"; // "all" = 처음 화면 + 모든 앱 펼침 (홈페이지 그림)
        _allFilter = mode == "unused" ? AllFilter.Unused : AllFilter.All;
        _unused = null;
        _unusedChecked.Clear();
        Rebuild();
        if (mode == "unused" && _unused is { Count: > 2 } rows)
        {
            foreach (var r in rows.Skip(1).Take(2)) _unusedChecked.Add(r.App.Key);
            Rebuild();
        }
    }

    internal FrameworkElement? SceneFavRow => _favRow;
    internal FrameworkElement? SceneExpanded => _expandedView;
    internal IReadOnlyList<Border> SceneFolderCells => _groupCells;
    internal Border? SceneSelectionBar => _selShown ? _selBar : null;

    /// <summary>추천 칸들 (즐겨찾기 줄 끝).</summary>
    internal IEnumerable<Border> SceneSuggestions() =>
        Descendants(_body).OfType<Border>().Where(b => b.Tag is DropTag { Kind: "suggest" });

    /// <summary>폴더 칸 (폴더 줄).</summary>
    internal IEnumerable<Border> SceneFolderTiles() =>
        Descendants(_body).OfType<Border>().Where(b => b.Tag is DropTag { Kind: "group" });

    /// <summary>펼쳐 둔 폴더의 폴더 칸.</summary>
    internal Border? SceneOpenFolderTile() => _expanded is { } id ? SceneFolderTiles().FirstOrDefault(b => b.Tag is DropTag t && t.Key == id) : null;

    /// <summary>루틴 칸들 (판 맨 위 루틴 줄).</summary>
    internal IEnumerable<Border> SceneRoutineCells() =>
        Descendants(_body).OfType<Border>().Where(b => b.Tag is DropTag { Kind: "routine" });

    /// <summary>잠자는 앱 줄들.</summary>
    internal IEnumerable<Border> SceneUnusedRows() =>
        Descendants(_body).OfType<Border>().Where(b => b.Uid.StartsWith("m:u:", StringComparison.Ordinal));
}
