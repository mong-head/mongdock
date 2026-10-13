using System.IO;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 알림 오면 독 아이콘 2번 튀기기 (#27, spec-dock-bounce.md — 사용자가 이번에 직접 요청한 "튀기" 예외. 다른 곳에 되튐 넣지 않음).
/// 신호: 윈도우 알림(토스트, 배너와 같은 신호) · 작업 표시줄 깜빡임(FlashWindow 가 새로 시작될 때).
/// 독에 있는 앱만, 같은 앱은 10초에 한 번. 방해 금지(예외 앱은 튐)·전체 화면·일시 정지·이미 앞에 있는 앱·끌기·커서가 독 위면 안 튐.
/// 자동 숨김 중이면 "숨은 독도 잠깐 꺼내기"일 때 꺼냈다가 튀기 끝나고 2.5초 뒤 숨김 (그사이 커서가 오면 그대로).
/// </summary>
public partial class DockWindow
{
    private static readonly TimeSpan BounceCooldown = TimeSpan.FromSeconds(10);
    private const int PeekMs = 2500; // 튀기 끝나고 2.5초 뒤 숨김 (2026-10-13 사용자)
    private readonly Dictionary<string, DateTime> _bouncedAt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>알림으로 잠깐 꺼낸 독을 이 시각(TickCount64)까지 숨기지 않음.</summary>
    private long _peekUntil;

    private void OnNotificationArrived(object? sender, NotificationItem n)
    {
        if (_closed) return;
        var item = _items.FirstOrDefault(i => !i.IsSeparator && (
                       i.Pin is { Kind: PinKind.Aumid } p && string.Equals(p.Target, n.Aumid, StringComparison.OrdinalIgnoreCase)
                       || i.Windows.Any(w => string.Equals(w.Aumid, n.Aumid, StringComparison.OrdinalIgnoreCase))))
                   ?? _items.FirstOrDefault(i => !i.IsSeparator && i.Pin is not { Kind: PinKind.Folder or PinKind.Routine } && string.Equals(i.Name, n.AppName, StringComparison.CurrentCultureIgnoreCase));
        if (item is not null) TryBounce(item, n.Aumid, n.AppName, "알림");
    }

    /// <summary>깜빡임이 새로 시작된 창 (OnWindowFlashed 에서 처음 들어온 때만).</summary>
    private void BounceForFlash(IntPtr hwnd)
    {
        var item = _items.FirstOrDefault(i => i.Windows.Any(w => w.Hwnd == hwnd));
        if (item is null) return;
        var w = item.Windows.First(x => x.Hwnd == hwnd);
        TryBounce(item, w.Aumid, AppNames.Get(w), "깜빡임");
    }

    private void TryBounce(DockItemViewModel item, string? aumid, string? appName, string why)
    {
        var n = _services.Settings.Current.Notifications;
        if (!n.BounceOnNotify || !Anim.Enabled) return;
        string? key = BounceKey(item);
        if (key is null || n.BounceOff.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
        if (AppState.Paused || _fullscreen || !DockActive) return;
        if (RoutineTriggers.DndActive && !RoutineTriggers.AllowedDuringDnd(aumid, appName)) return;
        var fg = _services.Windows.ForegroundWindow;
        if (item.Windows.Any(w => w.Hwnd == fg)) return; // 이미 보고 있음
        if (_bouncedAt.TryGetValue(key, out var at) && DateTime.Now - at < BounceCooldown) return;
        if (AnyItemDrag || _dragArmed || IsMouseOver) return; // 커서 아래 자리가 움직이면 안 됨 (파도 확대·끌기)
        bool hidden = _layout.Mode == DockMode.AutoHide && _hideTo >= 1;
        if (hidden && !n.BouncePeekHidden) return; // 점만
        if (!_views.TryGetValue(item.Id, out var view)) return;
        _bouncedAt[key] = DateTime.Now;
        if (_bouncedAt.Count > 200) foreach (var k in _bouncedAt.Where(kv => DateTime.Now - kv.Value > BounceCooldown).Select(kv => kv.Key).ToList()) _bouncedAt.Remove(k);
        if (hidden)
        {
            _peekUntil = Environment.TickCount64 + DockItemView.NotifyBounceMs + PeekMs;
            SetHidden(false, animate: true);
        }
        view.NotifyBounce();
        Log.Info($"독 아이콘 튀기기 ({why}{(hidden ? ", 숨은 독 잠깐 꺼냄" : "")})");
    }

    /// <summary>앱마다 끄기 목록의 키 (고정 앱 = 실행 기록과 같은 이름, 실행 중 앱 = exe 이름). 폴더·루틴·구분선은 null.</summary>
    internal static string? BounceKey(DockItemViewModel item)
    {
        if (item.IsSeparator) return null;
        if (item.Pin is { } pin) return BounceKey(pin);
        return item.Windows.FirstOrDefault() is { } w ? BounceKey(w) : null;
    }

    internal static string? BounceKey(PinItem pin) => pin.Kind switch
    {
        PinKind.Exe or PinKind.Aumid => AllAppsCatalog.Identity(pin) ?? (pin.Kind == PinKind.Aumid ? "app:" + pin.Target.ToLowerInvariant() : null),
        _ => null,
    };

    internal static string? BounceKey(AppWindowInfo w)
    {
        string exe = Path.GetFileNameWithoutExtension(w.ProcessPath).ToLowerInvariant();
        if (exe is "applicationframehost" or "" && !string.IsNullOrEmpty(w.Aumid)) return "app:" + w.Aumid!.ToLowerInvariant();
        return exe.Length > 0 ? "exe:" + exe : null;
    }

    /// <summary>앱 오른쪽 클릭 "알림 오면 튀기기 ✓" (설정 → 알림의 앱마다 고르기와 같은 목록).</summary>
    private void AddBounceItem(System.Windows.Controls.ContextMenu menu, DockItemViewModel item)
    {
        var n = _services.Settings.Current.Notifications;
        if (!n.BounceOnNotify || BounceKey(item) is not { } key) return;
        bool on = !n.BounceOff.Contains(key, StringComparer.OrdinalIgnoreCase);
        menu.Items.Add(DockMenus.Item(Loc.T("알림 오면 튀기기"), () =>
        {
            var off = _services.Settings.Current.Notifications.BounceOff;
            if (on) off.Add(key); else off.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            _services.Settings.Save();
        }, isChecked: on));
    }
}
