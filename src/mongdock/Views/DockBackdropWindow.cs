using System.Windows;
using System.Windows.Media;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 독 패널 뒤의 아크릴 블러 배경 창. 패널과 정확히 같은 위치·크기.
/// 아크릴은 SetWindowRgn 으로 잘리지 않으므로(백엔드 실측) region 은 쓰지 않고, EnableBlur 가 켜는
/// DWM 둥근 모서리(약 8px)에 맞춰 패널 테두리 반경도 8 로 그린다.
/// 아이콘을 그리는 DockWindow 가 이 창의 owned 창이라 항상 바로 위에 있다.
/// </summary>
internal sealed class DockBackdropWindow : Window
{
    private readonly AppServices _services;
    private Color? _tint;

    public DockBackdropWindow(AppServices services)
    {
        _services = services;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "mongdock Backdrop";
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;

        SourceInitialized += (_, _) => _services.DesktopWindows.MakeOverlay(this);
    }

    /// <summary>블러 켜기/틴트 갱신 (값이 바뀔 때만 서비스 호출).</summary>
    public void Apply(Color tint)
    {
        try
        {
            if (_tint != tint)
            {
                _services.DesktopWindows.EnableBlur(this, tint);
                _tint = tint;
            }
        }
        catch (Exception ex)
        {
            Log.Error("독 블러 적용 실패", ex);
        }
    }

    public void Disable()
    {
        if (_tint == null) return;
        try { _services.DesktopWindows.DisableBlur(this); }
        catch (Exception ex) { Log.Error("독 블러 해제 실패", ex); }
        _tint = null;
    }

    /// <summary>패널 화면 영역(DIP)에 맞춤. 1px 미만 변화는 무시(매 프레임 호출 대비).</summary>
    public void SetRect(Rect r)
    {
        if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
        if (Math.Abs(Left - r.Left) >= 0.5) Left = r.Left;
        if (Math.Abs(Top - r.Top) >= 0.5) Top = r.Top;
        if (Math.Abs(Width - r.Width) >= 0.5) Width = r.Width;
        if (Math.Abs(Height - r.Height) >= 0.5) Height = r.Height;
    }
}
