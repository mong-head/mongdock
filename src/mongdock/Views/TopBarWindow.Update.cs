using System.Windows;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>상단바 로고의 "새 버전 있음" 점 배지 (UpdateService.Pending · 스토어판은 StoreUpdateService.HasPending).</summary>
public partial class TopBarWindow
{
    private UpdateService? _updates;
    private StoreUpdateService? _storeUpdates;

    private void HookUpdates()
    {
        _updates = UpdateService.Instance;
        if (_updates is not null) _updates.Changed += OnUpdateChanged;
        _storeUpdates = StoreUpdateService.Instance; // 스토어판
        if (_storeUpdates is not null) _storeUpdates.Changed += OnUpdateChanged;
        RefreshUpdateDot();
    }

    private void UnhookUpdates()
    {
        if (_updates is not null) _updates.Changed -= OnUpdateChanged;
        _updates = null;
        if (_storeUpdates is not null) _storeUpdates.Changed -= OnUpdateChanged;
        _storeUpdates = null;
    }

    private void OnUpdateChanged(object? sender, EventArgs e) => RefreshUpdateDot();

    private void RefreshUpdateDot()
    {
        if (_closed) return;
        UpdateDot.Visibility = UpdateUi.HasPending ? Visibility.Visible : Visibility.Collapsed;
    }
}
