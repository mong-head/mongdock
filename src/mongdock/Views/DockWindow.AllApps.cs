using System.IO;
using System.Windows;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>앱 모음 판 (#24): 독의 "앱 모음"(Special "launchpad") 누르면 판 열기/닫기 (시작 메뉴 대신).</summary>
public partial class DockWindow
{
    internal static bool IsAllApps(PinItem? pin) =>
        pin is { Kind: PinKind.Special } && pin.Target.Equals("launchpad", StringComparison.OrdinalIgnoreCase);

    private void ToggleAllAppsPanel(DockItemView? view, PinItem pin)
    {
        bool same = _folderPanel is AllAppsPanel;
        _folderPanel?.CloseAnimated();
        if (same || view == null) return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var a = toDip.Transform(view.PointToScreen(new Point(0, 0)));
        var b = toDip.Transform(view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight)));
        var panel = new AllAppsPanel(_services, UiTheme.Palette(_services.Settings.Current), new Rect(a, b), _layout.Edge, _monitor);
        panel.Launched += app => AppUsage.Record(AllAppsCatalog.Identity(app), _services.Settings.Current.AllApps);
        panel.PinToDockRequested += PinAppToDock;
        panel.Closed += (_, _) =>
        {
            if (_folderPanel != panel) return;
            _folderPanel = null;
            _folderPanelPin = null;
            _lastInsideTicks = Environment.TickCount64;
        };
        _folderPanel = panel;
        _folderPanelPin = pin;
        Interlocked.Increment(ref AllAppsCatalog.OpenedSinceSignal);
        panel.Show();
    }

    /// <summary>판의 "독에 고정": 시작 메뉴 바로 가기가 있으면 그것으로(창과 짝이 맞게), 없으면 AppsFolder 항목으로. 이미 있으면 그대로.</summary>
    private void PinAppToDock(AppEntry app)
    {
        PinItem? pin = null;
        if (app.Shortcut is { } lnk && File.Exists(lnk)) pin = PinFactory.CreatePin(lnk, _services.Settings);
        pin ??= new PinItem { Kind = PinKind.Aumid, Target = app.Key, Name = app.Name };
        var add = pin;
        ModifyPins(p =>
        {
            if (p.Any(x => x.Kind == add.Kind && string.Equals(x.Target, add.Target, StringComparison.OrdinalIgnoreCase))) return;
            p.Add(add); // 휴지통 앞 (저장하며 휴지통은 끝으로)
        });
        Log.Info("앱 모음 판 → 독에 고정");
    }
}
