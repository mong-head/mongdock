using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 다른 앱의 트레이 아이콘 하나 (상단바·⌃ 카드 공용). 아이콘은 원래 색 그대로.
/// 왼쪽 클릭/더블 클릭/오른쪽 클릭/가운데 클릭을 탐색기와 같은 메시지로 앱에 전달 (TrayIconService).
/// 왼쪽 버튼으로 6 DIP 넘게 끌면 클릭 대신 옮기기 (TrayIconDrag): 바 → ⌃, ⌃ 패널 → 바, 바 안 순서 바꾸기.
/// </summary>
internal sealed class TrayIconButton : Button
{
    private readonly AppServices _services;
    private readonly Image _image;
    private readonly Action? _beforeClick;
    private readonly bool _onBar;
    private bool _doubleClick;
    private Point? _downAt;
    private TrayIconDrag? _drag;
    private bool _swallowRightUp;
    private bool _swallowLeftUp;

    public TrayIconInfo Info { get; private set; }
    public double IconSize { get; }

    /// <param name="onBar">상단바에 있는 버튼이면 true, ⌃ 패널 안이면 false (끌어 놓을 때 방향).</param>
    public TrayIconButton(AppServices services, TrayIconInfo info, Style style, double iconSize, Action? beforeClick, bool onBar)
    {
        _services = services;
        _beforeClick = beforeClick;
        _onBar = onBar;
        IconSize = iconSize;
        Info = info;
        Style = style;
        _image = new Image { Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Content = _image;
        ToolTipService.SetInitialShowDelay(this, 400);
        Apply(info);

        PreviewMouseLeftButtonDown += (_, e) =>
        {
            _swallowLeftUp = false;
            _doubleClick = e.ClickCount >= 2;
            _downAt = e.GetPosition(this);
        };
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            _downAt = null;
            if (_swallowLeftUp)
            {
                _swallowLeftUp = false;
                e.Handled = true; // 오른쪽 클릭으로 취소한 끌기의 왼쪽 떼기 → 클릭 아님
                ReleaseCaptureIfIdle(e);
                return;
            }
            if (_drag is null) return;
            e.Handled = true; // Click 이 나가지 않게
            var d = _drag;
            _drag = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
            d.End(cancel: false);
        };
        LostMouseCapture += (_, _) =>
        {
            _swallowLeftUp = false;
            _swallowRightUp = false;
            if (_drag is null) return;
            var d = _drag; // 다른 창이 캡처를 가져감(Alt+Tab 등) → 취소
            _drag = null;
            d.End(cancel: true);
        };
        // 끄는 중 오른쪽 클릭 = 취소 (상단바는 포커스를 받지 않아 Esc 가 오지 않음 — 원격에서도 클릭만으로).
        // 캡처는 두 버튼을 다 뗄 때까지 유지해서, 이어지는 오른쪽·왼쪽 떼기가 다른 곳(상단바 메뉴·앱 클릭)으로 새지 않게 삼킨다.
        PreviewMouseRightButtonDown += (_, e) =>
        {
            _swallowRightUp = false;
            if (_drag is null) return;
            e.Handled = true;
            var d = _drag;
            _drag = null;
            _downAt = null;
            _swallowRightUp = true;
            _swallowLeftUp = e.LeftButton == MouseButtonState.Pressed;
            d.End(cancel: true);
        };
        PreviewMouseRightButtonUp += (_, e) =>
        {
            if (!_swallowRightUp) return;
            _swallowRightUp = false;
            e.Handled = true; // 끌기 취소용 오른쪽 클릭 → 앱 메뉴·상단바 메뉴 안 띄움
            ReleaseCaptureIfIdle(e);
        };
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

    /// <summary>끌기 취소 뒤 남은 버튼까지 다 떼면 캡처 해제.</summary>
    private void ReleaseCaptureIfIdle(MouseButtonEventArgs e)
    {
        if (_swallowLeftUp || _swallowRightUp) return;
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed) return;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_downAt is not Point down || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (_drag is null)
        {
            if (Math.Abs(pos.X - down.X) < TrayIconDrag.Threshold && Math.Abs(pos.Y - down.Y) < TrayIconDrag.Threshold) return;
            if (!IsMouseCaptured && !CaptureMouse()) return;
            ToolTipService.SetIsEnabled(this, false);
            try { _drag = new TrayIconDrag(_services, this, _onBar); }
            catch (Exception ex)
            {
                Log.Error("트레이 아이콘 끌기 시작 실패", ex);
                _downAt = null;
                return;
            }
            finally { ToolTipService.SetIsEnabled(this, true); }
        }
        e.Handled = true;
        if (PresentationSource.FromVisual(this) is null) return;
        _drag.Move(PointToScreen(pos));
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

    /// <summary>상단바에 바로 보일 아이콘 / ⌃ 카드로 갈 아이콘 (TrayIconLayout: 몽독 저장값 &gt; 윈도우 설정 &gt; ⌃).</summary>
    public static (List<TrayIconInfo> OnBar, List<TrayIconInfo> Overflow) Split(AppServices services) =>
        TrayIconLayout.Split(services.TrayIcons.Icons, services.Settings.Current.TopBar);
}
