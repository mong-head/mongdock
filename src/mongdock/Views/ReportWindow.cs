using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// "문제 신고하기…" 창 (설정 → 정보, 상단바 로고 메뉴 → mongdock ›).
/// 종류(버그/질문/제안) · 내용 · 답장 받을 이메일(선택) · 함께 보낼 정보(가린 결과 미리 보기) → <see cref="ReportService.SendAsync"/>.
/// 설정 창과 같은 맥 스타일 일반 창. 입력 칸 말고는 모두 클릭만으로. 한 개만 열림.
/// </summary>
public sealed class ReportWindow : Window
{
    private static ReportWindow? _instance;

    private static readonly Regex EmailPattern = new(@"^[^\s@<>()""',;]+@[^\s@<>()""',;]+\.[^\s@<>()""',;]+$", RegexOptions.CultureInvariant);

    private readonly UiPalette _p;
    private ReportKind _kind = ReportKind.Bug;
    private string _diagnostics = Loc.T("진단 정보를 모으는 중…");
    private bool _diagnosticsReady;
    private bool _detailsOpen;
    private bool _sending;
    private bool _closed;

    private readonly TextBox _title;
    private readonly TextBlock _titlePlaceholder;
    private readonly TextBox _message;
    private readonly TextBlock _messagePlaceholder;
    private readonly TextBlock _counter;
    private readonly TextBox _contact;
    private readonly TextBlock _contactPlaceholder;
    private readonly TextBlock _contactError;
    private readonly TextBox _details;
    private readonly Border _detailsHost;
    private readonly TextBlock _detailsChevron;
    private readonly TextBlock _status;
    private readonly Button _send;
    private readonly Button _cancel;
    private readonly Grid _form;
    private readonly StackPanel _done;
    private readonly List<(Button Button, ReportKind Kind)> _kindButtons = new();

    /// <summary>창이 열려 있는지 (열려 있으면 Open 의 prefill 은 무시됨).</summary>
    public static bool IsOpen => _instance is not null;

    /// <summary>미리 채울 내용 (오류 자동 신고 — Views/CrashPrompt). ErrorInfo 는 원문, 여기서 가린 뒤 진단 정보 맨 앞에 붙임.</summary>
    public sealed record Prefill(ReportKind Kind, string Title, string Message, string? ErrorInfo);

    /// <summary>창 열기. 이미 열려 있으면 앞으로 (prefill 은 새로 열 때만).</summary>
    public static void Open(AppServices services, Prefill? prefill = null)
    {
        try
        {
            if (_instance is { } w)
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
                return;
            }
            var settings = services.Settings.Current;
            var ctx = ReportRedactor.Context.Current(services.Windows.Windows.Select(x => x.Title));
            // 설정 요약은 지금 UI 스레드에서 (설정 객체는 UI 스레드에서 바뀜), 로그 읽기·가리기만 UI 밖에서
            string head = ReportService.BuildSystemInfo(settings, Monitors.GetAll(), TouchSupport.HasTouch, ctx);
            if (prefill?.ErrorInfo is { Length: > 0 } error)
            {
                // 예외 메시지·스택에도 경로·창 제목이 들어갈 수 있어 로그와 똑같이 줄마다 가림
                string redacted = string.Join("\n", error.Replace("\r\n", "\n").Split('\n').Select(l => ReportRedactor.RedactLog(l, ctx)));
                head = redacted.TrimEnd() + "\n\n" + head;
            }
            var win = new ReportWindow(settings);
            if (prefill is not null) win.SetPreviewState(prefill.Kind, prefill.Title, prefill.Message, "", detailsOpen: false);
            _instance = win;
            win.Show();
            win.Activate();
            Task.Run(() => ReportService.AppendLog(head, ctx))
                .ContinueWith(t => win.SetDiagnostics(t.IsCompletedSuccessfully ? t.Result : Loc.F($"(진단 정보를 모으지 못함: {t.Exception?.InnerException?.GetType().Name})")),
                    TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            Log.Error("문제 신고하기 창 열기 실패", ex);
        }
    }

