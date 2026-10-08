using System.Windows.Media;
using Mongdock.Models;

namespace Mongdock.ViewModels;

/// <summary>독의 아이콘(또는 구분선) 하나.</summary>
public sealed class DockItemViewModel : ObservableObject
{
    public DockItemViewModel(string id, PinItem? pin, bool isSeparator, string name, ImageSource? icon)
    {
        Id = id;
        Pin = pin;
        IsSeparator = isSeparator;
        Name = name;
        Icon = icon;
    }

    /// <summary>재사용 판단용 식별자 (핀: 인덱스+대상+아이콘, 실행 중 앱: 앱 키).</summary>
    public string Id { get; }
    /// <summary>핀 항목이면 설정의 PinItem, 핀이 아닌 실행 중 앱이면 null.</summary>
    public PinItem? Pin { get; }
    public bool IsSeparator { get; }
    /// <summary>"실행 중 앱" 구역 앞의 자동 구분선 (설정에 없는 구분선).</summary>
    public bool IsAutoSeparator => IsSeparator && Pin == null;
    public string Name { get; set; }
    public ImageSource? Icon { get; }

    /// <summary>이 항목에 속한 실행 중 창들.</summary>
    public IReadOnlyList<AppWindowInfo> Windows { get; set; } = Array.Empty<AppWindowInfo>();

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    private bool _onlyElsewhere;
    /// <summary>창이 다른 가상 데스크톱에만 있음 → 실행 중 점을 흐리게.</summary>
    public bool RunningElsewhereOnly { get => _onlyElsewhere; set => Set(ref _onlyElsewhere, value); }

    private bool _isLaunching;
    /// <summary>
    /// 독에서 눌러 실행했고 아직 창이 뜨지 않음 (최대 10초). 실행 점을 바로 보여 주고 튀기/깜빡임으로 반응.
    /// 창이 나타나 IsRunning 이 되거나 시간이 지나면 DockWindow 가 false 로 내린다.
    /// </summary>
    public bool IsLaunching { get => _isLaunching; set => Set(ref _isLaunching, value); }

    private bool _hasNotification;
    public bool HasNotification { get => _hasNotification; set => Set(ref _hasNotification, value); }
}
