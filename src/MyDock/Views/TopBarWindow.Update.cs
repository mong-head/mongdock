using System.Windows;
using MyDock.Services;

namespace MyDock.Views;

/// <summary>상단바 로고의 "새 버전 있음" 점 배지 (UpdateService.Pending 이 있을 때).</summary>
public partial class TopBarWindow
{
    private UpdateService? _updates;

    private void HookUpdates()
    {
        _updates = UpdateService.Instance;
        if (_updates is not null) _updates.Changed += OnUpdateChanged;
        RefreshUpdateDot();
    }

    private void UnhookUpdates()
    {
        if (_updates is not null) _updates.Changed -= OnUpdateChanged;
        _updates = null;
    }

    private void OnUpdateChanged(object? sender, EventArgs e) => RefreshUpdateDot();

    private void RefreshUpdateDot()
    {
        if (_closed) return;
        UpdateDot.Visibility = UpdateUi.HasPending ? Visibility.Visible : Visibility.Collapsed;
    }
}
