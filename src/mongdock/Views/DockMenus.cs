using System.Windows.Controls;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>독과 상단바가 같이 쓰는 메뉴 항목. 설정을 바꾸면 Save() → SettingsChanged 로 모든 창에 반영된다.</summary>
internal static class DockMenus
{
    public static MenuItem Item(string header, Action action, bool enabled = true, bool? isChecked = null)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (isChecked.HasValue) mi.IsChecked = isChecked.Value;
        mi.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"메뉴 '{header}' 실행 실패", ex); }
        };
        return mi;
    }

    private static MenuItem Choice<T>(string header, IEnumerable<(T Value, string Label)> options, T current, Action<T> set)
        where T : struct, Enum
    {
        var parent = new MenuItem { Header = header };
        foreach (var (value, label) in options)
            parent.Items.Add(Item(label, () => set(value), isChecked: EqualityComparer<T>.Default.Equals(value, current)));
        return parent;
    }

    /// <summary>"독 위치 ▸ 왼쪽/오른쪽/아래/위".</summary>
    public static MenuItem DockPosition(AppServices services)
    {
        var dock = services.Settings.Current.Dock;
        return Choice("독 위치",
            new[] { (DockEdge.Left, "왼쪽"), (DockEdge.Right, "오른쪽"), (DockEdge.Bottom, "아래"), (DockEdge.Top, "위") },
            dock.Edge, v => Update(services, () => services.Settings.Current.Dock.Edge = v));
    }

    /// <summary>"독 동작 ▸ 자동 숨김 / 항상 보이기 / ─ / 여러 창 클릭: 창 선택 / 최근 창".</summary>
    public static MenuItem DockBehavior(AppServices services)
    {
        var dock = services.Settings.Current.Dock;
        var parent = Choice("독 동작",
            new[] { (DockMode.AutoHide, "자동 숨김"), (DockMode.Overlay, "항상 보이기") },
            dock.Mode, v => Update(services, () => services.Settings.Current.Dock.Mode = v));
        parent.Items.Add(new Separator());
        foreach (var (value, label) in new[] { (MultiWindowClick.Picker, "여러 창 클릭: 창 선택"), (MultiWindowClick.MostRecent, "여러 창 클릭: 최근 창") })
        {
            parent.Items.Add(Item(label, () => Update(services, () => services.Settings.Current.Dock.MultiWindowClick = value),
                isChecked: dock.MultiWindowClick == value));
        }
        return parent;
    }

    /// <summary>"테마 ▸ 시스템 / 라이트 / 다크".</summary>
    public static MenuItem DockThemeMenu(AppServices services)
    {
        var dock = services.Settings.Current.Dock;
        return Choice("독 테마",
            new[] { (DockTheme.System, "시스템"), (DockTheme.Light, "라이트"), (DockTheme.Dark, "다크") },
            dock.Theme, v => Update(services, () => services.Settings.Current.Dock.Theme = v));
    }

    /// <summary>"상단바 색 ▸ 투명 / 앱 색에 맞춤 / 블러 / 고정 색".</summary>
    public static MenuItem TopBarColor(AppServices services)
    {
        var top = services.Settings.Current.TopBar;
        return Choice("상단바 색",
            new[]
            {
                (TopBarColorMode.Transparent, "투명"), (TopBarColorMode.Auto, "앱 색에 맞춤"),
                (TopBarColorMode.Blur, "블러"), (TopBarColorMode.Fixed, "고정 색"),
            },
            top.ColorMode, v => Update(services, () => services.Settings.Current.TopBar.ColorMode = v));
    }

    /// <summary>윈도우 설정 → 앱 → 시작 프로그램 (스토어판에서 사용자가 끈 시작 앱은 여기서만 다시 켤 수 있음).</summary>
    public const string StartupAppsSettingsUri = "ms-settings:startupapps";

    /// <summary>
    /// "로그인 시 자동 실행" (체크 = 실제 등록 상태). 등록/해제 + 설정 저장.
    /// 스토어판에서 사용자가 윈도우 설정에서 껐으면 앱이 켤 수 없음 → 시작 앱 설정을 여는 항목, 정책이면 회색.
    /// </summary>
    public static MenuItem StartWithWindows(AppServices services)
    {
        StartupState state;
        try { state = services.Startup.State; }
        catch { state = services.Settings.Current.StartWithWindows ? StartupState.Enabled : StartupState.Disabled; }
        if (state == StartupState.DisabledByUser)
            return Item("컴퓨터를 켜면 몽독도 켜기 (윈도우 설정에서 켜기…)", () => services.Launcher.OpenFile(StartupAppsSettingsUri));
        if (state is StartupState.DisabledByPolicy or StartupState.EnabledByPolicy)
            return Item("컴퓨터를 켜면 몽독도 켜기", () => { }, enabled: false, isChecked: state == StartupState.EnabledByPolicy);

        bool on = state == StartupState.Enabled;
        return Item("컴퓨터를 켜면 몽독도 켜기", () =>
        {
            bool next = !on;
            services.Startup.SetEnabled(next);
            services.Settings.Current.StartWithWindows = next;
            services.Settings.Save();
        }, isChecked: on);
    }

    /// <summary>"독 숨기기" (Dock.Enabled=false 저장). 트레이 "독 보이기" 로 다시 켬.</summary>
    public static MenuItem HideDock(AppServices services)
        => Item("독 숨기기", () => Update(services, () => services.Settings.Current.Dock.Enabled = false));

    /// <summary>"독 보이기" 체크 (Dock.Enabled 토글).</summary>
    public static MenuItem ShowDock(AppServices services)
    {
        bool on = services.Settings.Current.Dock.Enabled;
        return Item("독 보이기", () => Update(services, () => services.Settings.Current.Dock.Enabled = !on), isChecked: on);
    }

    /// <summary>"상단바 보이기" 체크 (TopBar.Enabled 토글).</summary>
    public static MenuItem ShowTopBar(AppServices services)
    {
        bool on = services.Settings.Current.TopBar.Enabled;
        return Item("상단바 보이기", () => Update(services, () => services.Settings.Current.TopBar.Enabled = !on), isChecked: on);
    }

    /// <summary>"일시 정지" 체크 (저장 안 하는 런타임 상태).</summary>
    public static MenuItem Pause()
        => Item("일시 정지", ViewModels.AppState.TogglePaused, isChecked: ViewModels.AppState.Paused);

    /// <summary>"윈도우 작업 표시줄 숨기기" 체크 (HideWindowsTaskbar 저장, 실제 숨김은 TrayController 가 상태에 맞춰).</summary>
    public static MenuItem HideTaskbar(AppServices services)
    {
        bool on = services.Settings.Current.HideWindowsTaskbar;
        return Item("윈도우 작업 표시줄 숨기기", () => Update(services, () => services.Settings.Current.SetHideWindowsTaskbar(!on)), isChecked: on);
    }

    /// <summary>설정 창 열기 (클릭으로 바꾸는 설정 화면). 이미 열려 있으면 앞으로.</summary>
    public static MenuItem SettingsWindow(AppServices services, string header = "설정…")
        => Item(header, () => Views.SettingsWindow.Open(services));

    public static MenuItem OpenSettings(AppServices services)
        => Item("설정 파일 열기", () => services.Launcher.OpenFile(services.Settings.SettingsPath));

    public static MenuItem Quit()
        => Item($"{AppInfo.Name} 종료", () => System.Windows.Application.Current.Shutdown());

    private static void Update(AppServices services, Action change)
    {
        change();
        services.Settings.Save();
    }
}
