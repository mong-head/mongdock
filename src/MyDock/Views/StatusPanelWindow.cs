using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MyDock.Converters;
using MyDock.Services;

namespace MyDock.Views;

internal enum StatusPanelKind { Volume, Wifi, Bluetooth }

/// <summary>
/// 상단바 상태 아이콘(볼륨·와이파이·블루투스)을 눌렀을 때 아래로 뜨는 작은 카드.
/// 포커스를 뺏지 않는 NOACTIVATE 창이라 일반 Popup 의 "바깥 클릭 시 닫힘"이 동작하지 않으므로,
/// <see cref="OutsideClickWatcher"/> 로 바깥 클릭·다른 창 활성화 시 닫는다 (메뉴와 같은 규칙).
/// </summary>
internal sealed class StatusPanelWindow : Window
{
    private readonly AppServices _services;
    private readonly Brush _fg, _fgDim, _accent, _track;
    private readonly Border _card;
    private readonly OutsideClickWatcher _watch;
    private Rect _anchorRect;      // 아이콘 버튼 화면 영역 (DIP)
    private bool _updating;

    // 볼륨
    private Slider? _slider;
    private Button? _muteButton;
    private TextBlock? _volumeText;
    // 와이파이
    private TextBlock? _wifiName, _wifiDetail;
    // 블루투스
    private ToggleButton? _btSwitch;
    private TextBlock? _btState, _btMessage;
    private bool _btBusy;

    public StatusPanelKind Kind { get; }

    public StatusPanelWindow(AppServices services, StatusPanelKind kind, bool light)
    {
        _services = services;
        Kind = kind;
        _fg = BrushParser.Frozen(light ? BrushParser.Hex("#FF1D1D1F") : BrushParser.Hex("#FFF2F2F2"));
        _fgDim = BrushParser.Frozen(light ? BrushParser.Hex("#99000000") : BrushParser.Hex("#99FFFFFF"));
        _accent = BrushParser.Frozen(BrushParser.Hex("#FF0A84FF"));
        _track = BrushParser.Frozen(light ? BrushParser.Hex("#33000000") : BrushParser.Hex("#40FFFFFF"));

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "MyDock Status";
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        FontSize = 13;
        Foreground = _fg;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _card = new Border
        {
            Width = 268,
            Margin = new Thickness(12, 4, 12, 14), // 그림자 여유
            Padding = new Thickness(14, 12, 14, 8),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Background = BrushParser.Frozen(light ? BrushParser.Hex("#F7F4F4F6") : BrushParser.Hex("#F52A2A2C")),
            BorderBrush = BrushParser.Frozen(light ? BrushParser.Hex("#1F000000") : BrushParser.Hex("#26FFFFFF")),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = light ? 0.18 : 0.4 },
            Child = kind switch
            {
                StatusPanelKind.Volume => BuildVolume(),
                StatusPanelKind.Wifi => BuildWifi(),
                _ => BuildBluetooth(),
            },
        };
        Content = _card;

        _watch = new OutsideClickWatcher(services, InsideAreas, Close);

