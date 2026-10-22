using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 독 아이콘 ＋ = 「지금 데스크톱에 새 창」 (#41 루틴 v2, 사용자 결정 2026-10-22): 여러 창 앱(NewWindowApps.CanOpenNew)의 아이콘에 마우스를 0.3초 멈추면
/// 화면 안쪽·알림 점 반대쪽 모서리에 ＋. 누르면 지금 데스크톱에 새 창(데스크톱을 안 옮기니 알약 없음). 아이콘을 떠나면 0.4초 뒤 숨김(＋로 가는 동안은 유지),
/// ＋ 위에서는 확대 고정. 메뉴·끌기·숨김·폴더 판·터치 중에는 안 띄움. 새 창이 안 뜨면 그 앱을 「한 개만」으로 배우고 알약으로 알림.
/// </summary>
public partial class DockWindow
{
    private const int PlusDelayMs = 300, PlusLingerMs = 400;
    private DockItemView? _plusView;
    private DispatcherTimer? _plusShowTimer;
    private DispatcherTimer _plusHide => _plusHideTimer ??= MakePlusHideTimer();
    private DispatcherTimer? _plusHideTimer;

    private DispatcherTimer MakePlusHideTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PlusLingerMs) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            // 아직 ＋ 위면 기다림
            if (_plusView is { } v && v.PlusContains(System.Windows.Input.Mouse.GetPosition(this), this)) { t.Start(); return; }
            HidePlus();
        };
        return t;
    }

    private void ArmPlus(DockItemView view)
    {
        if (_plusView is not null && _plusView != view) HidePlus();
        _plusHide.Stop();
        if (PanelBorder.ContextMenu?.IsOpen == true || _dragArmed || _itemDragging || _fileDragOver || _hideTo >= 1 || _folderPanel != null || _touchReleased) return;
        if (view.Item.IsSeparator || !NewWindowApps.CanOpenNew(view.Item.Pin, view.Item.Windows)) return;
        _plusView = view;
        _plusShowTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PlusDelayMs) };
        _plusShowTimer.Stop();
        _plusShowTimer.Tick -= OnPlusShowTick;
        _plusShowTimer.Tick += OnPlusShowTick;
        _plusShowTimer.Start();
    }

    private void OnPlusShowTick(object? sender, EventArgs e)
    {
        _plusShowTimer?.Stop();
        if (_plusView is not { } v || !v.IsMouseOver || _itemDragging || _dragArmed || _hideTo >= 1) return;
        v.ShowPlus(true);
    }

    /// <summary>아이콘을 떠남: 보이는 중이면 잠깐 남겨 둠(＋로 가는 길), 아직 안 보였으면 취소.</summary>
    private void DisarmPlus(bool keepShown)
    {
        _plusShowTimer?.Stop();
        if (keepShown && _plusView is { PlusVisible: true }) _plusHide.Start();
        else HidePlus();
    }

    private void HidePlus()
    {
        _plusShowTimer?.Stop();
        _plusHideTimer?.Stop();
        _plusView?.ShowPlus(false);
        _plusView = null;
    }

    private async Task OpenNewWindowHereAsync(DockItemView view)
    {
        var item = view.Item;
        var monitor = Monitors.FromCursor();
        bool ok = await NewWindowApps.OpenNewAsync(_services.Windows, item.Pin, item.Windows);
        if (!ok)
            DesktopPill.Show(_services, monitor, Loc.T("새 창을 열 수 없는 앱이에요 · 아이콘을 누르면 그 창으로 가요"), 0);
    }
}
