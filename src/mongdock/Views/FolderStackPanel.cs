using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독 폴더 판 (#24-B): 폴더 아이콘 옆(독 안쪽)에 뜨는 격자 — 최근 것부터 최대 20개(4~5열) + "탐색기에서 열기 (n개 더)".
/// 파일: 클릭 = 열기, 끌기 = 다른 앱·바탕 화면으로(DoDragDrop, 복사·이동은 윈도우), 오른쪽 클릭 = 열기 / 파일 위치 열기 / 휴지통으로 버리기 / 이름 복사.
/// 닫기: 바깥 클릭·다른 창 활성화·Esc·파일 열기. 열기·닫기는 페이드 + 살짝 올라오기 150ms (되튐 없음).
/// 키보드(Esc)를 받으려고 열릴 때 활성화함.
/// </summary>
internal sealed class FolderStackPanel : Window
{
    private const int MaxItems = 20;
    private const double Cell = 92, Thumb = 56;

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly PinItem _pin;
    private readonly Rect _anchor;
    private readonly DockEdge _edge;
    private readonly MonitorInfo _monitor;
    private readonly OutsideClickWatcher _watch;
    private readonly Border _card;
    private bool _closing;
    private Point _pressAt;
    private string? _pressPath;
    private bool _dragging;

    /// <summary>판에서 정렬을 바꿈 (독이 저장).</summary>
    public event Action<FolderSort>? SortChanged;

    public FolderStackPanel(AppServices services, UiPalette palette, PinItem pin, Rect anchorDip, DockEdge edge, MonitorInfo monitor)
    {
        _services = services;
        _p = palette;
        _pin = pin;
        _anchor = anchorDip;
        _edge = edge;
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "mongdock Folder";
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 12;
        Foreground = _p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Left = -32000;
        Top = -32000;

        _card = new Border
        {
            Background = _p.CardBackground,
            BorderBrush = _p.Divider,
            BorderThickness = new Thickness(0.75),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 12, 12, 8),
            Margin = new Thickness(16),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = _p.ShadowOpacity },
            Child = BuildContent(),
        };
        Content = _card;

        _watch = new OutsideClickWatcher(services, () => new[] { new Rect(Left, Top, ActualWidth, ActualHeight), Inflate(_anchor) }, CloseAnimated);
        SourceInitialized += (_, _) =>
        {
            _services.DesktopWindows.MakeOverlay(this);
            _watch.IgnoreHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle; // 열면서 Activate 한 자기 자신으로 닫히지 않게
        };
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) =>
        {
            Place();
            Anim.Appear(_card, 150, fromX: _edge switch { DockEdge.Left => -8, DockEdge.Right => 8, _ => 0 },
                fromY: _edge switch { DockEdge.Bottom => 8, DockEdge.Top => -8, _ => 0 });
            Activate();
            Keyboard.Focus(this);
            _watch.Start();
        };
        Deactivated += (_, _) => { if (!_dragging) CloseAnimated(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseAnimated(); } };
        Closed += (_, _) => _watch.Stop();
    }

    private static Rect Inflate(Rect r)
    {
        r.Inflate(2, 2);
        return r;
    }

    // ───────────────────────── 내용 ─────────────────────────

    private UIElement BuildContent()
    {
        var root = new StackPanel();
        string path = _pin.Target;
        var opts = _pin.Folder ?? new FolderOptions();

        // 머리: 폴더 이름
        root.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_pin.Name) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : _pin.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 8),
        });

        var listed = DockFolderService.List(path, opts.Sort, MaxItems);
        if (listed is not { } l)
        {
            root.Children.Add(Muted(Loc.T("폴더를 찾을 수 없어요")));
            return root;
        }
        if (l.Items.Count == 0)
        {
            root.Children.Add(Muted(Loc.T("비어 있어요")));
        }
        else
        {
            int cols = l.Items.Count <= 8 ? 4 : 5;
            var grid = new WrapPanel { Width = cols * Cell };
            foreach (var f in l.Items) grid.Children.Add(BuildCell(f));
            root.Children.Add(new ScrollViewer
            {
                Content = grid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(200, _monitor.WorkArea.Height * 0.6),
                Focusable = false,
            });
        }

        int more = l.Total - l.Items.Count;
        var link = new Button
        {
            Style = (Style)Application.Current.FindResource("CardLinkButton"),
            Foreground = _p.Accent,
            Padding = new Thickness(6, 6, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new TextBlock
            {
                Text = more > 0 ? Loc.F($"탐색기에서 열기 ({more}개 더)") : Loc.T("탐색기에서 열기"),
                FontSize = 12,
            },
        };
        link.Click += (_, _) =>
        {
            Open(path);
        };
        root.Children.Add(link);
        return root;
    }

    private TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = _p.SubText,
        Margin = new Thickness(4, 8, 4, 12),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private UIElement BuildCell(FileSystemInfo f)
    {
        var image = new Image { Width = Thumb, Height = Thumb, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        string full = f.FullName;
        // 썸네일은 판을 먼저 보여 준 뒤 하나씩 (느린 미리보기가 판을 늦추지 않게)
        Dispatcher.BeginInvoke(() => image.Source = DockFolderService.Thumbnail(full, 96), DispatcherPriority.Background);

        var name = new TextBlock
        {
            Text = f.Name,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 32,
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var stack = new StackPanel { Width = Cell - 8 };
        stack.Children.Add(image);
        stack.Children.Add(name);
        var cell = new Border
        {
            Width = Cell - 4,
            Margin = new Thickness(2),
            Padding = new Thickness(2, 6, 2, 6),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Child = stack,
            ToolTip = f.Name,
            Cursor = Cursors.Hand,
        };
        cell.MouseEnter += (_, _) => cell.Background = _p.Tile;
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        cell.MouseLeftButtonDown += (_, e) =>
        {
            _pressAt = e.GetPosition(this);
            _pressPath = full;
            cell.CaptureMouse();
            e.Handled = true;
        };
        cell.MouseMove += (_, e) =>
        {
            if (_pressPath != full || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressAt.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            cell.ReleaseMouseCapture();
            _pressPath = null;
            DragOut(cell, full);
        };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            bool click = _pressPath == full;
            cell.ReleaseMouseCapture();
            _pressPath = null;
            if (click) Open(full);
            e.Handled = true;
        };
        cell.ContextMenu = BuildMenu(full);
        return cell;
    }

    /// <summary>다른 앱·바탕 화면으로 끌어 놓기 (복사·이동은 놓는 쪽과 윈도우가 정함). 끄는 동안 판은 닫지 않음.</summary>
    private void DragOut(DependencyObject source, string path)
    {
        _dragging = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            Log.Warn($"독 폴더 끌어 놓기 실패: {ex.GetType().Name}");
        }
        finally
        {
            _dragging = false;
        }
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
            var t = new System.Threading.Thread(() => { if (RecycleBin.Send(path)) Log.Info("독 폴더: 휴지통으로 버림"); }) { IsBackground = true };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
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
        try { _services.Launcher.OpenFile(path); }
        catch (Exception ex) { Log.Error("독 폴더 항목 열기 실패", ex); }
    }

    // ───────────────────────── 위치·닫기 ─────────────────────────

    /// <summary>폴더 아이콘 옆(독 안쪽)에, 작업 영역 안으로.</summary>
    private void Place()
    {
        if (ActualWidth <= 0 || _closing) return;
        _services.DesktopWindows.EnsureOnMonitor(this, _monitor);
        var work = _monitor.WorkArea;
        if (work.IsEmpty || work.Width <= 0) work = _monitor.Bounds;
        const double gap = 2; // 카드 여백(16)이 이미 있음
        double w = ActualWidth, h = ActualHeight;
        double cx = _anchor.Left + _anchor.Width / 2, cy = _anchor.Top + _anchor.Height / 2;
        double left, top;
        switch (_edge)
        {
            case DockEdge.Left: left = _anchor.Right + gap; top = cy - h / 2; break;
            case DockEdge.Top: left = cx - w / 2; top = _anchor.Bottom + gap; break;
            case DockEdge.Right: left = _anchor.Left - gap - w; top = cy - h / 2; break;
            default: left = cx - w / 2; top = _anchor.Top - gap - h; break;
        }
        Left = Math.Round(Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - w)));
        Top = Math.Round(Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h)));
    }

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _watch.Stop();
        Anim.Disappear(_card, 120, () => Dispatcher.BeginInvoke(Close));
    }
}

/// <summary>휴지통으로 보내기 (되돌릴 수 있음 — 완전 삭제 없음, 확인 창 없음).</summary>
internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>휴지통으로. 휴지통에 못 넣는 경우(네트워크 드라이브 등) 윈도우가 "완전히 지울까요?" 를 묻게 둠(FOF_WANTNUKEWARNING) — 몽독이 직접 완전 삭제하지 않음.</summary>
    public static bool Send(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING),
            };
            int r = SHFileOperation(ref op);
            if (r != 0 || op.fAnyOperationsAborted) Log.Warn($"휴지통으로 보내기 실패/취소 (코드 {r})");
            return r == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            Log.Warn($"휴지통으로 보내기 실패: {ex.GetType().Name}");
            return false;
        }
    }
}
