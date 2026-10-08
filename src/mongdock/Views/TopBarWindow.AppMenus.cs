using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 맥 메뉴바처럼 앱 이름 오른쪽에 포그라운드 앱의 메뉴 제목(파일 편집 보기 …)을 나열.
/// - 제목 클릭 → 그 아래 맥 스타일 드롭다운. 항목 클릭 → 메뉴를 연 당시 포그라운드 창에 Invoke.
/// - 메뉴가 열린 상태에서 다른 제목 위로 커서를 옮기면 클릭 없이 그 메뉴로 전환 (커서 폴링 — 열린 메뉴가 마우스를 캡처하므로).
/// - 오른쪽 구역과 겹치기 전에 넘치는 제목은 "»" 하나로 묶어 하위 메뉴로.
/// 상단바 창은 NOACTIVATE 라 메뉴를 여는 동안에도 포그라운드(단축키 대상)는 원래 앱 그대로다.
/// - 하위 항목이 없는 제목(UI 자동화로 읽은 메뉴 막대 — 윈도우 11 메모장 등)은 누르면 앱 창 안의 실제 메뉴를 펼친다.
/// - 앱 전용 "@app" 항목(예: VS Code 설정…)은 앱 이름 메뉴 맨 위에 붙는다 (<see cref="AddAppNameItems"/>).
/// </summary>
public partial class TopBarWindow
{
    private const double MenuGapToRight = 16;

    private IntPtr _menuHwnd = IntPtr.MaxValue;     // 지금 제목들이 가리키는 창 (Zero = 없음)
    private IReadOnlyList<AppMenu> _appMenus = Array.Empty<AppMenu>();
    private readonly List<Button> _titleButtons = new();
    private Button? _overflowButton;
    private ContextMenu? _openAppMenu;
    private Button? _openTitle;
    private DispatcherTimer? _hoverTimer;
    private bool _menuLayoutHooked;
    private bool _menusChangedHooked;

    /// <summary>포그라운드 앱이 바뀌었으면 메뉴 제목 다시 구성.</summary>
    private void SyncAppMenus()
    {
        var app = _currentApp;
        IntPtr hwnd = app?.Hwnd ?? IntPtr.Zero;
        if (hwnd == _menuHwnd) return;
        _menuHwnd = hwnd;
        HookMenusChanged();

        IReadOnlyList<AppMenu> menus = Array.Empty<AppMenu>();
        if (app != null && _services.Settings.Current.TopBar.ShowAppMenus)
        {
            try { menus = _services.AppMenus.GetMenus(app) ?? Array.Empty<AppMenu>(); }
            catch (Exception ex) { Log.Error("앱 메뉴 읽기 실패", ex); }
        }
        _appMenus = menus.Where(m => !string.IsNullOrWhiteSpace(m.Title)).ToList();
        BuildTitleButtons();
    }

    /// <summary>백그라운드(UI 자동화) 조회가 끝나 지금 창의 메뉴가 바뀌면 제목 다시 구성 (열린 드롭다운이 없을 때만).</summary>
    private void HookMenusChanged()
    {
        if (_menusChangedHooked) return;
        _menusChangedHooked = true;
        EventHandler<IntPtr> handler = (_, hwnd) => Dispatcher.BeginInvoke(() =>
        {
            if (hwnd != _menuHwnd || _openAppMenu?.IsOpen == true) return;
            _menuHwnd = IntPtr.MaxValue;
            SyncAppMenus();
        }, DispatcherPriority.Background);
        _services.AppMenus.MenusChanged += handler;
        Closed += (_, _) => _services.AppMenus.MenusChanged -= handler;
    }

    private void BuildTitleButtons()
    {
        CloseAppMenu();
        AppMenuBar.Children.Clear();
        _titleButtons.Clear();
        _overflowButton = null;

        if (!_menuLayoutHooked)
        {
            _menuLayoutHooked = true;
            // 바 폭·오른쪽 구역 폭·앱 이름 폭이 바뀌면 넘침 다시 계산
            RightSection.SizeChanged += (_, _) => LayoutTitles();
            AppNameButton.SizeChanged += (_, _) => LayoutTitles();
            SizeChanged += (_, _) => LayoutTitles();
        }

        for (int i = 0; i < _appMenus.Count; i++)
        {
            int index = i;
            var b = MakeTitleButton(CleanText(_appMenus[i].Title));
            b.Click += (_, _) =>
            {
                if (IsNativeOnly(index)) OpenNativeMenu(index);
                else ToggleAppMenu(b, () => BuildMenu(FreshMenu(index)));
            };
            _titleButtons.Add(b);
            AppMenuBar.Children.Add(b);
        }

        if (_appMenus.Count > 0)
        {
            _overflowButton = MakeTitleButton("»");
            _overflowButton.Click += (_, _) => ToggleAppMenu(_overflowButton, BuildOverflowMenu);
            _overflowButton.Visibility = Visibility.Collapsed;
            AppMenuBar.Children.Add(_overflowButton);
        }

        Remeasure(LeftSection);
        Dispatcher.BeginInvoke(LayoutTitles, DispatcherPriority.Loaded);
    }

