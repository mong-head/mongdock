using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyDock.Services;
using MyDock.ViewModels;
using WinForms = System.Windows.Forms;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace MyDock.Views;

/// <summary>
/// 알림 영역(트레이) 아이콘 + 켜고 끄기 컨트롤러. App 이 독·상단바를 만든 뒤 하나 만들고, 종료 시 Dispose.
/// - 왼쪽 클릭: 일시 정지 토글 / 오른쪽 클릭: 맥 스타일 메뉴 (독·상단바 보이기, 일시 정지, 작업 표시줄 숨기기, 자동 실행, 설정, 종료)
/// - 작업 표시줄 숨김은 여기서만 결정: HideWindowsTaskbar 이고 일시 정지가 아닐 때만 숨김.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly AppServices _services;
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _normalIcon;
    private readonly Icon _pausedIcon;
    private ContextMenu? _menu;
    private bool? _taskbarHidden;
    private bool _disposed;

    public TrayController(AppServices services)
    {
        _services = services;
        _normalIcon = DrawIcon(paused: false);
        _pausedIcon = DrawIcon(paused: true);
        _icon = new WinForms.NotifyIcon { Icon = _normalIcon, Text = AppInfo.Name, Visible = true };
        _icon.MouseUp += OnMouseUp;

        _services.Settings.SettingsChanged += OnStateChanged;
        AppState.Changed += OnStateChanged;
        Refresh();
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>아이콘·툴팁·작업 표시줄 숨김 상태를 현재 설정/일시 정지에 맞춤.</summary>
    private void Refresh()
    {
        if (_disposed) return;
        bool paused = AppState.Paused;
        _icon.Icon = paused ? _pausedIcon : _normalIcon;
        _icon.Text = paused ? $"{AppInfo.Name} (일시 정지)" : AppInfo.Name;

        bool hide = _services.Settings.Current.HideWindowsTaskbar && !paused;
        if (_taskbarHidden == hide) return;
        // 처음(null)이고 숨길 필요가 없으면 건드리지 않음 (사용자 작업 표시줄 설정 존중)
        if (_taskbarHidden == null && !hide)
        {
            _taskbarHidden = false;
            return;
        }
        try
        {
            _services.DesktopWindows.SetWindowsTaskbarHidden(hide);
            _taskbarHidden = hide;
        }
        catch (Exception ex) { Log.Error("작업 표시줄 숨김 전환 실패", ex); }
    }

    private void OnMouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            if (_menu?.IsOpen == true) _menu.IsOpen = false;
            AppState.TogglePaused();
        }
        else if (e.Button == WinForms.MouseButtons.Right)
        {
            ShowMenu();
        }
    }

    private void ShowMenu()
    {
        if (_menu?.IsOpen == true)
        {
            _menu.IsOpen = false;
            return;
        }
        UiTheme.Apply(_services.Settings.Current);
        UiFonts.Apply(_services.Settings.Current);

        var menu = new ContextMenu();
        menu.Items.Add(DockMenus.SettingsWindow(_services));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.ShowDock(_services));
        menu.Items.Add(DockMenus.ShowTopBar(_services));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Pause());
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.HideTaskbar(_services));
        menu.Items.Add(DockMenus.StartWithWindows(_services));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item("새로운 기능 보기", CoachMarks.ShowWhatsNew));
        menu.Items.Add(DockMenus.OpenSettings(_services));
        menu.Items.Add(DockMenus.Quit());
        UpdateUi.AddMenuItems(menu, _services); // 새 버전 있으면 맨 위에 "업데이트 있음 — vX 설치…"

        // 커서 위치에 (메뉴가 화면을 넘으면 WPF 가 위로 뒤집어 줌)
        WpfPoint? cursor = null;
        try { cursor = _services.DesktopWindows.GetCursorPosition(); }
        catch { }
        menu.Placement = PlacementMode.AbsolutePoint;
        if (cursor is WpfPoint c)
        {
            menu.HorizontalOffset = c.X - 10; // 카드 그림자 여백
            menu.VerticalOffset = c.Y;
        }
        OutsideClickWatcher.Attach(menu, _services, () => Array.Empty<WpfRect>());
        _menu = menu;
        menu.IsOpen = true;
    }

    // ───────────────────────── 아이콘 그리기 ─────────────────────────

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>둥근 독 모양 (둥근 판 + 아이콘 세 개 + 실행 점). 일시 정지면 회색 + 두 줄.</summary>
    private static Icon DrawIcon(bool paused)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var plate = paused ? Color.FromArgb(255, 140, 140, 146) : Color.FromArgb(255, 58, 120, 230);
            using (var path = RoundRect(new RectangleF(1, 13, 30, 15), 6))
            using (var b = new SolidBrush(plate))
                g.FillPath(b, path);
            using (var white = new SolidBrush(Color.White))
            {
                for (int i = 0; i < 3; i++)
                {
                    using var tile = RoundRect(new RectangleF(5 + i * 8.2f, 16.5f, 6.5f, 6.5f), 1.8f);
                    g.FillPath(white, tile);
                }
                g.FillEllipse(white, 7.2f, 24.6f, 2.2f, 2.2f);
            }
            if (paused)
            {
                using var pause = new SolidBrush(Color.FromArgb(255, 90, 90, 96));
                g.FillRectangle(pause, 11, 2, 3.5f, 9);
                g.FillRectangle(pause, 17.5f, 2, 3.5f, 9);
            }
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _services.Settings.SettingsChanged -= OnStateChanged;
        AppState.Changed -= OnStateChanged;
        if (_menu?.IsOpen == true) _menu.IsOpen = false;
        _icon.Visible = false;
        _icon.Dispose();
        _normalIcon.Dispose();
        _pausedIcon.Dispose();
        // 숨겨 둔 작업 표시줄은 복원 (백엔드도 종료 시 복원하지만 명시적으로)
        if (_taskbarHidden == true)
        {
            try { _services.DesktopWindows.SetWindowsTaskbarHidden(false); }
            catch (Exception ex) { Log.Error("작업 표시줄 복원 실패", ex); }
        }
    }
}
