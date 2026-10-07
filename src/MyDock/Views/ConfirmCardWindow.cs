using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 맥 대화상자 같은 확인 카드 (다시 시작·시스템 종료·로그아웃 전).
/// 포커스를 뺏지 않는 창 + 마우스만으로 [취소]/[확인]. 바깥 클릭·다른 창 활성화 시 취소로 닫힌다.
/// </summary>
internal sealed class ConfirmCardWindow : Window
{
    private readonly OutsideClickWatcher _watch;
    private readonly Border _card;
    private Action? _onConfirm;
    private Action? _onCancel;

    public ConfirmCardWindow(AppServices services, UiPalette p, string title, string message, string confirmText, Action onConfirm)
    {
        _onConfirm = onConfirm;
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
        Title = "MyDock Confirm";
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 13;
        Foreground = p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

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
        var cancel = MakeButton("취소", p.Tile, p.Text);
        cancel.Click += (_, _) => Close();
        var ok = MakeButton(confirmText, p.Accent, p.AccentText);
        ok.Click += (_, _) =>
        {
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

        _watch = new OutsideClickWatcher(services, () => new[] { OutsideClickWatcher.ScreenRect(_card) }, Close);
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
        Ask(services, $"{appName} 창 {windows.Count}개를 모두 닫을까요?",
            windows.Any(w => !w.OnCurrentDesktop) ? "다른 데스크톱에 있는 창도 함께 닫혀요." : "저장하지 않은 내용은 사라질 수 있어요.",
            "닫기", Run);
    }

    /// <summary>확인 카드를 띄움. 확인을 눌렀을 때만 onConfirm 실행.</summary>
    public static void Ask(AppServices services, string title, string message, string confirmText, Action onConfirm)
    {
        var p = UiTheme.Palette(services.Settings.Current);
        new ConfirmCardWindow(services, p, title, message, confirmText, onConfirm).Show();
    }
}
