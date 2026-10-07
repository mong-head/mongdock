using System.Windows.Controls;
using MyDock.Models;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>독과 상단바가 같이 쓰는 메뉴 항목.</summary>
internal static class DockMenus
{
    public static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MyDock] menu '{header}' failed: {ex}"); }
        };
        return mi;
    }

    /// <summary>"독 위치 ▸ 왼쪽/오른쪽/아래/위" 하위 메뉴. 현재 위치에 체크.</summary>
    public static MenuItem DockPosition(AppServices services)
    {
        var parent = new MenuItem { Header = "독 위치" };
        var current = services.Settings.Current.Dock.Edge;
        foreach (var (edge, label) in new[]
                 {
                     (DockEdge.Left, "왼쪽"), (DockEdge.Right, "오른쪽"),
                     (DockEdge.Bottom, "아래"), (DockEdge.Top, "위"),
                 })
        {
            var mi = Item(label, () => SetDockEdge(services, edge));
            mi.IsChecked = edge == current;
            parent.Items.Add(mi);
        }
        return parent;
    }

    public static void SetDockEdge(AppServices services, DockEdge edge)
    {
        var dock = services.Settings.Current.Dock;
        if (dock.Edge == edge) return;
        dock.Edge = edge;
        services.Settings.Save();
    }

    public static MenuItem OpenSettings(AppServices services)
        => Item("설정 파일 열기", () => services.Launcher.OpenFile(services.Settings.SettingsPath));

    public static MenuItem Quit()
        => Item("MyDock 종료", () => System.Windows.Application.Current.Shutdown());
}
