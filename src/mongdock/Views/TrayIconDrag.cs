using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Mongdock.Native;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>트레이 아이콘을 끌다가 놓을 곳.</summary>
internal enum TrayDropKind { None, Bar, More }

/// <summary>상단바 하나에 대한 놓기 판정 결과.</summary>
internal readonly record struct TrayDropHit(TrayDropKind Kind, int BarIndex);

/// <summary>
/// 트레이 아이콘 끌어 옮기기 (상단바 ↔ ⌃ 패널, 바 안 순서 바꾸기).
/// 마우스는 끄는 버튼이 캡처한 채로 화면 좌표(물리 픽셀)를 받아, 열려 있는 모든 상단바에 놓을 자리를 묻는다.
/// 놓으면 TrayIconLayout.Move → Settings.Save → SettingsChanged 로 상단바·⌃ 패널이 다시 그려진다.
/// </summary>
internal sealed class TrayIconDrag
{
    /// <summary>클릭과 끌기를 가르는 거리 (DIP).</summary>
    public const double Threshold = 6;

    private readonly AppServices _services;
    private readonly TrayIconButton _source;
    private readonly bool _fromBar;
    private readonly TrayDragGhostWindow _ghost;
    private TopBarWindow? _hoverBar;
    private TrayDropHit _hit;

    public static bool IsActive { get; private set; }

    public TrayIconDrag(AppServices services, TrayIconButton source, bool fromBar)
    {
        _services = services;
        _source = source;
        _fromBar = fromBar;
        _ghost = new TrayDragGhostWindow(services, source.Info.Icon, source.IconSize);
        IsActive = true;
        bool ok = false;
        try
        {
            if (_fromBar) source.Opacity = 0.0; // 원래 자리는 비워 둠 (복사본이 커서를 따라다님)
            foreach (var bar in TopBarWindow.TrayHosts) bar.BeginTrayDrag(source, _fromBar);
            ok = true;
        }
        finally
        {
            if (!ok)
            {
                // 생성 중 예외 → 끌기 상태가 남지 않게 되돌림 (IsActive 가 true 로 남으면 트레이 갱신이 멈춤)
                IsActive = false;
                source.Opacity = 1.0;
                try { _ghost.Close(); } catch { }
                foreach (var bar in TopBarWindow.TrayHosts)
                {
                    try { bar.EndTrayDrag(); } catch { }
                }
            }
        }
    }

    public void Move(Point screenPx)
    {
        _ghost.MoveCenter(screenPx);
        TopBarWindow? hitBar = null;
        TrayDropHit hit = default;
        foreach (var bar in TopBarWindow.TrayHosts)
        {
            var h = bar.HitTestTrayDrop(screenPx, _source);
            if (h.Kind == TrayDropKind.None) continue;
            hitBar = bar;
            hit = h;
            break;
        }
        if (hitBar != _hoverBar) _hoverBar?.PreviewTrayDrop(_source, default, _fromBar);
        _hoverBar = hitBar;
        _hit = hit;
        hitBar?.PreviewTrayDrop(_source, hit, _fromBar);
    }

    /// <summary>놓기 (cancel 이면 그대로 되돌림).</summary>
    public void End(bool cancel)
    {
        IsActive = false;
        _ghost.Close();
        _source.Opacity = 1.0;
        foreach (var bar in TopBarWindow.TrayHosts) bar.EndTrayDrag();
        if (cancel || _hit.Kind == TrayDropKind.None) return;
        if (_hit.Kind == TrayDropKind.More && !_fromBar) return; // ⌃ 에서 꺼내 ⌃ 에 놓음 → 그대로

        try
        {
            var settings = _services.Settings.Current.TopBar;
            string key = TrayIconLayout.PlacementKey(_source.Info);
            bool onBar = _hit.Kind == TrayDropKind.Bar;
            if (_fromBar && onBar)
            {
                // 제자리에 놓음 → 저장하지 않음 (윈도우 설정을 계속 따르게)
                var bar = TrayIconLayout.Split(_services.TrayIcons.Icons, settings).OnBar;
                if (bar.FindIndex(i => i.Key == _source.Info.Key) == _hit.BarIndex) return;
            }
            TrayIconLayout.Move(_services.TrayIcons.Icons, settings, key, onBar, _hit.BarIndex);
            Log.Info($"트레이 아이콘 옮김: {key} → {(onBar ? $"상단바 {_hit.BarIndex + 1}번째" : "⌃ 안")}");
            _services.Settings.Save();
        }
        catch (Exception ex) { Log.Error("트레이 아이콘 옮기기 실패", ex); }
    }
}

/// <summary>끄는 동안 커서를 따라다니는 반투명 아이콘 복사본 (입력·활성화 없음). 위치는 물리 픽셀로 직접.</summary>
internal sealed class TrayDragGhostWindow : Window
{
    private readonly double _size;

    public TrayDragGhostWindow(AppServices services, ImageSource? icon, double size)
    {
        _size = size + 8;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Width = Height = _size;
        Title = "mongdock Tray Drag";
        var img = new Image { Source = icon, Width = size, Height = size, Stretch = Stretch.Uniform, Opacity = 0.6 };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        Content = new Grid { Children = { img } };
        SourceInitialized += (_, _) => services.DesktopWindows.MakeOverlay(this);
    }

    /// <summary>복사본 가운데가 화면 좌표(물리 픽셀) p 에 오도록.</summary>
    public void MoveCenter(Point p)
    {
        if (!IsVisible)
        {
            Left = -10000;
            Top = -10000;
            Show();
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int half = (int)Math.Round(_size * scale / 2);
        User32.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(p.X) - half, (int)Math.Round(p.Y) - half, 0, 0,
            User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }
}
