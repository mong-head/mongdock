using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독 휴지통 판 (#24-C): 머리줄 "휴지통 · n개 (용량)" + [비우기…], 최근 버린 20개(지운 날짜 순), 아래 "휴지통 열기 ↗".
/// 파일은 열지 않음(휴지통 안 파일) — 오른쪽 클릭 = 복원 / 원래 위치 열기.
/// </summary>
internal sealed class RecycleBinPanel : DockStackPanel
{
    private const int MaxItems = 20;
    private readonly long _count, _bytes;

    /// <summary>[비우기…] — 독이 확인 카드를 띄움.</summary>
    public event Action? EmptyRequested;

    public RecycleBinPanel(AppServices services, UiPalette palette, Rect anchorDip, DockEdge edge, MonitorInfo monitor, long count, long bytes)
        : base(services, palette, anchorDip, edge, monitor, "mongdock Recycle Bin")
    {
        _count = count;
        _bytes = bytes;
        SetBody(Build(null));
        // 목록은 셸 COM(STA) 으로 따로 읽고 채움 — 항목이 많아도 판이 바로 뜨게
        RecycleBin.RunSta(() =>
        {
            var items = RecycleBin.List(MaxItems);
            Dispatcher.BeginInvoke(() => { if (!IsClosing) SetBody(Build(items)); });
        });
    }

    public static string Summary(long count, long bytes) => count > 0
        ? Loc.F($"휴지통 · 파일 {count}개 ({RecycleBin.FormatSize(bytes)})")
        : Loc.T("휴지통 · 비어 있음");

    private UIElement Build(List<RecycledItem>? items)
    {
        var root = new StackPanel();
        var empty = new Button
        {
            Style = (Style)Application.Current.FindResource("CardButton"),
            Content = new TextBlock { Text = Loc.T("비우기…"), FontSize = 12 },
            Background = P.Tile,
            Foreground = P.Text,
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            IsEnabled = _count > 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        empty.Click += (_, _) =>
        {
            CloseAnimated();
            EmptyRequested?.Invoke();
        };
        root.Children.Add(Header(Summary(_count, _bytes), empty));

        if (items is null) root.Children.Add(Muted("…"));
        else if (items.Count == 0) root.Children.Add(Muted(Loc.T("비어 있어요")));
        else root.Children.Add(Grid(items.Select(i => (UIElement)MakeCell(i.Name, i.Path, Tooltip(i), click: null, dragPath: null, BuildMenu(i))).ToList()));

        root.Children.Add(Link(Loc.T("휴지통 열기 ↗"), () =>
        {
            CloseAnimated();
            RecycleBin.OpenInExplorer();
        }));
        return root;
    }

    private static string Tooltip(RecycledItem i)
    {
        var lines = new List<string> { i.Name };
        if (i.OriginalFolder.Length > 0) lines.Add(Loc.F($"원래 위치: {i.OriginalFolder}"));
        if (i.Deleted > DateTime.MinValue) lines.Add(Loc.F($"지운 날짜: {i.Deleted.ToString("g", Loc.Culture)}"));
        return string.Join("\n", lines);
    }

    private ContextMenu BuildMenu(RecycledItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(DockMenus.Item(Loc.T("복원"), () =>
        {
            CloseAnimated();
            RecycleBin.RunSta(() => RecycleBin.Restore(item));
        }, enabled: item.OriginalFolder.Length > 0));
        menu.Items.Add(DockMenus.Item(Loc.T("원래 위치 열기"), () =>
        {
            CloseAnimated();
            try { using (Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.OriginalFolder}\"") { UseShellExecute = true })) { } }
            catch (Exception ex) { Log.Warn($"원래 위치 열기 실패: {ex.GetType().Name}"); }
        }, enabled: item.OriginalFolder.Length > 0 && System.IO.Directory.Exists(item.OriginalFolder)));
        return menu;
    }
}
