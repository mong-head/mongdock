using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// "아이콘 바꾸기" 카드 (#24, 루틴·독 폴더 공통): [자동] [기호] [그림 파일] 세 칸.
/// 기호 = 판 색 8가지 + 기호 30개(또는 "기본 그림" — 독 폴더는 색만 바꾸고 폴더 그림 유지) + 글자 1~2자.
/// 그림 파일 = PNG·ICO·JPG 를 몽독 아이콘 폴더에 복사(원본을 지워도 유지). 위에 미리 보기, [완료] 를 눌러야 바뀜.
/// </summary>
internal sealed class IconPickerWindow : Window
{
    private static IconPickerWindow? _open;

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly Func<PinIcon?, ImageSource?> _preview;
    private readonly Action<PinIcon?> _done;
    private readonly bool _keepDefaultGlyph;
    private readonly OutsideClickWatcher _watch;
    private PinIcon _icon;
    private bool _dialogOpen;

    private readonly Image _previewImage = new() { Width = 72, Height = 72, Margin = new Thickness(0, 4, 0, 12) };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel _glyphPage = new();
    private readonly StackPanel _filePage = new();
    private readonly StackPanel _autoPage = new();
    private readonly WrapPanel _colors = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
    private readonly WrapPanel _glyphs = new() { Width = 6 * 40, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBox _text = new() { MaxLength = 2, Width = 64, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 14 };
    private readonly TextBlock _fileName = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    /// <param name="keepDefaultGlyph">독 폴더: 기호 칸 맨 앞 "기본 그림"(폴더 + 종류 그림) — 색만 바꿀 수 있게.</param>
    /// <param name="autoText">[자동] 칸 설명 (예 "폴더 종류에 맞는 기본 그림을 써요.").</param>
    public static void Open(AppServices services, PinIcon? current, bool keepDefaultGlyph, string autoText, Func<PinIcon?, ImageSource?> preview, Action<PinIcon?> done)
    {
        _open?.Close();
        var w = new IconPickerWindow(services, current, keepDefaultGlyph, autoText, preview, done);
        _open = w;
        w.Closed += (_, _) => { if (_open == w) _open = null; };
        w.Show();
        w.Activate();
    }

    private IconPickerWindow(AppServices services, PinIcon? current, bool keepDefaultGlyph, string autoText, Func<PinIcon?, ImageSource?> preview, Action<PinIcon?> done)
    {
        _services = services;
        _p = UiTheme.Palette(services.Settings.Current);
        _preview = preview;
        _done = done;
        _keepDefaultGlyph = keepDefaultGlyph;
        _icon = Copy(current) ?? new PinIcon();
        _icon.Color ??= "mongdock";

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "mongdock Icon";
        AppIcon.Apply(this);
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 13;
        Foreground = _p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        var body = new StackPanel { Width = 280 };
        body.Children.Add(new TextBlock { Text = Loc.T("아이콘 바꾸기"), FontSize = 14, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 8) });
        body.Children.Add(_previewImage);
        body.Children.Add(_tabs);

        // 자동
        _autoPage.Children.Add(Muted(autoText));

        // 기호: 판 색 → 기호 칸 → 글자
        foreach (string c in PinIconRenderer.Colors) _colors.Children.Add(ColorSwatch(c));
        _glyphPage.Children.Add(_colors);
        if (keepDefaultGlyph) _glyphs.Children.Add(GlyphCell(null));
        foreach (string g in PinIconRenderer.Glyphs) _glyphs.Children.Add(GlyphCell(g));
        _glyphPage.Children.Add(_glyphs);
        var textRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        textRow.Children.Add(new TextBlock { Text = Loc.T("글자 (1~2자)"), Foreground = _p.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _text.Text = _icon.Text ?? "";
        _text.TextChanged += (_, _) =>
        {
            _icon.Text = string.IsNullOrWhiteSpace(_text.Text) ? null : _text.Text.Trim();
            if (_icon.Text is not null) _icon.Glyph = null;
            _icon.Mode = PinIconMode.Glyph;
            Refresh();
        };
        textRow.Children.Add(_text);
        _glyphPage.Children.Add(textRow);

        // 그림 파일
        var pick = MakeButton(Loc.T("그림 고르기…"), _p.Tile, _p.Text);
        pick.HorizontalAlignment = HorizontalAlignment.Center;
        pick.Padding = new Thickness(16, 0, 16, 0);
        pick.Click += (_, _) => PickFile();
        _filePage.Children.Add(pick);
        _filePage.Children.Add(_fileName);

        body.Children.Add(_autoPage);
        body.Children.Add(_glyphPage);
        body.Children.Add(_filePage);

        var buttons = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var cancel = MakeButton(Loc.T("취소"), _p.Tile, _p.Text);
        cancel.Click += (_, _) => Close();
        var ok = MakeButton(Loc.T("완료"), _p.Accent, _p.AccentText);
        ok.Click += (_, _) => Finish();
        Grid.SetColumn(ok, 2);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _p.CardBackground,
            BorderBrush = _p.CardBorder,
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(18, 16, 18, 16),
            Child = body,
        };
        var root = new Grid { Margin = new Thickness(24, 16, 24, 32) };
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _p.CardBackground,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = _p.ShadowOpacity + 0.08 },
        });
        root.Children.Add(card);
        Content = root;

        BuildTabs();
        Refresh();

        // 바깥을 누르면 취소 (파일 고르기 창이 열려 있는 동안은 빼고)
        _watch = new OutsideClickWatcher(services, () => new[] { OutsideClickWatcher.ScreenRect(card) }, () => { if (!_dialogOpen) Close(); })
        {
            CloseOnActivation = false,
        };
        Loaded += (_, _) =>
        {
            var screen = services.DesktopWindows.GetPrimaryScreenBounds();
            Left = Math.Round(screen.Left + (screen.Width - ActualWidth) / 2);
            Top = Math.Round(screen.Top + screen.Height * 0.30 - ActualHeight / 2);
            _watch.Start();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Finish(); }
        };
        Closed += (_, _) => _watch.Stop();
    }

    private static PinIcon? Copy(PinIcon? i) => i is null ? null : new PinIcon { Mode = i.Mode, Glyph = i.Glyph, Color = i.Color, Text = i.Text, File = i.File };

    private void Finish()
    {
        // 그림 파일 칸인데 파일을 안 골랐으면 자동으로
        PinIcon? result = _icon.Mode switch
        {
            PinIconMode.Auto => null,
            PinIconMode.File when string.IsNullOrEmpty(_icon.File) => null,
            PinIconMode.File => new PinIcon { Mode = PinIconMode.File, File = _icon.File },
            _ => new PinIcon { Mode = PinIconMode.Glyph, Color = _icon.Color, Glyph = _icon.Text is null ? _icon.Glyph : null, Text = _icon.Text },
        };
        Log.Info($"아이콘 바꾸기: {result?.Mode.ToString() ?? "자동"}");
        Close();
        try { _done(result); }
        catch (Exception ex) { Log.Error("아이콘 바꾸기 적용 실패", ex); }
    }

    private void PickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("그림 고르기…"),
            Filter = Loc.T("그림") + " (*.png;*.ico;*.jpg;*.jpeg;*.bmp)|*.png;*.ico;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
        };
        _dialogOpen = true;
        bool ok;
        try { ok = dialog.ShowDialog(this) == true; }
        finally { _dialogOpen = false; }
        if (!ok) return;
        try
        {
            _icon.File = _services.Settings.ImportIcon(dialog.FileName);
            _icon.Mode = PinIconMode.File;
        }
        catch (Exception ex)
        {
            Log.Error("아이콘 그림 복사 실패", ex);
        }
        Refresh();
    }

    // ───────────────────────── 칸 ─────────────────────────

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var (mode, label) in new[] { (PinIconMode.Auto, Loc.T("자동")), (PinIconMode.Glyph, Loc.T("기호")), (PinIconMode.File, Loc.T("그림 파일")) })
        {
            bool on = _icon.Mode == mode;
            var tab = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(2, 0, 2, 0),
                Background = on ? _p.Accent : _p.Tile,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = label, Foreground = on ? _p.AccentText : _p.Text, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal },
            };
            tab.MouseLeftButtonUp += (_, _) => { _icon.Mode = mode; Refresh(); };
            _tabs.Children.Add(tab);
        }
    }

    private void Refresh()
    {
        BuildTabs();
        _autoPage.Visibility = _icon.Mode == PinIconMode.Auto ? Visibility.Visible : Visibility.Collapsed;
        _glyphPage.Visibility = _icon.Mode == PinIconMode.Glyph ? Visibility.Visible : Visibility.Collapsed;
        _filePage.Visibility = _icon.Mode == PinIconMode.File ? Visibility.Visible : Visibility.Collapsed;
        _fileName.Text = string.IsNullOrEmpty(_icon.File) ? Loc.T("PNG·ICO·JPG 그림을 골라 주세요. 몽독 폴더에 복사해 둬요.") : System.IO.Path.GetFileName(_icon.File);
        _fileName.Foreground = _p.SubText;

        foreach (Border sw in _colors.Children) sw.BorderBrush = (string)sw.Tag == _icon.Color ? _p.Text : Brushes.Transparent;
        foreach (Border cell in _glyphs.Children)
        {
            bool on = _icon.Text is null && (string?)cell.Tag == _icon.Glyph;
            cell.Background = on ? _p.Hover : Brushes.Transparent;
            cell.BorderBrush = on ? _p.Accent : Brushes.Transparent;
        }

        PinIcon? candidate = _icon.Mode switch
        {
            PinIconMode.Auto => null,
            PinIconMode.File when string.IsNullOrEmpty(_icon.File) => null,
            _ => _icon,
        };
        try { _previewImage.Source = _preview(candidate is null ? null : Copy(candidate)); }
        catch (Exception ex) { Log.Error("아이콘 미리 보기 실패", ex); }
    }

    private Border ColorSwatch(string name)
    {
        var (top, bottom) = PinIconRenderer.PlateColors(name);
        var sw = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Margin = new Thickness(3),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(2),
            Tag = name,
            Cursor = Cursors.Hand,
            Child = new Border { CornerRadius = new CornerRadius(9), Background = new LinearGradientBrush(top, bottom, 75) },
        };
        sw.MouseLeftButtonUp += (_, _) => { _icon.Color = name; _icon.Mode = PinIconMode.Glyph; Refresh(); };
        return sw;
    }

    /// <summary>기호 칸. glyph null = "기본 그림"(독 폴더의 폴더 + 종류 그림).</summary>
    private Border GlyphCell(string? glyph)
    {
        var cell = new Border
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5),
            Tag = glyph,
            Cursor = Cursors.Hand,
            ToolTip = glyph is null ? Loc.T("기본 그림") : null,
            Child = new TextBlock
            {
                Text = glyph ?? "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = glyph is null ? _p.SubText : _p.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        cell.MouseLeftButtonUp += (_, _) =>
        {
            _icon.Glyph = glyph;
            _icon.Text = null;
            _text.Text = "";
            _icon.Mode = PinIconMode.Glyph;
            Refresh();
        };
        return cell;
    }

    private TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = _p.SubText,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button MakeButton(string text, Brush background, Brush foreground) => new()
    {
        Style = (Style)Application.Current.FindResource("CardButton"),
        Content = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold },
        Background = background,
        Foreground = foreground,
        Height = 30,
    };
}