    /// <param name="settings">테마(라이트/다크)만 씀.</param>
    public ReportWindow(Settings settings)
    {
        _p = UiTheme.Palette(settings);
        Title = Loc.T("문제 신고하기");
        AppIcon.Apply(this);
        var work = SystemParameters.WorkArea;
        Width = Math.Min(540, Math.Max(360, work.Width - 24));
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, work.Height - 24);
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 13;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Background = _p.WindowBackground;
        Foreground = _p.Text;
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        // "함께 보낼 정보"를 펼치면 창이 아래로 길어짐 → 화면 아래를 넘으면 위로 올리고, 화면보다 길면 높이를 막아 안에서 스크롤
        SizeChanged += (_, e) => { if (e.HeightChanged) KeepOnScreen(); };
        Closed += (_, _) =>
        {
            _closed = true;
            if (_instance == this) _instance = null;
        };

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        body.Children.Add(new TextBlock { Text = Loc.T("문제 신고하기"), FontSize = 20, FontWeight = FontWeights.Bold });
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("보낸 내용은 몽독 지원 메일함으로 가요. 메일 계정이 없어도 보낼 수 있어요."),
            Foreground = _p.SubText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14),
        });
        if (!ReportService.IsConfigured) body.Children.Add(NotReadyNote());

        // ── 종류 ── (세그먼트가 제목·내용 칸 안내 문구를 바꾸므로 안내 문구를 먼저 만듦)
        _titlePlaceholder = Placeholder(TitlePlaceholderFor(_kind), multiLine: false);
        _messagePlaceholder = Placeholder(PlaceholderFor(_kind), multiLine: true);
        body.Children.Add(Label(Loc.T("종류")));
        body.Children.Add(KindSegments());

        // ── 제목 (선택) ── 받는 쪽은 내용 첫 줄을 메일 제목으로 쓰므로, 적으면 내용 앞줄로 붙여 보냄 (비우면 내용 첫 줄)
        body.Children.Add(Label(Loc.T("제목 (선택)")));
        _title = Input(multiLine: false);
        _title.MaxLength = TitleMax;
        _title.TextChanged += (_, _) => UpdateForm();
        var titleGrid = new Grid();
        titleGrid.Children.Add(_titlePlaceholder);
        titleGrid.Children.Add(_title);
        body.Children.Add(InputBox(titleGrid));

        // ── 내용 ──
        body.Children.Add(Label(Loc.T("내용")));
        _message = Input(multiLine: true);
        _message.MaxLength = ReportService.MaxMessage;
        _message.TextChanged += (_, _) => UpdateForm();
        var messageGrid = new Grid();
        messageGrid.Children.Add(_messagePlaceholder);
        messageGrid.Children.Add(_message);
        body.Children.Add(InputBox(messageGrid));
        _counter = new TextBlock { FontSize = 11.5, Foreground = _p.SubText, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 2, 0) };
        body.Children.Add(_counter);

        // ── 답장 받을 이메일 ──
        body.Children.Add(Label(Loc.T("답장 받을 이메일 (선택)")));
        _contact = Input(multiLine: false);
        _contact.MaxLength = 254;
        _contact.TextChanged += (_, _) => UpdateForm();
        var contactGrid = new Grid();
        _contactPlaceholder = Placeholder(Loc.T("적으면 답장을 메일로 보내 드려요"), multiLine: false);
        contactGrid.Children.Add(_contactPlaceholder);
        contactGrid.Children.Add(_contact);
        body.Children.Add(InputBox(contactGrid));
        _contactError = new TextBlock
        {
            Text = Loc.T("이메일 형식을 확인해 주세요."),
            FontSize = 11.5,
            Foreground = _p.HolidayText,
            Margin = new Thickness(2, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        body.Children.Add(_contactError);

        // ── 함께 보낼 정보 (접기/펼치기) ──
        _detailsChevron = new TextBlock
        {
            Text = "", // ChevronRight
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 10,
            Foreground = _p.SubText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 8, 0),
        };
        var toggleContent = new StackPanel { Orientation = Orientation.Horizontal };
        toggleContent.Children.Add(_detailsChevron);
        toggleContent.Children.Add(new TextBlock { Text = Loc.T("함께 보낼 정보"), FontWeight = FontWeights.SemiBold, Foreground = _p.SubText });
        var toggle = new Button
        {
            Style = (Style)FindResource("CardButton"),
            Background = Brushes.Transparent,
            Foreground = _p.Text,
            Padding = new Thickness(4, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = toggleContent,
            Margin = new Thickness(-4, 14, 0, 0),
        };
        toggle.Click += (_, _) => SetDetailsOpen(!_detailsOpen);
        body.Children.Add(toggle);

        _details = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            Foreground = _p.SubText,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Malgun Gothic"),
            FontSize = 11,
            Height = 170,
            Padding = new Thickness(0),
            Text = _diagnostics,
        };
        var detailsStack = new StackPanel();
        detailsStack.Children.Add(new TextBlock
        {
            Text = Loc.T("앱 버전·윈도우·모니터·설정 요약·최근 로그예요. 이메일·주소(URL)·파일 경로·사용자 이름·기기 이름·창 제목은 가렸고, 보이는 그대로 보내요."),
            FontSize = 11.5,
            Foreground = _p.SubText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        detailsStack.Children.Add(_details);
        _detailsHost = InputBox(detailsStack);
        _detailsHost.Visibility = Visibility.Collapsed;
        _detailsHost.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(_detailsHost);

        // ── 아래: 상태 문구 + 취소/보내기 ──
        var bottom = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = new TextBlock { Foreground = _p.SubText, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), FontSize = 12 };
        bottom.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _cancel = Button(Loc.T("취소"), primary: false, Close);
        _send = Button(Loc.T("보내기"), primary: true, () => _ = SendAsync());
        _send.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_send);
        Grid.SetColumn(buttons, 1);
        bottom.Children.Add(buttons);
        body.Children.Add(bottom);

        _form = new Grid();
        _form.Children.Add(body);
        _done = new StackPanel { Margin = new Thickness(24, 36, 24, 24), Visibility = Visibility.Collapsed };

        var root = new Grid();
        // 입력 칸·미리 보기·창 스크롤 모두 맥 같은 얇은 스크롤바 (Themes/Controls.xaml ThinScrollBar)
        if (TryFindResource("ThinScrollBar") is Style thin) root.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), thin);
        root.Children.Add(new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        root.Children.Add(_done);
        Content = root;

        UpdateForm();
        Loaded += (_, _) => _title.Focus();
    }

    /// <summary>진단 정보가 준비됨 (가린 결과). 미리 보기와 보낼 내용이 같다.</summary>
    public void SetDiagnostics(string text)
    {
        if (_closed) return;
        _diagnostics = text;
        _diagnosticsReady = true;
        _details.Text = text;
        UpdateForm();
    }

    /// <summary>시험·스크린샷용: 펼침 상태와 입력값을 정함.</summary>
    public void SetPreviewState(ReportKind kind, string title, string message, string contact, bool detailsOpen)
    {
        SelectKind(kind);
        _title.Text = title;
        _message.Text = message;
        _contact.Text = contact;
        SetDetailsOpen(detailsOpen);
    }

    // ───────────────────────── 동작 ─────────────────────────

    private bool ContactValid => _contact.Text.Trim().Length == 0 || EmailPattern.IsMatch(_contact.Text.Trim());

    private void UpdateForm()
    {
        int len = _message.Text.Length;
        _messagePlaceholder.Visibility = len == 0 ? Visibility.Visible : Visibility.Collapsed;
        _titlePlaceholder.Visibility = _title.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _contactPlaceholder.Visibility = _contact.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _counter.Text = $"{len:N0} / {ReportService.MaxMessage:N0}";
        bool contactOk = ContactValid;
        _contactError.Visibility = contactOk ? Visibility.Collapsed : Visibility.Visible;
        bool ok = !_sending && _diagnosticsReady && _message.Text.Trim().Length >= 3 && contactOk;
        _send.IsEnabled = ok;
        _send.Opacity = ok ? 1 : 0.45;
    }

    private void SetDetailsOpen(bool open)
    {
        _detailsOpen = open;
        _detailsHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _detailsChevron.Text = open ? "" : ""; // ChevronDown / ChevronRight
        // 창 높이가 막혀 안에서 스크롤될 때도 펼친 내용이 보이게
        if (open) Dispatcher.BeginInvoke(() => _detailsHost.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 창이 있는 모니터의 작업 영역 안에 머물게: 높이 상한 = 작업 영역 - 여백, 아래가 넘치면 위로 올림.
    /// (WPF 좌표는 그 모니터 기준 DIP = 물리 픽셀 / 배율 — Services/Monitors 설명)
    /// </summary>
    private void KeepOnScreen()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var center = new Point((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0);
        var monitor = Monitors.GetAll().FirstOrDefault(m => m.ContainsPx(center)) ?? Monitors.GetPrimary();
        Rect work = monitor.WorkArea;
        const double gap = 12;
        double max = Math.Max(360, work.Height - gap * 2);
        if (Math.Abs(MaxHeight - max) > 0.5) MaxHeight = max;
        double height = Math.Min(ActualHeight, max);
        if (Top + height > work.Bottom - gap) Top = Math.Max(work.Top + gap, work.Bottom - gap - height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

    private void SelectKind(ReportKind kind)
    {
        _kind = kind;
        foreach (var (b, k) in _kindButtons)
        {
            bool on = k == kind;
            b.Background = on ? _p.Accent : Brushes.Transparent;
            b.Foreground = on ? _p.AccentText : _p.Text;
        }
        _messagePlaceholder.Text = PlaceholderFor(kind);
        _titlePlaceholder.Text = TitlePlaceholderFor(kind);
    }

    /// <summary>메일 제목 칸 최대 길이 (받는 쪽 Code.gs 가 첫 줄을 60자로 자름).</summary>
    private const int TitleMax = 60;

    private static string TitlePlaceholderFor(ReportKind kind) => kind switch
    {
        ReportKind.Question => Loc.T("예: 상단바 시계 형식을 바꿀 수 있나요?"),
        ReportKind.Idea => Loc.T("예: 독 아이콘에 알림 개수도 보여 주세요"),
        _ => Loc.T("예: 독에서 카카오톡이 안 열려요"),
    } + (Loc.IsEnglish ? "" : Loc.T("  (비우면 내용 첫 줄)")); // 영어는 길어서 한 줄 칸에서 넘침 (QA)

    private static string PlaceholderFor(ReportKind kind) => kind switch
    {
        ReportKind.Question => Loc.T("궁금한 점을 적어 주세요."),
        ReportKind.Idea => Loc.T("있으면 좋겠는 기능이나 바꾸면 좋겠는 점을 적어 주세요."),
        _ => Loc.T("무엇을 했을 때 어떻게 됐는지 적어 주세요.\n예: 독에서 카카오톡을 누르면 창이 안 떠요."),
    };

    private async Task SendAsync()
    {
        if (_sending || !_send.IsEnabled) return;
        // 제목을 적었으면 내용 앞줄로 (받는 쪽이 첫 줄을 메일 제목으로 씀)
        string title = _title.Text.Trim().ReplaceLineEndings(" ");
        string message = title.Length > 0 ? title + "\n\n" + _message.Text.Trim() : _message.Text;
        string contact = _contact.Text.Trim(), diagnostics = _diagnostics;

        if (!ReportService.IsConfigured)
        {
            CopyFallback(message, contact, diagnostics);
            ShowStatus(Loc.F($"아직 신고 받는 곳을 준비 중이에요. 내용을 클립보드에 복사했어요 — {ReportService.SupportAddress} 로 메일에 붙여 넣어 보내 주세요."), error: false);
            return;
        }

        _sending = true;
        _send.Content = Loc.T("보내는 중…");
        ShowStatus("", error: false);
        UpdateForm();
        ReportSendResult result;
        try
        {
            result = await ReportService.SendAsync(_kind, message, contact, diagnostics);
        }
        catch (Exception ex)
        {
            Log.Warn($"문제 신고 보내기 실패: {ex.Message}");
            result = ReportSendResult.Failed;
        }
        if (_closed) return;
        _sending = false;
        _send.Content = Loc.T("보내기");
        UpdateForm();

        switch (result)
        {
            case ReportSendResult.Sent:
                ShowDone(contact.Length > 0);
                break;
            case ReportSendResult.Limited:
                ShowStatus(Loc.T("오늘은 더 보낼 수 없어요. 내일 다시 보내 주세요."), error: true);
                break;
            default:
                CopyFallback(message, contact, diagnostics);
                ShowStatus(Loc.F($"보내지 못했어요. 내용을 클립보드에 복사했어요 — {ReportService.SupportAddress} 로 메일에 붙여 넣어 보내 주세요."), error: true);
                break;
        }
    }

    private void CopyFallback(string message, string contact, string diagnostics)
    {
        try { Clipboard.SetText(ReportService.ClipboardText(_kind, message, contact, diagnostics)); }
        catch (Exception ex) { Log.Warn($"신고 내용 클립보드 복사 실패: {ex.Message}"); }
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = error ? _p.HolidayText : _p.SubText;
    }

    private void ShowDone(bool willReply)
    {
        _form.Visibility = Visibility.Collapsed;
        _done.Children.Clear();
        _done.Children.Add(new TextBlock
        {
            Text = "", // CheckMark
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 36,
            Foreground = _p.Accent,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _done.Children.Add(new TextBlock
        {
            Text = Loc.T("보냈어요"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        });
        _done.Children.Add(new TextBlock
        {
            Text = willReply ? Loc.T("고마워요. 답장은 메일로 갈게요.") : Loc.T("고마워요. 보내 주신 내용은 꼼꼼히 읽어 볼게요."),
            Foreground = _p.SubText,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 20),
        });
        var close = Button(Loc.T("닫기"), primary: true, Close);
        close.HorizontalAlignment = HorizontalAlignment.Center;
        close.MinWidth = 96;
        _done.Children.Add(close);
        _done.Visibility = Visibility.Visible;
    }

    // ───────────────────────── 컨트롤 ─────────────────────────

    private UIElement NotReadyNote()
    {
        var text = new TextBlock
        {
            Text = Loc.F($"신고 받는 곳을 준비 중이에요. 그동안은 {ReportService.SupportAddress} 로 메일을 보내 주세요. 아래에 적고 \"보내기\"를 누르면 내용을 클립보드에 복사해 드려요."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        var copy = Button(Loc.T("주소 복사"), primary: false, () =>
        {
            try { Clipboard.SetText(ReportService.SupportAddress); }
            catch (Exception ex) { Log.Warn($"지원 주소 복사 실패: {ex.Message}"); }
        });
        copy.Margin = new Thickness(12, 0, 0, 0);
        copy.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        return new Border
        {
            Background = _p.GroupBackground,
            BorderBrush = _p.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 14),
            Child = grid,
        };
    }

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = _p.SubText,
        Margin = new Thickness(2, 12, 0, 6),
    };

    private Border KindSegments()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (kind, label) in new[] { (ReportKind.Bug, Loc.T("버그")), (ReportKind.Question, Loc.T("질문")), (ReportKind.Idea, Loc.T("제안")) })
        {
            var b = new Button
            {
                Style = (Style)FindResource("CardButton"),
                Content = label,
                Padding = new Thickness(14, 4, 14, 5),
                MinWidth = 64,
            };
            b.Click += (_, _) => SelectKind(kind);
            _kindButtons.Add((b, kind));
            panel.Children.Add(b);
        }
        SelectKind(_kind);
        return new Border
        {
            Background = _p.Tile,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = panel,
        };
    }

    private TextBox Input(bool multiLine)
    {
        var box = new TextBox
        {
            Background = Brushes.Transparent,
            Foreground = _p.Text,
            CaretBrush = _p.Text,
            SelectionBrush = _p.Accent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = multiLine,
            AcceptsTab = false,
            TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Height = multiLine ? 132 : double.NaN,
            VerticalContentAlignment = multiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        box.SetResourceReference(FontFamilyProperty, UiFonts.Key);
        InputMethod.SetIsInputMethodEnabled(box, true);
        return box;
    }

    private TextBlock Placeholder(string text, bool multiLine) => new()
    {
        Text = text,
        Foreground = _p.Disabled,
        TextWrapping = TextWrapping.Wrap,
        IsHitTestVisible = false,
        VerticalAlignment = multiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 0, 0),
    };

    /// <summary>입력 칸 테두리: 설정 창 그룹 카드와 같은 둥근 카드.</summary>
    private Border InputBox(UIElement child) => new()
    {
        Background = _p.GroupBackground,
        BorderBrush = _p.Divider,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 8, 10, 8),
        Child = child,
    };

    private Button Button(string text, bool primary, Action action)
    {
        var b = new Button
        {
            Style = (Style)FindResource("CardButton"),
            Background = primary ? _p.Accent : _p.Tile,
            Foreground = primary ? _p.AccentText : _p.Text,
            Content = text,
            MinWidth = 72,
        };
        b.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"'{text}' 실행 실패", ex); }
        };
        return b;
    }

    // ───────────────────────── 제목 표시줄 색 ─────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private void ApplyTitleBarTheme()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = _p.IsLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { /* 오래된 Windows */ }
    }
}