    /// <summary>하위 항목을 미리 모르는 제목 (UI 자동화 메뉴 막대) → 앱 창의 실제 메뉴를 펼쳐야 함.</summary>
    private bool IsNativeOnly(int index) => index < _appMenus.Count && _appMenus[index].Items.Count == 0;

    private void OpenNativeMenu(int index)
    {
        CloseAppMenu();
        _panel?.Close();
        try { _services.AppMenus.OpenNativeMenu(_menuHwnd, _appMenus[index], index); }
        catch (Exception ex) { Log.Error("앱 메뉴 펼치기 실패", ex); }
    }

    /// <summary>앱 이름 메뉴 맨 위에 앱 전용 항목(설정… 등)과 구분선을 붙임. OnAppNameClick 에서 호출.</summary>
    private void AddAppNameItems(ContextMenu menu, AppWindowInfo? app)
    {
        if (app == null || !_services.Settings.Current.TopBar.ShowAppMenus) return;
        IReadOnlyList<AppMenuItem> items;
        try { items = _services.AppMenus.GetAppNameItems(app); }
        catch (Exception ex)
        {
            Log.Error("앱 이름 메뉴 항목 읽기 실패", ex);
            return;
        }
        if (items.Count == 0) return;
        foreach (var item in items) menu.Items.Add(BuildItem(item, app.Hwnd));
        menu.Items.Add(new Separator());
    }

    private Button MakeTitleButton(string text) => new()
    {
        Style = (Style)FindResource("BarButton"),
        Padding = new Thickness(7, 0, 7, 0),
        Margin = new Thickness(0, 3, 0, 3),
        MinWidth = 0,
        Content = new TextBlock { Text = text, FontSize = 13 },
    };

    /// <summary>오른쪽 구역과 겹치지 않을 만큼만 제목을 보이고 나머지는 » 로.</summary>
    private void LayoutTitles()
    {
        if (_titleButtons.Count == 0 || !IsLoaded || AppMenuBar.Visibility != Visibility.Visible) return;

        double start = AppMenuBar.TranslatePoint(new Point(0, 0), this).X;
        double rightEdge = RightSection.ActualWidth > 0
            ? RightSection.TranslatePoint(new Point(0, 0), this).X
            : ActualWidth;
        double available = rightEdge - MenuGapToRight - start;

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _overflowButton!.Measure(infinite);
        double overflowWidth = _overflowButton.DesiredSize.Width;

        double total = 0;
        foreach (var b in _titleButtons)
        {
            b.Measure(infinite);
            total += b.DesiredSize.Width;
        }

        bool overflow = total > available;
        double used = 0;
        int visible = 0;
        foreach (var b in _titleButtons)
        {
            double w = b.DesiredSize.Width;
            double limit = overflow ? available - overflowWidth : available;
            bool fits = used + w <= limit;
            b.Visibility = fits && visible == _titleButtons.IndexOf(b) ? Visibility.Visible : Visibility.Collapsed;
            if (b.Visibility == Visibility.Visible)
            {
                used += w;
                visible++;
            }
        }
        _firstHidden = visible;
        _overflowButton.Visibility = overflow && visible < _titleButtons.Count ? Visibility.Visible : Visibility.Collapsed;
    }

    private int _firstHidden;

    // ───────────────────────── 드롭다운 ─────────────────────────

    private ContextMenu BuildMenu(AppMenu menu)
    {
        var cm = new ContextMenu();
        foreach (var item in menu.Items) cm.Items.Add(BuildItem(item));
        return cm;
    }

    /// <summary>
    /// 드롭다운을 열 때마다 최신 메뉴 상태(체크·비활성 등)로 다시 읽음 (리뷰 M7).
    /// 같은 제목을 찾고, 없으면 같은 위치, 그래도 없으면 처음 읽은 것.
    /// </summary>
    private AppMenu FreshMenu(int index)
    {
        var old = _appMenus[index];
        if (_currentApp == null || _currentApp.Hwnd != _menuHwnd) return old;
        try
        {
            var fresh = (_services.AppMenus.GetMenus(_currentApp) ?? Array.Empty<AppMenu>())
                .Where(m => !string.IsNullOrWhiteSpace(m.Title)).ToList();
            return fresh.FirstOrDefault(m => m.Title == old.Title)
                   ?? (index < fresh.Count ? fresh[index] : old);
        }
        catch (Exception ex)
        {
            Log.Error("앱 메뉴 다시 읽기 실패", ex);
            return old;
        }
    }

