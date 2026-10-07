using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

internal enum StatusPanelKind { Volume, Wifi, Bluetooth, ControlCenter }

/// <summary>
/// 상단바 상태 아이콘(Wi-Fi·사운드·블루투스·제어센터)을 눌렀을 때 아래로 뜨는 MyDockFinder/맥 스타일 카드.
/// 흰(다크면 짙은 회색) 카드, 반경 12, 그림자, 패딩 16, 섹션 사이 구분선.
/// 포커스를 뺏지 않는 NOACTIVATE 창이라 <see cref="OutsideClickWatcher"/> 로 바깥 클릭·다른 창 활성화 시 닫는다.
/// </summary>
internal sealed class StatusPanelWindow : Window
{
    private const string IconFontName = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly FontFamily IconFont = new(IconFontName);

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly Border _card;
    private readonly OutsideClickWatcher _watch;
    private readonly DispatcherTimer _mediaTimer;
    private readonly List<Action> _refreshers = new();
    private Rect _anchorRect;
    private bool _updating;

    public StatusPanelKind Kind { get; }

    public StatusPanelWindow(AppServices services, StatusPanelKind kind, UiPalette palette)
    {
        _services = services;
        _p = palette;
        Kind = kind;

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
        Title = "mongdock Status";
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 15;
        Foreground = _p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _card = new Border
        {
            Width = kind switch { StatusPanelKind.Volume => 352, StatusPanelKind.ControlCenter => 352, _ => 300 },
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0.75),
            Background = _p.CardBackground,
            BorderBrush = _p.CardBorder,
            Padding = kind == StatusPanelKind.ControlCenter ? new Thickness(12) : new Thickness(17, 15, 17, 10),
            Child = kind switch
            {
                StatusPanelKind.Volume => BuildVolume(),
                StatusPanelKind.Wifi => BuildWifi(),
                StatusPanelKind.Bluetooth => BuildBluetooth(),
                _ => BuildControlCenter(),
            },
        };
        // 그림자는 뒤의 별도 Border 에만 (글자 흐려짐 방지)
        var root = new Grid { Margin = new Thickness(ShadowMargin, 2, ShadowMargin, 22) };
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _p.CardBackground,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = _p.ShadowOpacity },
        });
        root.Children.Add(_card);
        Content = root;

        _watch = new OutsideClickWatcher(services, InsideAreas, Close);
        _mediaTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _mediaTimer.Tick += (_, _) => RefreshAll();

        SourceInitialized += (_, _) => _services.DesktopWindows.MakeOverlay(this);
        Loaded += (_, _) =>
        {
            RefreshAll();
            _watch.Start();
            if (kind is StatusPanelKind.Volume or StatusPanelKind.ControlCenter) _mediaTimer.Start();
        };
        Closed += (_, _) =>
        {
            _watch.Stop();
            _mediaTimer.Stop();
            _services.Status.Changed -= OnChanged;
            _services.Media.Changed -= OnChanged;
        };
        _services.Status.Changed += OnChanged;
        _services.Media.Changed += OnChanged;
    }

    private const double ShadowMargin = 18;

    /// <summary>아이콘 아래(상단바와 6px 간격)에 아이콘 왼쪽 정렬로 표시. 화면을 넘으면 오른쪽 끝에 맞춤.</summary>
    public void ShowBelow(Rect anchor, double barBottom)
    {
        _anchorRect = anchor;
        double cardWidth = _card.Width;
        var screen = _services.DesktopWindows.GetPrimaryScreenBounds();
        double cardLeft = anchor.Left - 4;
        if (cardLeft + cardWidth > screen.Right - 6) cardLeft = screen.Right - 6 - cardWidth;
        cardLeft = Math.Max(screen.Left + 6, cardLeft);
        Left = Math.Round(cardLeft - ShadowMargin);
        Top = Math.Round(barBottom + 6 - 2);
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

    private void OnChanged(object? sender, EventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        _updating = true;
        try
        {
            foreach (var r in _refreshers) r();
        }
        catch (Exception ex)
        {
            Log.Error("상태 패널 갱신 실패", ex);
        }
        finally
        {
            _updating = false;
        }
    }

    // ───────────────────────── 공통 조각 ─────────────────────────

    private TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock Sub(string text, double size = 14) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = _p.SubText,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private UIElement Divider() => new Border
    {
        Height = 1,
        Background = _p.Divider,
        Margin = new Thickness(0, 9, 0, 7),
        SnapsToDevicePixels = true,
    };

    /// <summary>제목 + 오른쪽 요소 한 줄.</summary>
    private DockPanel HeaderRow(string title, UIElement? right)
    {
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 2), MinHeight = 26 };
        row.Children.Add(Heading(title));
        if (right != null)
        {
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
        }
        return row;
    }

    /// <summary>"네트워크 설정…" 같은 텍스트 행 버튼 (호버 시 옅은 배경).</summary>
    private Button LinkRow(string text, Action action, bool closeAfter = true)
    {
        var b = new Button
        {
            Style = (Style)FindStyle("CardLinkButton"),
            Content = new TextBlock { Text = text, FontSize = 15 },
            Foreground = _p.Text,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(-6, 0, -6, 0),
        };
        b.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"상태 패널 '{text}' 실패", ex); }
            if (closeAfter) Close();
        };
        return b;
    }

    private object FindStyle(string key)
        => TryFindResource(key) ?? Application.Current?.TryFindResource(key) ?? new Style(typeof(Button));

    /// <summary>원형 아이콘 (켜짐 = 파란 원 + 흰 글리프, 꺼짐 = 회색 원).</summary>
    private Grid Circle(string glyph, bool on, double size = 30, double glyphSize = 15)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Fill = on ? _p.Accent : _p.CircleOff });
        g.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = glyphSize,
            Foreground = on ? _p.AccentText : _p.CircleOffGlyph,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return g;
    }

    private void SetCircle(Grid circle, string glyph, bool on)
    {
        ((Ellipse)circle.Children[0]).Fill = on ? _p.Accent : _p.CircleOff;
        var t = (TextBlock)circle.Children[1];
        t.Text = glyph;
        t.Foreground = on ? _p.AccentText : _p.CircleOffGlyph;
    }

    /// <summary>Wi-Fi 끄기는 원격 연결이 끊길 수 있으므로 확인 후에만 (리뷰 H1). 켜기는 즉시. 취소면 null.</summary>
    private async Task<bool?> SetWifiConfirmed(bool on)
    {
        if (!on)
        {
            bool ok = await ConfirmCardWindow.AskAsync(_services, "Wi-Fi 를 끌까요?", "원격 연결이 끊길 수 있어요.", "끄기");
            if (!ok) return null;
        }
        return await _services.Status.SetWifiAsync(on);
    }

    private ToggleButton Switch(Func<bool, Task<bool>> set, TextBlock? errorLine)
        => Switch(async on => (bool?)await set(on), errorLine);

    /// <summary>set 결과: true 성공, false 실패(되돌리고 안내), null 취소(조용히 되돌림).</summary>
    private ToggleButton Switch(Func<bool, Task<bool?>> set, TextBlock? errorLine)
    {
        var sw = new ToggleButton
        {
            Style = (Style)FindStyle("MacSwitch"),
            Background = _p.Accent,
            BorderBrush = _p.CircleOff,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sw.Click += async (_, _) =>
        {
            if (_updating) return;
            bool wanted = sw.IsChecked == true;
            sw.IsEnabled = false;
            if (errorLine != null) errorLine.Visibility = Visibility.Collapsed;
            bool? ok;
            try { ok = await set(wanted); }
            catch (Exception ex)
            {
                Log.Error("상태 토글 실패", ex);
                ok = false;
            }
            if (!IsLoaded) return;
            sw.IsEnabled = true;
            if (ok != true)
            {
                _updating = true;
                sw.IsChecked = !wanted; // 되돌림
                _updating = false;
                if (ok == false && errorLine != null)
                {
                    errorLine.Text = "여기서 바꿀 수 없어요. 설정에서 바꿔 주세요.";
                    errorLine.Visibility = Visibility.Visible;
                }
            }
            RefreshAll();
        };
        return sw;
    }

    private TextBlock ErrorLine() => new()
    {
        Foreground = Converters.BrushParser.Frozen(Converters.BrushParser.Hex("#FFFF3B30")),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 4, 0, 0),
    };

    /// <summary>장치/네트워크 목록의 한 줄 (원형 아이콘 + 이름 [+ 보조 글]), 클릭 가능.</summary>
    private Button DeviceRow(string glyph, bool on, string name, string? sub, Action? onClick)
    {
        var dock = new DockPanel { LastChildFill = true };
        var circle = Circle(glyph, on);
        circle.Margin = new Thickness(0, 0, 10, 0);
        dock.Children.Add(circle);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(sub)) texts.Children.Add(Sub(sub, 12.5));
        dock.Children.Add(texts);

        var b = new Button
        {
            Style = (Style)FindStyle("CardLinkButton"),
            Content = dock,
            Foreground = _p.Text,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(-6, 0, -6, 0),
            IsHitTestVisible = onClick != null,
        };
        if (onClick != null)
        {
            b.Click += (_, _) =>
            {
                try { onClick(); }
                catch (Exception ex) { Log.Error("장치 선택 실패", ex); }
                RefreshAll();
            };
        }
        return b;
    }

    // ───────────────────────── Wi-Fi ─────────────────────────

    private UIElement BuildWifi()
    {
        var st = _services.Status;
        var root = new StackPanel();
        var error = ErrorLine();
        var sw = Switch(SetWifiConfirmed, error);
        root.Children.Add(HeaderRow("Wi-Fi", sw));
        root.Children.Add(error);
        root.Children.Add(Divider());

        var current = new StackPanel();
        var currentTitle = Sub("현재 네트워크");
        currentTitle.Margin = new Thickness(0, 0, 0, 6);
        current.Children.Add(currentTitle);
        var netRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var circle = Circle("", true);
        circle.Margin = new Thickness(0, 0, 10, 0);
        netRow.Children.Add(circle);
        var lockIcon = new TextBlock
        {
            Text = "",
            FontFamily = IconFont,
            FontSize = 13,
            Foreground = _p.SubText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(lockIcon, Dock.Right);
        netRow.Children.Add(lockIcon);
        var ssid = new TextBlock { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        netRow.Children.Add(ssid);
        current.Children.Add(netRow);
        var ipRow = new DockPanel { LastChildFill = false };
        var ip = Sub("");
        var speed = Sub("");
        DockPanel.SetDock(speed, Dock.Right);
        ipRow.Children.Add(ip);
        ipRow.Children.Add(speed);
        current.Children.Add(ipRow);
        current.Children.Add(Divider());
        root.Children.Add(current);

        var others = LinkRow("다른 네트워크", () => st.OpenAvailableNetworks());
        root.Children.Add(others);
        var othersDivider = Divider();
        root.Children.Add(othersDivider);
        root.Children.Add(LinkRow("네트워크 설정…", () => st.OpenWifiSettings()));

        _refreshers.Add(() =>
        {
            bool? radio = st.WifiRadioOn;
            sw.IsChecked = radio == true || (radio == null && st.Wifi == WifiState.Connected);
            sw.IsEnabled = radio != null;
            bool ethernet = st.Wifi == WifiState.Ethernet;
            bool connected = st.Wifi == WifiState.Connected || ethernet;
            current.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            SetCircle(circle, ethernet ? "" : "", true);
            ssid.Text = ethernet ? "유선 연결" : st.WifiName ?? "연결됨";
            lockIcon.Visibility = ethernet ? Visibility.Collapsed : Visibility.Visible;
            ip.Text = st.IpAddress ?? "";
            speed.Text = FormatLinkSpeed(st.LinkSpeedBps);
            bool radioOn = radio != false;
            others.Visibility = radioOn && !ethernet ? Visibility.Visible : Visibility.Collapsed;
            othersDivider.Visibility = others.Visibility;
        });
        return root;
    }

    private static string FormatLinkSpeed(long bps)
    {
        if (bps <= 0) return "";
        double mbps = bps / 1_000_000.0;
        return mbps >= 1000
            ? (mbps / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " Gbps"
            : mbps.ToString("0", CultureInfo.InvariantCulture) + " Mbps";
    }

    // ───────────────────────── 사운드 ─────────────────────────

    private UIElement BuildVolume()
    {
        var st = _services.Status;
        var root = new StackPanel();
        root.Children.Add(HeaderRow("사운드", null));
        var slider = new PillSlider(_p) { Margin = new Thickness(0, 6, 0, 2) };
        slider.UserChanged += (_, v) => SetVolume(v);
        root.Children.Add(slider);
        root.Children.Add(Divider());

        var devTitle = Sub("출력 장치");
        devTitle.Margin = new Thickness(0, 0, 0, 4);
        root.Children.Add(devTitle);
        var devices = new StackPanel();
        root.Children.Add(devices);

        var media = BuildMediaSection();
        root.Children.Add(media);

        root.Children.Add(Divider());
        root.Children.Add(LinkRow("사운드 설정…", () => st.OpenSoundSettings()));

        string signature = "";
        _refreshers.Add(() =>
        {
            if (!slider.IsDragging) slider.Value = st.Muted ? 0 : st.Volume;
            var list = st.OutputDevices ?? Array.Empty<AudioDevice>();
            string sig = string.Join("|", list.Select(d => $"{d.Id}:{d.IsDefault}:{d.Name}"));
            if (sig == signature) return;
            signature = sig;
            devices.Children.Clear();
            foreach (var d in list)
            {
                var id = d.Id;
                devices.Children.Add(DeviceRow(DeviceGlyph(d.Kind), d.IsDefault, d.Name, null,
                    d.IsDefault ? null : () => st.SetDefaultOutput(id)));
            }
            if (list.Count == 0) devices.Children.Add(Sub("재생 장치 없음"));
        });
        return root;
    }

    private void SetVolume(double v)
    {
        try
        {
            var st = _services.Status;
            if (st.Muted && v > 0) st.SetMuted(false);
            st.SetVolume(v);
        }
        catch (Exception ex) { Log.Error("볼륨 설정 실패", ex); }
    }

    private static string DeviceGlyph(AudioDeviceKind kind) => kind switch
    {
        AudioDeviceKind.Headphones => "",
        AudioDeviceKind.Display => "",
        AudioDeviceKind.Digital => "",
        _ => "",
    };

    /// <summary>지금 재생 중: 썸네일 40 + 제목 2줄 + 아티스트, 진행 바 + 시간, ◀◀ ▶/❚❚ ▶▶. 세션 없으면 숨김.</summary>
    private UIElement BuildMediaSection()
    {
        var m = _services.Media;
        var section = new StackPanel();
        section.Children.Add(Divider());

        var top = new DockPanel { LastChildFill = true };
        var thumb = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(7),
            Background = _p.Tile,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        top.Children.Add(thumb);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 42,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        var artist = Sub("", 13);
        texts.Children.Add(title);
        texts.Children.Add(artist);
        top.Children.Add(texts);
        section.Children.Add(top);

        var barGrid = new Grid { Height = 3, Margin = new Thickness(0, 10, 0, 4) };
        barGrid.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = _p.SliderTrack });
        var barFill = new Border { CornerRadius = new CornerRadius(1.5), Background = _p.Text, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        barGrid.Children.Add(barFill);
        section.Children.Add(barGrid);
        var times = new DockPanel { LastChildFill = false };
        var pos = Sub("", 11);
        var dur = Sub("", 11);
        DockPanel.SetDock(dur, Dock.Right);
        times.Children.Add(pos);
        times.Children.Add(dur);
        section.Children.Add(times);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        var prev = MediaButton(MediaGlyph.Previous, () => m.PreviousAsync());
        var play = MediaButton(MediaGlyph.Play, () => m.PlayPauseAsync(), big: true);
        var next = MediaButton(MediaGlyph.Next, () => m.NextAsync());
        buttons.Children.Add(prev);
        buttons.Children.Add(play);
        buttons.Children.Add(next);
        section.Children.Add(buttons);

        _refreshers.Add(() =>
        {
            section.Visibility = m.HasSession ? Visibility.Visible : Visibility.Collapsed;
            if (!m.HasSession) return;
            title.Text = m.Title ?? "";
            artist.Text = m.Artist ?? "";
            artist.Visibility = string.IsNullOrEmpty(m.Artist) ? Visibility.Collapsed : Visibility.Visible;
            thumb.Background = m.Thumbnail != null ? new ImageBrush(m.Thumbnail) { Stretch = Stretch.UniformToFill } : _p.Tile;
            double total = m.Duration.TotalSeconds;
            double ratio = total > 0 ? Math.Clamp(m.Position.TotalSeconds / total, 0, 1) : 0;
            barFill.Width = Math.Max(0, (barGrid.ActualWidth > 0 ? barGrid.ActualWidth : _card.Width - 32) * ratio);
            pos.Text = FormatTime(m.Position);
            dur.Text = total > 0 ? FormatTime(m.Duration) : "";
            times.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            barGrid.Visibility = times.Visibility;
            SetMediaGlyph(play, m.IsPlaying ? MediaGlyph.Pause : MediaGlyph.Play);
        });
        return section;
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private enum MediaGlyph { Previous, Play, Pause, Next }

    /// <summary>◀◀ ▶ ❚❚ ▶▶ 를 채운 도형으로 (MyDockFinder 처럼 굵게).</summary>
    private static Geometry MediaGeometry(MediaGlyph g) => g switch
    {
        MediaGlyph.Previous => Geometry.Parse("M10,0 L10,12 L0,6 Z M20,0 L20,12 L10,6 Z"),
        MediaGlyph.Next => Geometry.Parse("M0,0 L10,6 L0,12 Z M10,0 L20,6 L10,12 Z"),
        MediaGlyph.Pause => Geometry.Parse("M0,0 L4,0 L4,14 L0,14 Z M8,0 L12,0 L12,14 L8,14 Z"),
        _ => Geometry.Parse("M0,0 L12,7 L0,14 Z"),
    };

    private Button MediaButton(MediaGlyph glyph, Func<Task> action, bool big = false)
    {
        var path = new Path
        {
            Data = MediaGeometry(glyph),
            Fill = _p.Text,
            Width = big ? 16 : 20,
            Height = big ? 18 : 12,
            Stretch = Stretch.Uniform,
        };
        var b = new Button
        {
            Style = (Style)FindStyle("CardButton"),
            Background = Brushes.Transparent,
            Foreground = _p.Text,
            Content = path,
            Width = big ? 44 : 40,
            Height = 34,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 6, 0),
        };
        b.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { Log.Error("미디어 제어 실패", ex); }
        };
        return b;
    }

    private static void SetMediaGlyph(Button b, MediaGlyph glyph)
    {
        if (b.Content is Path p) p.Data = MediaGeometry(glyph);
    }

    // ───────────────────────── 블루투스 ─────────────────────────

    private UIElement BuildBluetooth()
    {
        var st = _services.Status;
        var root = new StackPanel();
        var error = ErrorLine();
        var sw = Switch(on => st.SetBluetoothAsync(on), error);
        root.Children.Add(HeaderRow("블루투스", sw));
        root.Children.Add(error);
        root.Children.Add(Divider());
        var devTitle = Sub("기기");
        devTitle.Margin = new Thickness(0, 0, 0, 4);
        root.Children.Add(devTitle);
        var devices = new StackPanel();
        root.Children.Add(devices);
        root.Children.Add(Divider());
        root.Children.Add(LinkRow("블루투스 설정…", () => st.OpenBluetoothSettings()));

        string signature = "";
        _refreshers.Add(() =>
        {
            sw.IsChecked = st.BluetoothOn == true;
            sw.IsEnabled = st.BluetoothOn != null;
            var list = st.BluetoothOn == true ? st.BluetoothDevices ?? Array.Empty<BluetoothDeviceInfo>() : Array.Empty<BluetoothDeviceInfo>();
            string sig = (st.BluetoothOn?.ToString() ?? "null") + string.Join("|", list.Select(d => $"{d.Id}:{d.Connected}:{d.Name}"));
            if (sig == signature) return;
            signature = sig;
            devices.Children.Clear();
            foreach (var d in list.OrderByDescending(d => d.Connected))
                devices.Children.Add(DeviceRow("", d.Connected, d.Name, d.Connected ? "연결됨" : null, null));
            if (list.Count == 0)
                devices.Children.Add(Sub(st.BluetoothOn switch { true => "페어링된 기기 없음", false => "블루투스가 꺼져 있음", null => "어댑터를 찾을 수 없음" }));
        });
        return root;
    }

    // ───────────────────────── 제어 센터 ─────────────────────────

    private Border Tile(UIElement child, Thickness? padding = null) => new()
    {
        CornerRadius = new CornerRadius(12),
        Background = _p.Tile,
        Padding = padding ?? new Thickness(12),
        Child = child,
    };

    /// <summary>원형 토글 버튼 + 제목 + 상태 (큰 타일 안 한 줄).</summary>
    private (Button Button, Grid Circle, TextBlock State) ToggleLine(string glyph, string label, Func<Task> onClick)
    {
        var circle = Circle(glyph, false, 30, 15);
        var b = new Button
        {
            Style = (Style)FindStyle("CardButton"),
            Background = Brushes.Transparent,
            Foreground = _p.Text,
            Padding = new Thickness(0),
            Content = circle,
            Margin = new Thickness(0, 0, 10, 0),
        };
        b.Click += async (_, _) =>
        {
            try { await onClick(); }
            catch (Exception ex) { Log.Error($"제어 센터 '{label}' 실패", ex); }
            RefreshAll();
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = label, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        var state = Sub("", 11.5);
        texts.Children.Add(state);
        var row = new DockPanel { LastChildFill = true };
        row.Children.Add(b);
        row.Children.Add(texts);
        return (b, circle, state);
    }

    private Button SmallTile(string glyph, string label, Action action)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 5),
        });
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var b = new Button
        {
            Style = (Style)FindStyle("CardButton"),
            Background = Brushes.Transparent,
            Foreground = _p.Text,
            Padding = new Thickness(6),
            Content = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        b.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"제어 센터 '{label}' 실패", ex); }
            Close();
        };
        return b;
    }

    private UIElement BuildControlCenter()
    {
        var st = _services.Status;
        var m = _services.Media;
        var root = new StackPanel();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 왼쪽 큰 타일: Wi-Fi / 블루투스
        var big = new StackPanel();
        var wifi = ToggleLine("", "Wi-Fi", async () =>
        {
            bool on = st.WifiRadioOn ?? st.Wifi == WifiState.Connected;
            await SetWifiConfirmed(!on);
        });
        var bt = ToggleLine("", "블루투스", async () =>
        {
            if (st.BluetoothOn is bool on) await st.SetBluetoothAsync(!on);
        });
        ((FrameworkElement)wifi.Button.Parent).Margin = new Thickness(0, 0, 0, 12);
        big.Children.Add((UIElement)wifi.Button.Parent);
        big.Children.Add((UIElement)bt.Button.Parent);
        var bigTile = Tile(big);
        grid.Children.Add(bigTile);

        // 오른쪽 작은 타일 둘
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        right.RowDefinitions.Add(new RowDefinition());
        var quick = Tile(SmallTile("", "Windows 빠른 설정", () => _services.Shell.OpenQuickSettings()), new Thickness(2));
        var settings = Tile(SmallTile("", "설정", () => _services.Shell.OpenSettings()), new Thickness(2));
        Grid.SetRow(settings, 2);
        right.Children.Add(quick);
        right.Children.Add(settings);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        root.Children.Add(grid);

        // 사운드 타일
        var soundStack = new StackPanel();
        var soundTitle = Heading("사운드");
        soundTitle.FontSize = 13.5;
        soundTitle.Margin = new Thickness(0, 0, 0, 8);
        soundStack.Children.Add(soundTitle);
        var slider = new PillSlider(_p);
        slider.UserChanged += (_, v) => SetVolume(v);
        soundStack.Children.Add(slider);
        var soundTile = Tile(soundStack);
        soundTile.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(soundTile);

        // 지금 재생 중 타일
        var mediaRow = new DockPanel { LastChildFill = true };
        var thumb = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(6), Background = _p.CircleOff, Margin = new Thickness(0, 0, 12, 0) };
        mediaRow.Children.Add(thumb);
        var nextBtn = MediaButton(MediaGlyph.Next, () => m.NextAsync());
        var playBtn = MediaButton(MediaGlyph.Play, () => m.PlayPauseAsync(), big: true);
        nextBtn.Margin = playBtn.Margin = new Thickness(0);
        DockPanel.SetDock(nextBtn, Dock.Right);
        DockPanel.SetDock(playBtn, Dock.Right);
        mediaRow.Children.Add(nextBtn);
        mediaRow.Children.Add(playBtn);
        var mediaTitle = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        mediaRow.Children.Add(mediaTitle);
        var mediaTile = Tile(mediaRow, new Thickness(12, 9, 8, 9));
        mediaTile.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(mediaTile);

        _refreshers.Add(() =>
        {
            bool wifiOn = st.WifiRadioOn ?? st.Wifi == WifiState.Connected;
            bool ethernet = st.Wifi == WifiState.Ethernet;
            SetCircle(wifi.Circle, ethernet ? "" : "", wifiOn || ethernet);
            wifi.State.Text = ethernet ? "유선 연결" : st.Wifi == WifiState.Connected ? st.WifiName ?? "연결됨" : wifiOn ? "연결 안 됨" : "끔";
            wifi.Button.IsEnabled = st.WifiRadioOn != null;
            SetCircle(bt.Circle, "", st.BluetoothOn == true);
            bt.State.Text = st.BluetoothOn switch { true => "켬", false => "끔", null => "없음" };
            bt.Button.IsEnabled = st.BluetoothOn != null;
            if (!slider.IsDragging) slider.Value = st.Muted ? 0 : st.Volume;

            mediaTile.Visibility = m.HasSession ? Visibility.Visible : Visibility.Collapsed;
            if (m.HasSession)
            {
                mediaTitle.Text = string.IsNullOrEmpty(m.Title) ? "재생 중" : m.Title;
                thumb.Background = m.Thumbnail != null ? new ImageBrush(m.Thumbnail) { Stretch = Stretch.UniformToFill } : _p.CircleOff;
                SetMediaGlyph(playBtn, m.IsPlaying ? MediaGlyph.Pause : MediaGlyph.Play);
            }
        });
        return root;
    }
}
