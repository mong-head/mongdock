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
    // 첨부 (#26)
    private readonly List<ReportAttachment> _attachments = new();
    private readonly StackPanel _attachRows = new();
    private readonly TextBlock _attachNote;
    private readonly TextBlock _attachError;
    /// <summary>영역 캡처 기다리는 중 줄 ("캡처 도구에서 영역을 고르면 … · 취소") (#13).</summary>
    private readonly TextBlock _clipWaiting;
    private CancellationTokenSource? _clipStop;
    private readonly Button _sendWithout;
    private AppServices? _services;
    private ReportRedactor.Context _ctx = ReportRedactor.Context.Current(Array.Empty<string>());

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
            var win = new ReportWindow(settings) { _services = services, _ctx = ctx };
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
        MongTitleBar.Install(this, _p); // 몽독 제목 줄 (#25 §8) — 크기 고정 창이라 최대화 동그라미는 흐리게
        // "함께 보낼 정보"를 펼치면 창이 아래로 길어짐 → 화면 아래를 넘으면 위로 올리고, 화면보다 길면 높이를 막아 안에서 스크롤
        SizeChanged += (_, e) => { if (e.HeightChanged) KeepOnScreen(); };
        // Ctrl+V: 클립보드의 그림·파일은 첨부로 (글자는 그대로 입력 칸에 붙음) (#13)
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PasteAttachments(ScreenClip.SafeClipboard())) e.Handled = true;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _clipStop?.Cancel();
            if (_instance == this) _instance = null;
        };

        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 20) };
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

        // ── 첨부 (선택) (#26) ──
        body.Children.Add(Label(Loc.T("첨부 (선택)")));
        var attachButtons = new WrapPanel();
        var addFile = Button("+ " + Loc.T("파일 추가"), primary: false, AddFiles);
        addFile.Margin = new Thickness(0, 0, 8, 0);
        var capture = Button("+ " + Loc.T("지금 화면 캡처"), primary: false, () => _ = CaptureAsync());
        attachButtons.Children.Add(addFile);
        attachButtons.Children.Add(capture);
        if (ScreenClip.Available())
        {
            // 영역 캡처 (#13): 윈도우 캡처 도구로 고른 부분 → 클립보드 그림 자동 첨부. 도구가 없는 윈도우 10 이면 버튼 없음
            capture.Margin = new Thickness(0, 0, 8, 0);
            attachButtons.Children.Add(Button("+ " + Loc.T("영역 캡처"), primary: false, () => _ = ClipAsync()));
        }
        body.Children.Add(attachButtons);
        body.Children.Add(new TextBlock
        {
            Text = Loc.F($"{Loc.TC("key", "Ctrl")}+V로 그림이나 파일을 붙여 넣을 수도 있어요."),
            FontSize = 11.5,
            Foreground = _p.SubText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 0),
        });
        _clipWaiting = new TextBlock { FontSize = 11.5, Foreground = _p.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0), Visibility = Visibility.Collapsed };
        var stopLink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(Loc.T("취소"))) { Foreground = _p.AccentInk, TextDecorations = null, Cursor = Cursors.Hand };
        stopLink.Click += (_, _) => _clipStop?.Cancel();
        _clipWaiting.Inlines.Add(new System.Windows.Documents.Run(Loc.T("캡처 도구에서 영역을 고르면 여기에 붙어요.") + " "));
        _clipWaiting.Inlines.Add(stopLink);
        body.Children.Add(_clipWaiting);
        _attachRows.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_attachRows);
        _attachError = new TextBlock { FontSize = 11.5, Foreground = _p.HolidayText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0), Visibility = Visibility.Collapsed };
        body.Children.Add(_attachError);
        _attachNote = new TextBlock
        {
            Text = Loc.T("화면에 개인정보(이름·대화·주소 등)가 보이지 않는지 확인해 주세요."),
            FontSize = 11.5,
            Foreground = _p.SubText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        body.Children.Add(_attachNote);

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
        _sendWithout = Button(Loc.T("첨부 없이 보내기"), primary: false, () => _ = SendAsync(withoutAttachments: true));
        _sendWithout.Margin = new Thickness(0, 0, 8, 0);
        _sendWithout.Visibility = Visibility.Collapsed;
        buttons.Children.Add(_sendWithout);
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
        root.Children.Add(MongScroll.Apply(new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Margin = new Thickness(0, MongTitleBar.Height, 0, 0) }, _p));
        root.Children.Add(_done);
        root.Children.Add(MongTitleBar.Overlay(this, _p, null));
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
    } + (Loc.IsKorean ? Loc.T("  (비우면 내용 첫 줄)") : ""); // 다른 언어는 길어서 한 줄 칸에서 넘침 (QA)

    private static string PlaceholderFor(ReportKind kind) => kind switch
    {
        ReportKind.Question => Loc.T("궁금한 점을 적어 주세요."),
        ReportKind.Idea => Loc.T("있으면 좋겠는 기능이나 바꾸면 좋겠는 점을 적어 주세요."),
        _ => Loc.T("무엇을 했을 때 어떻게 됐는지 적어 주세요.\n예: 독에서 카카오톡을 누르면 창이 안 떠요."),
    };

    // ───────────────────────── 첨부 (#26) ─────────────────────────

    /// <summary>시험·스크린샷용: 첨부 하나 더함.</summary>
    public void AddPreviewAttachment(ReportAttachment a) => TryAdd(a);

    private long AttachedBytes => _attachments.Sum(a => (long)a.Data.Length);

    private void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = ReportAttachments.DialogFilter, Title = Loc.T("첨부할 파일") };
        if (dlg.ShowDialog(this) != true) return;
        AddPaths(dlg.FileNames);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!ReportAttachments.Allowed(path)) { ShowAttachError(Loc.T("그림(png·jpg·gif·webp)·동영상(mp4)·텍스트(txt·log)만 붙일 수 있어요.")); continue; }
            var a = ReportAttachments.FromFile(path, _ctx, out bool tooBig);
            if (tooBig) { ShowAttachError(Loc.T("파일이 너무 커요(최대 10MB)")); continue; }
            if (a is null) { ShowAttachError(Loc.T("파일을 읽지 못했어요.")); continue; }
            if (!TryAdd(a)) break;
        }
    }

    /// <summary>
    /// 붙여넣기 (#13): 파일(탐색기에서 복사)이면 파일 추가와 같은 규칙으로, 그림이면 그림 첨부로. 둘 다 아니면(글자) false — 입력 칸이 그대로 붙여 넣음.
    /// 시험에서는 클립보드 대신 만든 데이터를 넘김.
    /// </summary>
    internal bool PasteAttachments(IDataObject? data)
    {
        if (data is null) return false;
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                AddPaths(files);
                return true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Warn($"붙여넣기 파일 읽기 실패: {ex.GetType().Name}");
            return false;
        }
        if (!data.GetDataPresent("PNG") && !data.GetDataPresent(DataFormats.Bitmap)) return false;
        if (ScreenClip.FromDataObject(data) is { } shot) TryAdd(shot);
        else ShowAttachError(Loc.T("클립보드 그림을 읽지 못했어요."));
        return true;
    }

    /// <summary>
    /// [+ 영역 캡처] (#13): 신고 창을 숨기고 윈도우 캡처 도구를 띄움 → 클립보드에 새 그림이 들어오면 첨부. 도구를 그림 없이 닫으면 그냥 돌아옴.
    /// 도구 화면을 못 찾으면 신고 창을 다시 띄우고 "고르면 여기에 붙어요 · 취소" 를 보이며 2분까지 기다림.
    /// </summary>
    private async Task ClipAsync()
    {
        if (_clipStop is not null) return;
        if (_attachments.Count >= ReportAttachments.MaxCount) { ShowAttachError(Loc.F($"첨부는 {ReportAttachments.MaxCount}개까지예요.")); return; }
        _attachError.Visibility = Visibility.Collapsed;
        var stop = _clipStop = new CancellationTokenSource();
        var hidden = HideForCapture();
        await Task.Delay(250); // 신고 창이 화면에서 사라진 뒤 (캡처 도구는 띄운 순간의 화면을 얼림)
        bool shown = false;
        void ShowBack()
        {
            if (shown || _closed) return;
            shown = true;
            RestoreAfterCapture(hidden);
            Show();
            Activate();
            if (!stop.IsCancellationRequested) _clipWaiting.Visibility = Visibility.Visible;
        }
        ReportAttachment? shot;
        ScreenClipOutcome outcome;
        try
        {
            (shot, outcome) = await ScreenClip.CaptureAsync(ShowBack, stop.Token);
        }
        finally
        {
            _clipStop = null;
            stop.Dispose();
            if (!_closed) { _clipWaiting.Visibility = Visibility.Collapsed; ShowBack(); }
        }
        if (_closed) return;
        switch (outcome)
        {
            case ScreenClipOutcome.Captured when shot is not null: TryAdd(shot); break;
            case ScreenClipOutcome.LaunchFailed: ShowAttachError(Loc.T("캡처 도구를 열지 못했어요.")); break;
            case ScreenClipOutcome.NoTool or ScreenClipOutcome.TimedOut: ShowAttachError(Loc.F($"캡처한 그림이 들어오지 않았어요. 캡처 도구에서 복사한 뒤 {Loc.TC("key", "Ctrl")}+V로 붙여 넣어도 돼요.")); break;
        }
    }

    private bool TryAdd(ReportAttachment a)
    {
        if (_attachments.Count >= ReportAttachments.MaxCount) { ShowAttachError(Loc.F($"첨부는 {ReportAttachments.MaxCount}개까지예요.")); return false; }
        if (AttachedBytes + a.Data.Length > ReportAttachments.MaxTotalBytes) { ShowAttachError(Loc.T("파일이 너무 커요(최대 10MB)")); return true; }
        _attachError.Visibility = Visibility.Collapsed;
        _attachments.Add(a);
        RebuildAttachments();
        return true;
    }

    private void ShowAttachError(string text)
    {
        _attachError.Text = text;
        _attachError.Visibility = Visibility.Visible;
    }

    /// <summary>[+ 지금 화면 캡처]: 신고 창을 숨기고 300ms 뒤 커서 모니터 전체를 찍고 다시 띄움 (카운트다운·번쩍임 없음).</summary>
    private async Task CaptureAsync()
    {
        if (_attachments.Count >= ReportAttachments.MaxCount) { ShowAttachError(Loc.F($"첨부는 {ReportAttachments.MaxCount}개까지예요.")); return; }
        var hidden = HideForCapture();
        ReportAttachment? shot;
        try
        {
            await Task.Delay(300);
            shot = await Task.Run(ReportAttachments.CaptureScreen);
        }
        finally
        {
            RestoreAfterCapture(hidden);
            if (!_closed) { Show(); Activate(); }
        }
        if (_closed) return;
        if (shot is null) { ShowAttachError(Loc.T("화면을 찍지 못했어요.")); return; }
        TryAdd(shot);
    }

    /// <summary>
    /// 캡처 동안 신고 창과 함께 다른 몽독 창(설정·루틴 편집·변경 내역 등 작업 표시줄에 뜨는 일반 창)도 잠깐 숨김 (#17).
    /// 독·상단바와 그 딸린 창은 사용자 화면의 일부라 그대로. 숨긴 창 목록을 돌려줌.
    /// </summary>
    internal List<Window> HideForCapture()
    {
        var hidden = new List<Window>();
        foreach (Window w in Application.Current.Windows)
        {
            if (ReferenceEquals(w, this) || !w.IsVisible || !w.ShowInTaskbar || w.WindowState == WindowState.Minimized) continue;
            w.Hide();
            hidden.Add(w);
        }
        Hide();
        if (hidden.Count > 0) Log.Info($"신고 캡처: 몽독 창 {hidden.Count}개도 잠깐 숨김");
        return hidden;
    }

    /// <summary>숨겼던 다른 몽독 창을 다시 보임 (활성화하지 않음 — 신고 창이 앞에 옴).</summary>
    internal static void RestoreAfterCapture(List<Window> hidden)
    {
        foreach (var w in hidden)
        {
            try
            {
                bool activated = w.ShowActivated;
                w.ShowActivated = false;
                w.Show();
                w.ShowActivated = activated;
            }
            catch (InvalidOperationException) { } // 그사이 닫힌 창
        }
        hidden.Clear();
    }

    private void RebuildAttachments()
    {
        _attachRows.Children.Clear();
        foreach (var att in _attachments)
        {
            var a = att;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            var remove = new Button
            {
                Style = (Style)FindResource("CardButton"),
                Background = Brushes.Transparent,
                Foreground = _p.SubText,
                Content = "×",
                FontSize = 15,
                Padding = new Thickness(8, 0, 8, 2),
                ToolTip = Loc.T("빼기"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += (_, _) => { _attachments.Remove(a); _attachError.Visibility = Visibility.Collapsed; RebuildAttachments(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var thumb = new Border { Width = 48, Height = 36, CornerRadius = new CornerRadius(4), Background = _p.Tile, Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true };
            if (ReportAttachments.Preview(a, 96) is { } img)
            {
                thumb.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
                thumb.Cursor = Cursors.Hand;
                thumb.ToolTip = Loc.T("크게 보기");
                thumb.MouseLeftButtonUp += (_, _) => ShowLarge(a);
            }
            else thumb.Child = new TextBlock { Text = a.Mime.StartsWith("video/") ? "▶" : "TXT", FontSize = 11, Foreground = _p.SubText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(thumb, Dock.Left);
            row.Children.Add(thumb);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = a.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = ReportAttachments.SizeText(a.Data.Length), FontSize = 11.5, Foreground = _p.SubText });
            row.Children.Add(text);
            _attachRows.Children.Add(row);
        }
        _attachNote.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _sendWithout.Visibility = Visibility.Collapsed;
    }

    /// <summary>그림 첨부 크게 보기 (화면의 80% 안).</summary>
    private void ShowLarge(ReportAttachment a)
    {
        if (ReportAttachments.Preview(a, 0) is not { } img) return;
        var work = Monitors.FromHwnd(new WindowInteropHelper(this).Handle).WorkArea;
        var w = new Window
        {
            Title = a.Name,
            Owner = this,
            Background = _p.WindowBackground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResize,
            Width = Math.Min(work.Width * 0.8, img.PixelWidth + 40),
            Height = Math.Min(work.Height * 0.8, img.PixelHeight + 80),
            Content = new Image { Source = img, Stretch = Stretch.Uniform, Margin = new Thickness(12) },
        };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.MouseLeftButtonUp += (_, _) => w.Close();
        w.Show();
    }

    private async Task SendAsync(bool withoutAttachments = false)
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
            result = await ReportService.SendAsync(_kind, message, contact, diagnostics, withoutAttachments ? null : _attachments);
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

        bool hadAttachments = !withoutAttachments && _attachments.Count > 0;
        switch (result)
        {
            case ReportSendResult.Sent:
                if (hadAttachments && _services is { } services)
                {
                    services.Settings.Current.ReportAttachmentsSinceSignal += _attachments.Count; // 통계: 첨부 개수만
                    services.Settings.Save();
                }
                ShowDone(contact.Length > 0);
                break;
            case ReportSendResult.Failed when hadAttachments:
                // 첨부 때문일 수 있음 → 첨부 없이 다시 보낼지 묻기 (클립보드 복사는 첨부 없이도 실패할 때)
                ShowStatus(Loc.T("첨부 파일 때문에 보내지 못했어요. 첨부 없이 보낼까요?"), error: true);
                _sendWithout.Visibility = Visibility.Visible;
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
            Foreground = _p.AccentInk,
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
            BorderBrush = _p.GroupBorder,
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
            SelectionBrush = _p.SoftAccentLine,
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
        BorderBrush = _p.GroupBorder,
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