    private ContextMenu BuildOverflowMenu()
    {
        var cm = new ContextMenu();
        for (int i = _firstHidden; i < _appMenus.Count; i++)
        {
            var fresh = FreshMenu(i);
            var parent = new MenuItem { Header = CleanText(fresh.Title) };
            if (IsNativeOnly(i))
            {
                int index = i;
                parent.Click += (_, _) => Dispatcher.BeginInvoke(() => OpenNativeMenu(index), DispatcherPriority.Background);
            }
            foreach (var item in fresh.Items) parent.Items.Add(BuildItem(item));
            cm.Items.Add(parent);
        }
        return cm;
    }

    private object BuildItem(AppMenuItem item) => BuildItem(item, _menuHwnd);

    private object BuildItem(AppMenuItem item, IntPtr target)
    {
        if (item.IsSeparator) return new Separator();

        string text = item.Text ?? "";
        string? shortcut = item.Shortcut;
        int tab = text.IndexOf('\t');
        if (tab >= 0)
        {
            // Win32 메뉴 문자열 "열기\tCtrl+O"
            if (string.IsNullOrWhiteSpace(shortcut)) shortcut = text[(tab + 1)..];
            text = text[..tab];
        }

        var mi = new MenuItem
        {
            Header = CleanText(text),
            IsEnabled = item.Enabled,
            IsChecked = item.Checked,
            InputGestureText = shortcut?.Trim() ?? "",
        };
        if (item.Children is { Count: > 0 } children)
        {
            foreach (var c in children) mi.Items.Add(BuildItem(c, target));
        }
        else
        {
            mi.Click += (_, _) =>
            {
                CloseAppMenu();
                // 메뉴를 닫은 뒤 실행 (단축키 전송 시 우리 메뉴가 떠 있지 않게)
                Dispatcher.BeginInvoke(() =>
                {
                    try { _services.AppMenus.Invoke(target, item); }
                    catch (Exception ex) { Log.Error($"앱 메뉴 '{text}' 실행 실패", ex); }
                }, DispatcherPriority.Background);
            };
        }
        return mi;
    }

    /// <summary>Win32 메뉴 접근 키 표시(&amp;) 제거. "&amp;&amp;" 는 "&amp;" 로.</summary>
    private static string CleanText(string text)
        => text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");

    private void ToggleAppMenu(Button title, Func<ContextMenu> build)
    {
        bool same = _openTitle == title && _openAppMenu?.IsOpen == true;
        CloseAppMenu();
        if (!same) OpenAppMenu(title, build);
    }

    private void OpenAppMenu(Button title, Func<ContextMenu> build)
    {
        _panel?.Close();
        UiTheme.Apply(_services.Settings.Current);
        ContextMenu menu;
        try { menu = build(); }
        catch (Exception ex)
        {
            Log.Error("앱 메뉴 구성 실패", ex);
            return;
        }
        if (menu.Items.Count == 0) return;

        menu.PlacementTarget = title;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -10; // 카드 그림자 여백만큼 당겨 제목 왼쪽에 맞춤
        OutsideClickWatcher.Attach(menu, _services, BarArea);
        menu.Closed += (_, _) =>
        {
            title.Tag = null;
            if (_openAppMenu == menu)
            {
                _openAppMenu = null;
                _openTitle = null;
                _hoverTimer?.Stop();
            }
        };
        _openAppMenu = menu;
        _openTitle = title;
        title.Tag = "Active";
        menu.IsOpen = true;

        _hoverTimer ??= CreateHoverTimer();
        _hoverTimer.Start();
    }

    private void CloseAppMenu()
    {
        _hoverTimer?.Stop();
        if (_openAppMenu != null) _openAppMenu.IsOpen = false;
        _openAppMenu = null;
        if (_openTitle != null) _openTitle.Tag = null;
        _openTitle = null;
    }

    /// <summary>메뉴가 열린 동안 커서가 다른 제목 위로 가면 그 메뉴로 전환 (맥 동작).</summary>
    private DispatcherTimer CreateHoverTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        t.Tick += (_, _) =>
        {
            if (_openAppMenu?.IsOpen != true || _openTitle == null) return;
            Point? cursor;
            try { cursor = _services.DesktopWindows.GetCursorPosition(Monitor); } // 이 상단바의 모니터 기준
            catch { return; }
            if (cursor is not Point c) return;

            for (int i = 0; i < _titleButtons.Count; i++)
            {
                var b = _titleButtons[i];
                if (b == _openTitle || b.Visibility != Visibility.Visible || IsNativeOnly(i)) continue;
                if (!OutsideClickWatcher.ScreenRect(b).Contains(c)) continue;
                int index = i;
                CloseAppMenu();
                OpenAppMenu(b, () => BuildMenu(FreshMenu(index)));
                return;
            }
            if (_overflowButton is { Visibility: Visibility.Visible } ob && ob != _openTitle
                && OutsideClickWatcher.ScreenRect(ob).Contains(c))
            {
                CloseAppMenu();
                OpenAppMenu(ob, BuildOverflowMenu);
            }
        };
        return t;
    }
}
