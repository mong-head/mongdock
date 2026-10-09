using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 맥 대화상자 같은 확인 카드 (다시 시작·시스템 종료·로그아웃 전).
/// 포커스를 뺏지 않는 창 + 마우스만으로 [취소]/[확인]. 바깥 클릭이면 취소로 닫힌다.
/// 다른 창 활성화로는 닫지 않는다 — 백그라운드 프로그램이 잠깐 포그라운드를 가져가도(알림·콘솔 깜빡임) 카드가 사라져
/// [확인]이 안 눌리는 일이 있었음(#6 QA). 닫힌 이유는 로그에 남김.
/// </summary>
internal sealed class ConfirmCardWindow : Window
{
    private readonly OutsideClickWatcher _watch;
    private readonly Border _card;
    private Action? _onConfirm;
    private Action? _onCancel;
    /// <summary>[취소] 버튼으로 닫힘 (바깥 클릭·다른 창 활성화와 구분).</summary>
    private bool _cancelClicked;
    /// <summary>아래 작은 글자 버튼(예: "다시 묻지 않기")으로 닫힘.</summary>
    private bool _extraClicked;

    public ConfirmCardWindow(AppServices services, UiPalette p, string title, string message, string confirmText, Action onConfirm,
        string? cancelText = null, string? extraText = null)
    {
        _onConfirm = onConfirm;
        cancelText ??= Loc.T("취소");
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
        Title = "mongdock Confirm";
        AppIcon.Apply(this);
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 13;
        Foreground = p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        var body = new StackPanel { Width = 260 };
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 6),
        });
        body.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = p.SubText,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var cancel = MakeButton(cancelText, p.Tile, p.Text);
        cancel.Click += (_, _) =>
        {
            Log.Info($"확인 카드 '{title}': {cancelText}");
            _cancelClicked = true;
            Close();
        };
        var ok = MakeButton(confirmText, p.Accent, p.AccentText);
        ok.Click += (_, _) =>
        {
            Log.Info($"확인 카드 '{title}': {confirmText}");
            var action = _onConfirm;
            _onConfirm = null;
            Close();
            try { action?.Invoke(); }
            catch (Exception ex) { Log.Error($"'{confirmText}' 실행 실패", ex); }
        };
        Grid.SetColumn(ok, 2);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);
        if (extraText is not null)
        {
            var extra = new Button
            {
                Style = (Style)Application.Current.FindResource("CardLinkButton"),
                Content = new TextBlock { Text = extraText, FontSize = 12, Foreground = p.SubText },
                Foreground = p.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, -6),
            };
            extra.Click += (_, _) =>
            {
                Log.Info($"확인 카드 '{title}': {extraText}");
                _extraClicked = true;
                Close();
            };
            body.Children.Add(extra);
        }

        _card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = p.CardBackground,
            BorderBrush = p.CardBorder,
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(18, 16, 18, 16),
            Child = body,
        };
        var root = new Grid { Margin = new Thickness(24, 16, 24, 32) };
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = p.CardBackground,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = p.ShadowOpacity + 0.08 },
        });
        root.Children.Add(_card);
        Content = root;

        _watch = new OutsideClickWatcher(services, () => new[] { OutsideClickWatcher.ScreenRect(_card) }, Close)
        {
            CloseOnActivation = false,
            LogName = Loc.F($"확인 카드 '{title}'"),
        };
        SourceInitialized += (_, _) => services.DesktopWindows.MakeOverlay(this);
        Loaded += (_, _) =>
        {
            // 주 화면 가운데 약간 위
            var screen = services.DesktopWindows.GetPrimaryScreenBounds();
            Left = Math.Round(screen.Left + (screen.Width - ActualWidth) / 2);
            Top = Math.Round(screen.Top + screen.Height * 0.32 - ActualHeight / 2);
            _watch.Start();
        };
        Closed += (_, _) =>
        {
            _watch.Stop();
            // 확인 없이 닫힘 (취소 버튼·바깥 클릭·다른 창 활성화)
            if (_onConfirm != null) _onCancel?.Invoke();
        };
    }

    private static Button MakeButton(string text, Brush background, Brush foreground) => new()
    {
        Style = (Style)Application.Current.FindResource("CardButton"),
        Content = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold },
        Background = background,
        Foreground = foreground,
        Height = 30,
    };

    /// <summary>
    /// 확인 카드를 띄우고 결과를 기다림: 확인 = true, [취소 버튼] = false, 바깥 클릭·다른 창 활성화 = null (고르지 않음 — 나중에 다시 물을 수 있음).
    /// </summary>
    public static Task<bool?> AskChoiceAsync(AppServices services, string title, string message, string confirmText, string cancelText, string? tag = null)
    {
        var tcs = new TaskCompletionSource<bool?>();
        var p = UiTheme.Palette(services.Settings.Current);
        var card = new ConfirmCardWindow(services, p, title, message, confirmText, () => tcs.TrySetResult(true), cancelText) { Tag = tag };
        card._onCancel = () => tcs.TrySetResult(card._cancelClicked ? false : null);
        card.Show();
        return tcs.Task;
    }

    /// <summary>tag 를 붙여 띄운 카드를 모두 닫음 (고르지 않음으로 끝남). 예: 체험이 끝나면 "N일 남았어요" 카드를 치움.</summary>
    public static void CloseTagged(string tag)
    {
        foreach (var w in Application.Current.Windows.OfType<ConfirmCardWindow>().Where(w => Equals(w.Tag, tag)).ToList())
            w.Close();
    }

    /// <summary>확인 · 취소 버튼 · 아래 작은 버튼(extraText) · 고르지 않고 닫힘.</summary>
    public enum Choice { Confirm, Cancel, Extra, Dismissed }

    /// <summary>버튼 둘 + 아래 작은 글자 버튼 하나("다시 묻지 않기" 등)인 카드.</summary>
    public static Task<Choice> AskWithExtraAsync(AppServices services, string title, string message, string confirmText, string cancelText, string extraText)
    {
        var tcs = new TaskCompletionSource<Choice>();
        var p = UiTheme.Palette(services.Settings.Current);
        var card = new ConfirmCardWindow(services, p, title, message, confirmText, () => tcs.TrySetResult(Choice.Confirm), cancelText, extraText);
        card._onCancel = () => tcs.TrySetResult(card._extraClicked ? Choice.Extra : card._cancelClicked ? Choice.Cancel : Choice.Dismissed);
        card.Show();
        return tcs.Task;
    }

    /// <summary>확인 카드를 띄우고 결과를 기다림 (확인 = true, 취소/바깥 클릭 = false).</summary>
    public static Task<bool> AskAsync(AppServices services, string title, string message, string confirmText)
    {
        var tcs = new TaskCompletionSource<bool>();
        var p = UiTheme.Palette(services.Settings.Current);
        var card = new ConfirmCardWindow(services, p, title, message, confirmText, () => tcs.TrySetResult(true));
        card._onCancel = () => tcs.TrySetResult(false);
        card.Show();
        return tcs.Task;
    }

    /// <summary>
    /// 창 N개 닫기 확인 (리뷰 M4): 닫을 창이 2개 이상이거나 다른 데스크톱 창이 섞여 있으면 확인 카드, 1개면 바로.
    /// </summary>
    public static void CloseWindows(AppServices services, string appName, IReadOnlyList<Models.AppWindowInfo> windows)
    {
        void Run()
        {
            foreach (var w in windows) services.Launcher.Close(w.Hwnd);
        }
        if (windows.Count == 0) return;
        if (windows.Count == 1 && windows[0].OnCurrentDesktop)
        {
            Run();
            return;
        }
        Ask(services, Loc.F($"{appName} 창 {windows.Count}개를 모두 닫을까요?"),
            windows.Any(w => !w.OnCurrentDesktop) ? Loc.T("다른 데스크톱에 있는 창도 함께 닫혀요.") : Loc.T("저장하지 않은 내용은 사라질 수 있어요."),
            Loc.T("닫기"), Run);
    }

    /// <summary>확인 카드를 띄움. 확인을 눌렀을 때만 onConfirm 실행.</summary>
    public static void Ask(AppServices services, string title, string message, string confirmText, Action onConfirm)
    {
        var p = UiTheme.Palette(services.Settings.Current);
        new ConfirmCardWindow(services, p, title, message, confirmText, onConfirm).Show();
    }
}
