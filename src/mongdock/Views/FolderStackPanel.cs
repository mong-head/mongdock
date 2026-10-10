using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독 폴더 판 (#24-B): 최근 것부터 최대 20개 + "탐색기에서 열기 (n개 더)".
/// 파일: 클릭 = 열기, 끌기 = 다른 앱·바탕 화면으로, 오른쪽 클릭 = 열기 / 파일 위치 열기 / 휴지통으로 버리기 / 이름 복사 / 정렬.
/// </summary>
internal sealed class FolderStackPanel : DockStackPanel
{
    private const int MaxItems = 20;
    private readonly PinItem _pin;

    /// <summary>판에서 정렬을 바꿈 (독이 저장).</summary>
    public event Action<FolderSort>? SortChanged;

    public FolderStackPanel(AppServices services, UiPalette palette, PinItem pin, Rect anchorDip, DockEdge edge, MonitorInfo monitor)
        : base(services, palette, anchorDip, edge, monitor, "mongdock Folder")
    {
        _pin = pin;
        SetBody(BuildContent());
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel();
        string path = _pin.Target;
        var opts = _pin.Folder ?? new FolderOptions();
        root.Children.Add(Header(string.IsNullOrWhiteSpace(_pin.Name) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : _pin.Name));

        var listed = DockFolderService.List(path, opts.Sort, MaxItems);
        if (listed is not { } l)
        {
            root.Children.Add(Muted(Loc.T("폴더를 찾을 수 없어요")));
            return root;
        }
        if (l.Items.Count == 0) root.Children.Add(Muted(Loc.T("비어 있어요")));
        else root.Children.Add(Grid(l.Items.Select(f => (UIElement)MakeCell(f.Name, f.FullName, f.Name, () => Open(f.FullName), f.FullName, BuildMenu(f.FullName))).ToList()));

        int more = l.Total - l.Items.Count;
        root.Children.Add(Link(more > 0 ? Loc.F($"탐색기에서 열기 ({more}개 더)") : Loc.T("탐색기에서 열기"), () => Open(path)));
        return root;
    }

    private ContextMenu BuildMenu(string path)
    {
        var menu = new ContextMenu();
        menu.Items.Add(DockMenus.Item(Loc.T("열기"), () => Open(path)));
        menu.Items.Add(DockMenus.Item(Loc.T("파일 위치 열기"), () =>
        {
            CloseAnimated();
            try { using (Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })) { } }
            catch (Exception ex) { Log.Warn($"파일 위치 열기 실패: {ex.GetType().Name}"); }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item(Loc.T("휴지통으로 버리기"), () =>
        {
            // 판을 먼저 닫고(맨 위 판에 윈도우 확인 창이 가리지 않게) 백그라운드에서 — 큰 폴더도 독이 멈추지 않게.
            // 폴더 감시가 독 아이콘을 다시 그림
            CloseAnimated();
            RecycleBin.SendInBackground(new[] { path });
        }));
        menu.Items.Add(DockMenus.Item(Loc.T("이름 복사"), () =>
        {
            try { Clipboard.SetText(Path.GetFileName(path)); }
            catch (Exception ex) { Log.Warn($"이름 복사 실패: {ex.Message}"); }
        }));
        menu.Items.Add(new Separator());
        var sort = (_pin.Folder ?? new FolderOptions()).Sort;
        menu.Items.Add(DockMenus.Item(Loc.T("추가된 날짜순"), () => SetSort(FolderSort.Added), isChecked: sort == FolderSort.Added));
        menu.Items.Add(DockMenus.Item(Loc.T("이름순"), () => SetSort(FolderSort.Name), isChecked: sort == FolderSort.Name));
        return menu;
    }

    private void SetSort(FolderSort sort)
    {
        CloseAnimated();
        SortChanged?.Invoke(sort);
    }

    private void Open(string path)
    {
        CloseAnimated();
        try { Services.Launcher.OpenFile(path); }
        catch (Exception ex) { Log.Error("독 폴더 항목 열기 실패", ex); }
    }
}
