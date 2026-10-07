using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyDock.Services;

namespace MyDock.Views;

/// <summary>
/// 다른 앱의 트레이 아이콘 하나 (상단바·⌃ 카드 공용). 아이콘은 원래 색 그대로.
/// 왼쪽 클릭/더블 클릭/오른쪽 클릭/가운데 클릭을 탐색기와 같은 메시지로 앱에 전달 (TrayIconService).
/// </summary>
internal sealed class TrayIconButton : Button
{
    private readonly AppServices _services;
    private readonly Image _image;
    private readonly Action? _beforeClick;
    private bool _doubleClick;

    public TrayIconInfo Info { get; private set; }

    public TrayIconButton(AppServices services, TrayIconInfo info, Style style, double iconSize, Action? beforeClick)
    {
        _services = services;
        _beforeClick = beforeClick;
        Info = info;
        Style = style;
        _image = new Image { Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Content = _image;
        ToolTipService.SetInitialShowDelay(this, 400);
        Apply(info);

        PreviewMouseLeftButtonDown += (_, e) => _doubleClick = e.ClickCount >= 2;
        Click += (_, _) =>
        {
            bool dbl = _doubleClick;
            _doubleClick = false;
            Send(TrayMouseButton.Left, dbl);
        };
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true; // 상단바 자체 오른쪽 클릭 메뉴가 뜨지 않게
            Send(TrayMouseButton.Right, false);
        };
        MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            Send(TrayMouseButton.Middle, false);
        };
        ContextMenuOpening += (_, e) => e.Handled = true;
        MouseEnter += (_, _) =>
        {
            try { _services.TrayIcons.Hover(Info, ScreenRect()); }
            catch (Exception ex) { Log.Warn($"트레이 아이콘 호버 전달 실패: {ex.Message}"); }
        };
    }

    /// <summary>같은 아이콘의 새 스냅숏 반영 (그림·툴팁).</summary>
    public void Apply(TrayIconInfo info)
    {
        Info = info;
        if (!ReferenceEquals(_image.Source, info.Icon)) _image.Source = info.Icon;
        string tip = string.IsNullOrWhiteSpace(info.Tooltip)
            ? System.IO.Path.GetFileNameWithoutExtension(info.ProcessName)
            : info.Tooltip.Trim();
        if (!Equals(ToolTip, tip)) ToolTip = tip.Length > 0 ? tip : null;
    }

    private void Send(TrayMouseButton button, bool doubleClick)
    {
        try
        {
            _beforeClick?.Invoke();
            _services.TrayIcons.Click(Info, button, doubleClick, ScreenRect());
        }
        catch (Exception ex) { Log.Error("트레이 아이콘 클릭 전달 실패", ex); }
    }

    /// <summary>화면 물리 픽셀 사각형 (앱이 Shell_NotifyIconGetRect 로 물으면 이 위치 → 팝업이 상단바 근처에).</summary>
    private Rect ScreenRect()
    {
        if (PresentationSource.FromVisual(this) is null || ActualWidth <= 0) return Rect.Empty;
        var a = PointToScreen(new Point(0, 0));
        var b = PointToScreen(new Point(ActualWidth, ActualHeight));
        return new Rect(a, b);
    }

    /// <summary>상단바에 바로 보일 아이콘 / ⌃ 카드로 갈 아이콘. 숨김(NIS_HIDDEN)·그림 없는 아이콘은 뺌.</summary>
    public static (List<TrayIconInfo> OnBar, List<TrayIconInfo> Overflow) Split(AppServices services)
    {
        var all = services.TrayIcons.Icons.Where(i => !i.IsHidden && i.Icon is not null).ToList();
        int max = Math.Clamp(services.Settings.Current.TopBar.TrayIconsVisibleCount, 0, 50);
        if (all.Count <= max) return (all, new List<TrayIconInfo>());
        return (all.Take(max).ToList(), all.Skip(max).ToList());
    }
}