        SourceInitialized += (_, _) => _services.DesktopWindows.MakeOverlay(this);
        Loaded += (_, _) => { Refresh(); _watch.Start(); };
        Closed += (_, _) =>
        {
            _watch.Stop();
            _services.Status.Changed -= OnStatusChanged;
        };
        _services.Status.Changed += OnStatusChanged;
    }

    /// <summary>아이콘(anchor, 화면 DIP) 아래에 오른쪽 맞춤으로 표시.</summary>
    public void ShowBelow(Rect anchor, double barBottom)
    {
        _anchorRect = anchor;
        // 표시 전 Measure 는 신뢰할 수 없으므로 고정 폭으로 계산 (카드 폭 + 그림자 여백)
        double width = _card.Width + _card.Margin.Left + _card.Margin.Right;
        var screen = _services.DesktopWindows.GetPrimaryScreenBounds();
        double left = anchor.Right + _card.Margin.Right + 6 - width; // 카드 오른쪽 끝 ≈ 아이콘 오른쪽 끝
        left = Math.Clamp(left, screen.Left, Math.Max(screen.Left, screen.Right - width));
        Left = left;
        Top = barBottom + 2;
        Show();
    }

    /// <summary>카드 + 이 패널을 연 아이콘 (아이콘을 다시 누르면 상단바가 토글로 닫음).</summary>
    private IEnumerable<Rect> InsideAreas()
    {
        var card = OutsideClickWatcher.ScreenRect(_card);
        card.Inflate(2, 2);
        var anchor = _anchorRect;
        anchor.Inflate(2, 2);
        return new[] { card, anchor };
    }

    private void OnStatusChanged(object? sender, EventArgs e) => Refresh();

    // ───────────────────────── 구성 ─────────────────────────

    private TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private UIElement Divider() => new Border
    {
        Height = 1,
        Background = _track,
        Opacity = 0.6,
        Margin = new Thickness(-14, 8, -14, 4),
    };

    private Button Link(string text, Action action)
    {
        var b = new Button
        {
            Style = (Style)FindResourceSafe("CardLinkButton"),
            Content = new TextBlock { Text = text },
            Foreground = _fg,
            Margin = new Thickness(-8, 0, -8, 0),
        };
        b.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"상태 패널 '{text}' 실패", ex); }
            Close();
        };
        return b;
    }

    private object FindResourceSafe(string key) => TryFindResource(key) ?? Application.Current?.TryFindResource(key) ?? new Style(typeof(Button));

    private UIElement BuildVolume()
    {
        var root = new StackPanel();
        var head = new DockPanel { LastChildFill = false };
        var title = Heading("소리");
        head.Children.Add(title);
        _volumeText = new TextBlock { Foreground = _fgDim, FontSize = 12 };
        DockPanel.SetDock(_volumeText, Dock.Right);
        head.Children.Add(_volumeText);
        root.Children.Add(head);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        _muteButton = new Button
        {
            Style = (Style)FindResourceSafe("CardRoundButton"),
            Margin = new Thickness(0, 0, 10, 0),
        };
        _muteButton.Click += (_, _) =>
        {
            try { _services.Status.SetMuted(!_services.Status.Muted); }
            catch (Exception ex) { Log.Error("음소거 전환 실패", ex); }
            Refresh();
        };
        DockPanel.SetDock(_muteButton, Dock.Left);
        row.Children.Add(_muteButton);

        _slider = new Slider
        {
            Style = (Style)FindResourceSafe("MacSlider"),
            Foreground = _accent,
            Background = _track,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _slider.ValueChanged += (_, e) =>
        {
            if (_updating) return;
            try { _services.Status.SetVolume(Math.Clamp(e.NewValue / 100.0, 0, 1)); }
            catch (Exception ex) { Log.Error("볼륨 설정 실패", ex); }
            UpdateVolumeLabels(e.NewValue / 100.0, _services.Status.Muted);
        };
        _slider.MouseWheel += (_, e) =>
        {
            _slider.Value = Math.Clamp(_slider.Value + Math.Sign(e.Delta) * 4, 0, 100);
            e.Handled = true;
        };
        row.Children.Add(_slider);
        root.Children.Add(row);

        root.Children.Add(Divider());
        root.Children.Add(Link("사운드 설정…", () => _services.Status.OpenSoundSettings()));
        return root;
    }

    private UIElement BuildWifi()
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Wi-Fi"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var circle = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
        circle.Children.Add(new System.Windows.Shapes.Ellipse { Fill = _accent });
        circle.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(circle);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _wifiName = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 };
        _wifiDetail = new TextBlock { Foreground = _fgDim, FontSize = 12 };
        texts.Children.Add(_wifiName);
        texts.Children.Add(_wifiDetail);
        row.Children.Add(texts);
        root.Children.Add(row);
        root.Children.Add(Divider());
        root.Children.Add(Link("Wi-Fi 설정…", () => _services.Status.OpenWifiSettings()));
        return root;
    }

    private UIElement BuildBluetooth()
    {
        var root = new StackPanel();
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        var title = Heading("블루투스");
        title.Margin = new Thickness(0);
        title.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(title);
        _btSwitch = new ToggleButton { Style = (Style)FindResourceSafe("MacSwitch"), Background = _accent };
        _btSwitch.Click += OnBluetoothToggle;
        DockPanel.SetDock(_btSwitch, Dock.Right);
        head.Children.Add(_btSwitch);
        root.Children.Add(head);

        _btState = new TextBlock { Foreground = _fgDim, FontSize = 12 };
        root.Children.Add(_btState);
        _btMessage = new TextBlock
        {
            Foreground = BrushParser.Frozen(BrushParser.Hex("#FFFF453A")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0),
        };
        root.Children.Add(_btMessage);
        root.Children.Add(Divider());
        root.Children.Add(Link("블루투스 설정…", () => _services.Status.OpenBluetoothSettings()));
        return root;
    }

    private async void OnBluetoothToggle(object sender, RoutedEventArgs e)
    {
        if (_btSwitch == null || _btBusy) return;
        bool wanted = _btSwitch.IsChecked == true;
        _btBusy = true;
        _btSwitch.IsEnabled = false;
        _btMessage!.Visibility = Visibility.Collapsed;
        bool ok;
        try { ok = await _services.Status.SetBluetoothAsync(wanted); }
        catch (Exception ex)
        {
            Log.Error("블루투스 전환 실패", ex);
            ok = false;
        }
        _btBusy = false;
        if (!IsLoaded) return;
        _btSwitch.IsEnabled = true;
        if (!ok)
        {
            _updating = true;
            _btSwitch.IsChecked = !wanted; // 되돌림
            _updating = false;
            _btMessage.Text = "여기서 바꿀 수 없어요. 설정에서 바꿔 주세요.";
            _btMessage.Visibility = Visibility.Visible;
        }
        Refresh();
    }

    // ───────────────────────── 값 반영 ─────────────────────────

    private void Refresh()
    {
        var st = _services.Status;
        _updating = true;
        try
        {
            switch (Kind)
            {
                case StatusPanelKind.Volume:
                    // 드래그 중에는 서비스 값으로 덮어쓰지 않음 (되튐 방지)
                    if (_slider != null && !_slider.IsMouseCaptureWithin) _slider.Value = Math.Round(st.Volume * 100);
                    UpdateVolumeLabels(st.Volume, st.Muted);
                    break;

                case StatusPanelKind.Wifi:
                    (_wifiName!.Text, _wifiDetail!.Text) = st.Wifi switch
                    {
                        WifiState.Connected => (st.WifiName ?? "연결됨", $"신호 {Math.Clamp(st.WifiSignal, 0, 100)}%"),
                        WifiState.Ethernet => ("유선 연결", "이더넷 사용 중"),
                        WifiState.Disconnected => ("연결 안 됨", "Wi-Fi 설정에서 네트워크를 고르세요"),
                        _ => ("알 수 없음", ""),
                    };
                    break;

                case StatusPanelKind.Bluetooth:
                    if (!_btBusy)
                    {
                        _btSwitch!.IsChecked = st.BluetoothOn == true;
                        _btSwitch.IsEnabled = st.BluetoothOn != null;
                    }
                    _btState!.Text = st.BluetoothOn switch { true => "켜짐", false => "꺼짐", null => "어댑터를 찾을 수 없음" };
                    break;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateVolumeLabels(double volume, bool muted)
    {
        if (_volumeText != null) _volumeText.Text = muted ? "음소거" : $"{Math.Round(volume * 100)}%";
        if (_muteButton != null)
        {
            _muteButton.Content = TopBarWindow.VolumeGlyph(volume, muted);
            _muteButton.Background = muted ? _track : _accent;
            _muteButton.Foreground = muted ? _fg : Brushes.White;
        }
    }
}
