using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 소리 판 (0.8.2 정리, #21): 출력과 입력을 같은 모양으로.
/// - 소리(출력): 제목 + 켜기 스위치(음소거) → 볼륨 막대 → 지금 소리 내는 앱 1~2개 막대 → 「앱별 볼륨 n개 ›」 → 「스피커: 이름 ›」.
/// - 마이크(입력): 제목 + 켜기 스위치(마이크 음소거) → 음량 막대 + 레벨 점 → 「마이크: 이름 ›」.
/// - 스위치를 끄면 그 아래 내용 대신 「음소거됨」 한 줄. 재생 중 카드는 늘 보임.
/// - 장치 목록·모든 앱 볼륨은 판 안에서 옆으로 넘어가는 세부 화면(‹ 뒤로, Esc·← 로도). 세부 화면은 내용만큼 높이(아래가 비지 않게)이고
///   넘어갈 때 판 높이가 180ms 동안 부드럽게 바뀜. 화면보다 길면 판 전체가 스크롤.
/// </summary>
internal sealed partial class StatusPanelWindow
{
    private enum SoundPage { None, Apps, Output, Input }

    private const string ChevronRightGlyph = "";
    private const string ChevronLeftGlyph = "";

    /// <summary>세부 화면이면 뒤로 가고 true (Esc·← 가 판을 닫지 않게). 소리 판이 아니면 null.</summary>
    private Func<bool>? _back;

    /// <summary>시험 그림용: 세부 화면을 움직임 없이 바로 엶 ("Apps"·"Output"·"Input"·"None").</summary>
    private Action<string>? _openSoundPageForTest;

    internal void OpenSoundPageForTest(string page) => _openSoundPageForTest?.Invoke(page);

    /// <summary>시험용: 움직임과 함께 세부 화면 열기 (실제로 누를 때와 같은 길).</summary>
    private Action<string>? _openSoundPageAnimatedForTest;
    internal void OpenSoundPageAnimatedForTest(string page) => _openSoundPageAnimatedForTest?.Invoke(page);

    /// <summary>마지막 넘김의 첫 프레임까지(ms)·가장 긴 프레임 간격(ms)·프레임 수 (#21 넘김 끊김 — 로그에도 남김).</summary>
    internal static (long FirstMs, long MaxGapMs, int Frames)? LastSoundTransition { get; private set; }

    /// <summary>넘김을 누른 순간부터 300ms 동안 화면 그리기 간격을 잼 (CompositionTarget.Rendering).</summary>
    private static void WatchSoundTransition(string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long first = -1, last = 0, maxGap = 0;
        int frames = 0;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            long now = sw.ElapsedMilliseconds;
            if (first < 0) first = now;
            else maxGap = Math.Max(maxGap, now - last);
            last = now;
            frames++;
            if (now < 300) return;
            CompositionTarget.Rendering -= tick;
            LastSoundTransition = (first, maxGap, frames);
            Log.Info($"소리 판 넘김({what}): 첫 프레임 {first}ms, 가장 긴 간격 {maxGap}ms, 300ms 동안 프레임 {frames}개");
        };
        CompositionTarget.Rendering += tick;
    }

    /// <summary>시험용: 판 높이 움직임을 "도는 중"으로 두고 끝내기를 기다림 (움직이는 도중 목록이 바뀌는 경우 재현). null 이면 보통.</summary>
    internal static bool HoldHeightAnimationForTest { get; set; }


    /// <summary>시험용: 붙잡아 둔 높이 움직임을 끝냄.</summary>
    private Action? _finishHeightForTest;
    internal void FinishHeightAnimationForTest() { var f = _finishHeightForTest; _finishHeightForTest = null; f?.Invoke(); }

    /// <summary>앱 줄 묶음 (첫 화면의 1~2줄, 세부 화면의 모든 앱) — 목록이 같으면 줄을 다시 만들지 않고 값만 고침.</summary>
    private sealed class AppRows
    {
        public readonly StackPanel Host = new();
        public readonly Dictionary<string, AppVolumeRow> ByKey = new();
        public string Signature = "";
    }

    private void ApplyAppRows(AppRows rows, IReadOnlyList<AppVolume> list)
    {
        string sig = string.Join("|", list.Select(a => a.Key));
        if (sig != rows.Signature)
        {
            rows.Host.Children.Clear();
            rows.ByKey.Clear();
            foreach (var a in list) rows.Host.Children.Add(AppVolumeLine(a, rows.ByKey));
            rows.Signature = sig; // 다 만든 뒤에 (중간에 실패하면 다음에 다시 만들게)
        }
        foreach (var a in list)
        {
            if (!rows.ByKey.TryGetValue(a.Key, out var r)) continue;
            if (!r.Slider.IsDragging) r.Slider.Value = a.Muted ? 0 : a.Volume;
            SetAppMuted(r, a.Muted);
        }
    }

    private UIElement BuildVolume()
    {
        var st = _services.Status;
        // 두 화면을 같은 칸에 겹쳐 두고 옆으로 밀어 넘김. 첫 화면은 넘어간 뒤에도 자리를 지켜 판 높이를 정함
        // 자르는 칸은 좌우로 8px 넓게 (줄 버튼의 호버 바탕이 좌우 -6px 밖까지 그려져 잘리면 각진 네모로 보임 — 0.8.2 사용자)
        var host = new Grid { ClipToBounds = true, Margin = new Thickness(-8, 0, -8, 0) };
        var main = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
        var detail = new DockPanel { LastChildFill = true, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 8, 0) };
        var mainShift = new TranslateTransform();
        var detailShift = new TranslateTransform();
        main.RenderTransform = mainShift;
        detail.RenderTransform = detailShift;
        host.Children.Add(main);
        host.Children.Add(detail);

        // ── 출력 ──
        var outSwitch = Switch(on =>
        {
            st.SetMuted(!on);
            if (on && st.Volume <= 0) st.SetVolume(0.3); // 볼륨 0 인 채로 켜면 들리지 않으니 조금 올림
            return Task.FromResult(true);
        }, null);
        outSwitch.ToolTip = Loc.T("소리 켜기/끄기");
        main.Children.Add(HeaderRow(Loc.T("소리"), outSwitch)); // 용어 (사용자 결정, 0.8.2): 출력 → 소리, 입력 → 마이크
        var outBody = new StackPanel();
        var outMuted = MutedLine();
        main.Children.Add(outBody);
        main.Children.Add(outMuted);
        var slider = new PillSlider(_p) { Margin = new Thickness(0, 6, 0, 2) };
        slider.UserChanged += (_, v) => SetVolume(v);
        outBody.Children.Add(slider);
        var topApps = new AppRows();
        topApps.Host.Margin = new Thickness(0, 4, 0, 0);
        outBody.Children.Add(topApps.Host);
        SoundPage page = SoundPage.None;
        Action<SoundPage>? go = null; // 아래에서 Open 으로 (세부 화면 조각을 만든 뒤)
        void Go(SoundPage p) => go?.Invoke(p);
        var appsNav = NavRow(() => Go(SoundPage.Apps));
        var outNav = NavRow(() => Go(SoundPage.Output));
        outBody.Children.Add(appsNav.Button);
        outBody.Children.Add(outNav.Button);

        // ── 입력 (실제 입력 장치가 없으면 통째로 숨김) ──
        var inSection = new StackPanel();
        inSection.Children.Add(Divider());
        var inSwitch = Switch(on =>
        {
            st.SetInputMuted(!on);
            if (on && st.InputVolume <= 0) st.SetInputVolume(0.5); // 음량 0 인 채로 켜면 안 들리니 올림
            return Task.FromResult(true);
        }, null);
        inSwitch.ToolTip = Loc.T("마이크 켜기/끄기");
        inSection.Children.Add(HeaderRow(Loc.T("마이크"), inSwitch));
        var inBody = new StackPanel();
        var inMuted = MutedLine();
        inSection.Children.Add(inBody);
        inSection.Children.Add(inMuted);
        var (inControls, inSlider, meterOn) = BuildInputControls();
        inBody.Children.Add(inControls);
        var inNav = NavRow(() => Go(SoundPage.Input));
        inBody.Children.Add(inNav.Button);
        main.Children.Add(inSection);

        // ── 재생 중 (늘 보임) · 설정 ──
        main.Children.Add(BuildMediaSection());
        main.Children.Add(Divider());
        main.Children.Add(LinkRow(Loc.T("사운드 설정…"), () => st.OpenSoundSettings()));

        // ── 세부 화면: ‹ 제목 / 목록 (스크롤) ──
        var backGlyph = new TextBlock { Text = ChevronLeftGlyph, FontFamily = IconFont, FontSize = 13, Foreground = _p.Text, VerticalAlignment = VerticalAlignment.Center };
        var backButton = new Button
        {
            Style = (Style)FindStyle("CardButton"),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Width = 28,
            Height = 28,
            Content = backGlyph,
            Margin = new Thickness(-6, 0, 4, 0),
            ToolTip = Loc.T("뒤로"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var detailTitle = Heading("");
        var detailHeader = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6), MinHeight = 26 };
        detailHeader.Children.Add(backButton);
        detailHeader.Children.Add(detailTitle);
        DockPanel.SetDock(detailHeader, Dock.Top);
        detail.Children.Add(detailHeader);
        var detailBody = new StackPanel();
        var detailScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
            Content = detailBody,
        };
        MongScroll.Apply(detailScroll, _p);
        if (TryFindResource("OverlayScrollViewer") is Style overlay) detailScroll.Style = overlay;
        detail.Children.Add(detailScroll);
        // 세부 화면 내용은 판이 열려 있는 동안 늘 만들어 둠 (#21 넘김 끊김: 넘기는 순간 줄·아이콘을 만들거나 녹음 앱을 읽지 않게)
        var allApps = new AppRows();
        var outDevices = new StackPanel();
        var inDevices = new StackPanel();
        string outSignature = "", inSignature = "";
        // 입력 장치별 녹음 중인 앱 ("사용 중: Discord") — 오디오 세션을 읽으므로 스레드 풀에서, 끝나면 입력 장치 줄만 다시
        Dictionary<string, string> inUse = new();
        bool readingUse = false;


        IReadOnlyList<AppVolume> apps = Array.Empty<AppVolume>();
        bool heightAnimating = false;

        double PageWidth() => host.ActualWidth > 0 ? host.ActualWidth : _card.Width - 34;

        // 세부 화면이 원하는 높이 (머리 줄 + 목록)
        double DetailHeight()
        {
            detail.Measure(new Size(PageWidth(), double.PositiveInfinity));
            return Math.Ceiling(detail.DesiredSize.Height);
        }

        // 세부 화면이면 지금 내용으로 다시 재서 판 높이를 맞춤 (#21 0.8.2 설치본: 목록이 다 그려지기 전 값이나 움직이는 도중 값으로
        // 높이가 굳어 셋째 줄이 잘리고 스크롤이 생기던 것 — 넘어간 뒤·목록이 바뀐 뒤·움직임이 끝난 뒤마다 다시 잼)
        void FitDetail()
        {
            if (page == SoundPage.None || heightAnimating) return;
            double h = DetailHeight();
            if (Math.Abs(host.Height - h) >= 0.5 || double.IsNaN(host.Height)) host.Height = h;
        }

        // 판(=host) 높이를 from → to 로 부드럽게 (움직임을 끈 PC·화면에 없을 땐 바로). 끝나면 done
        void ResizeHost(double from, double to, Action? done = null)
        {
            host.BeginAnimation(FrameworkElement.HeightProperty, null);
            if (HoldHeightAnimationForTest)
            {
                heightAnimating = true;
                host.Height = from;
                _finishHeightForTest = () =>
                {
                    heightAnimating = false;
                    host.Height = to;
                    done?.Invoke();
                    FitDetail();
                };
                return;
            }
            if (!Anim.Enabled || !IsLoaded || Math.Abs(from - to) < 1)
            {
                host.Height = to;
                done?.Invoke();
                return;
            }
            heightAnimating = true;
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            a.Completed += (_, _) =>
            {
                heightAnimating = false;
                host.Height = to;
                done?.Invoke();
                FitDetail(); // 움직이는 동안 목록이 바뀌었으면 마지막 측정값으로
            };
            host.Height = to;
            host.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
        // 세부 화면이 열려 있는 동안 내용이 바뀌면(장치 추가 등) 높이를 바로 맞춤
        detailBody.SizeChanged += (_, _) => FitDetail();
        // 넘길 땐 미리 만든 내용을 바꿔 끼우기만
        void FillDetail()
        {
            UIElement want = page switch { SoundPage.Apps => allApps.Host, SoundPage.Output => outDevices, _ => inDevices };
            if (detailBody.Children.Count == 1 && ReferenceEquals(detailBody.Children[0], want)) return;
            detailBody.Children.Clear();
            detailBody.Children.Add(want);
        }

        // 장치 줄 (장치 목록은 StatusService 가 몽독이 켜져 있는 동안 들고 있고 장치 바뀜 알림으로만 다시 읽음 — 여기선 바뀐 것만 다시 그림)
        void FillDevices()
        {
            var outs = st.OutputDevices ?? Array.Empty<AudioDevice>();
            string os = string.Join("|", outs.Select(d => $"{d.Id}:{d.IsDefault}:{d.Name}:{d.Kind}"));
            if (os != outSignature)
            {
                outDevices.Children.Clear();
                foreach (var d in outs)
                {
                    var id = d.Id;
                    outDevices.Children.Add(AudioDeviceRow(DeviceGlyph(d.Kind), d.IsDefault, d.Name, null, d.IsDefault ? null : () => st.SetDefaultOutput(id)));
                }
                if (outs.Count == 0) outDevices.Children.Add(Sub(Loc.T("재생 장치 없음")));
                outSignature = os;
            }
            var ins = VisibleInputs();
            string isig = string.Join("|", ins.Select(d => $"{d.Id}:{d.IsDefault}:{d.Name}:{d.Kind}:{(inUse.TryGetValue(d.Id, out var u) ? u : "")}"));
            if (isig != inSignature)
            {
                inDevices.Children.Clear();
                foreach (var d in ins)
                {
                    var id = d.Id;
                    // 장치마다 지금 녹음 중인 앱 ("사용 중: Discord") — 막대는 기본 장치만 바꾸므로, 앱이 다른 마이크를 쓰면 알 수 있게
                    string? sub = inUse.TryGetValue(d.Id, out var used) ? Loc.T("사용 중: ") + used
                        : d.Kind == AudioDeviceKind.Loopback ? Loc.T("재생 소리 녹음") : null;
                    inDevices.Children.Add(AudioDeviceRow(InputGlyph(d.Kind), d.IsDefault, d.Name, sub, d.IsDefault ? null : () => st.SetDefaultInput(id)));
                }
                if (ins.Count == 0) inDevices.Children.Add(Sub(Loc.T("입력 장치 없음")));
                inSignature = isig;
            }
        }

        async void ReadInputUse()
        {
            if (readingUse || _closed) return;
            readingUse = true;
            try
            {
                var use = await Task.Run(InputUsageByDevice);
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_closed) return;
                    inUse = use;
                    FillDevices();
                    FitDetail();
                });
            }
            catch (Exception ex) { Log.Warn($"입력 장치 사용 앱 읽기 실패: {ex.GetType().Name}"); }
            finally { readingUse = false; }
        }

        void Slide(TranslateTransform t, double from, double to, Action? done = null)
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);
            if (!Anim.Enabled || !IsLoaded)
            {
                t.X = to;
                done?.Invoke();
                return;
            }
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            if (done is not null) a.Completed += (_, _) => done();
            t.BeginAnimation(TranslateTransform.XProperty, a);
        }

        void Open(SoundPage p, bool animate)
        {
            if (animate) WatchSoundTransition(p.ToString());
            page = p;
            detailTitle.Text = p switch
            {
                SoundPage.Apps => Loc.T("앱별 볼륨"),
                SoundPage.Output => Loc.T("스피커 고르기"),
                _ => Loc.T("마이크 고르기"),
            };
            double fromHeight = host.ActualHeight > 0 ? host.ActualHeight : main.ActualHeight;
            detail.Visibility = Visibility.Visible;
            detailScroll.ScrollToTop();
            FillDetail();
            ResizeHost(fromHeight, DetailHeight());
            // 처음 그린 뒤(글꼴·아이콘·줄 배치가 끝난 뒤) 한 번 더 잼
            _ = Dispatcher.BeginInvoke(FitDetail, DispatcherPriority.Loaded);
            double w = PageWidth();
            if (animate)
            {
                Slide(mainShift, 0, -w);
                Slide(detailShift, w, 0);
            }
            else
            {
                mainShift.BeginAnimation(TranslateTransform.XProperty, null);
                detailShift.BeginAnimation(TranslateTransform.XProperty, null);
                mainShift.X = -w;
                detailShift.X = 0;
            }
            main.IsHitTestVisible = false;
            detail.IsHitTestVisible = true;
        }

        bool Back()
        {
            if (page == SoundPage.None) return false;
            page = SoundPage.None;
            double w = PageWidth();
            main.IsHitTestVisible = true;
            detail.IsHitTestVisible = false;
            main.Measure(new Size(w, double.PositiveInfinity));
            ResizeHost(host.ActualHeight > 0 ? host.ActualHeight : main.DesiredSize.Height, Math.Ceiling(main.DesiredSize.Height), () =>
            {
                if (page == SoundPage.None) host.Height = double.NaN; // 첫 화면은 다시 내용대로 (재생 카드가 생기고 사라져도)
            });
            Slide(mainShift, -w, 0);
            Slide(detailShift, 0, w, () =>
            {
                if (page != SoundPage.None) return; // 그 사이 다시 엶
                detail.Visibility = Visibility.Collapsed;
            });
            RefreshAll();
            return true;
        }
        go = p => Open(p, animate: true);
        backButton.Click += (_, _) => Back();
        _back = Back;
        _openSoundPageAnimatedForTest = name =>
        {
            if (Enum.TryParse<SoundPage>(name, out var p) && p != SoundPage.None) Open(p, animate: true);
            else Back();
        };
        _openSoundPageForTest = name =>
        {
            if (Enum.TryParse<SoundPage>(name, out var p) && p != SoundPage.None) Open(p, animate: false);
            else Back();
        };

        IReadOnlyList<AudioDevice> VisibleInputs()
        {
            // "스테레오 믹스" 같은 루프백 입력은 빼되, 그게 지금 기본 장치면 맨 아래에 보여 다른 장치로 바꿀 수 있게
            var all = st.InputDevices ?? Array.Empty<AudioDevice>();
            return all.Where(d => d.Kind != AudioDeviceKind.Loopback || d.IsDefault).OrderBy(d => d.Kind == AudioDeviceKind.Loopback ? 1 : 0).ToList();
        }

        // ── 앱 목록: 판이 열려 있는 동안 1초마다 스레드 풀에서 읽음 ──
        void ApplyApps(IReadOnlyList<AppVolume> list)
        {
            apps = list;
            // 첫 화면: 지금 소리 내는 앱 1~2개 (시스템 소리 빼고)
            ApplyAppRows(topApps, list.Where(a => a.Active && !a.System).Take(2).ToList());
            appsNav.Text.Text = Loc.F($"앱별 볼륨 {list.Count}개");
            appsNav.Button.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyAppRows(allApps, list); // 세부 화면도 미리 (넘길 때 만들지 않게)
            if (page == SoundPage.Apps) FitDetail();
        }
        bool reading = false, appsFixed = false;
        async void ReadApps()
        {
            if (reading || _closed || appsFixed) return;
            reading = true;
            try
            {
                var list = await Task.Run(AppVolumeService.List);
                // 판 스레드에서 (await 가 UI 스레드로 돌아오지 않는 경우에도)
                _ = Dispatcher.BeginInvoke(() => { if (!_closed && !appsFixed) ApplyApps(list); });
            }
            catch (Exception ex) { Log.Warn($"앱 볼륨 읽기 실패: {ex.GetType().Name}"); }
            finally { reading = false; }
        }
        // 시험 그림·둘러보기 장면: 받은 목록으로 고정 (뒤에서 읽은 실제 목록이 덮지 않게)
        _applyAppVolumeForTest = list => { appsFixed = true; ApplyApps(list); };

        _refreshers.Add(() =>
        {
            // 출력
            bool muted = st.Muted;
            outSwitch.IsChecked = !muted;
            outBody.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
            outMuted.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            if (!slider.IsDragging) slider.Value = st.Volume;
            var outs = st.OutputDevices ?? Array.Empty<AudioDevice>();
            var outDefault = outs.FirstOrDefault(d => d.IsDefault);
            outNav.Text.Text = outDefault is null ? Loc.T("재생 장치 없음") : Loc.F($"스피커: {ShortDeviceName(outDefault.Name)}");
            // 입력
            var ins = st.InputDevices ?? Array.Empty<AudioDevice>();
            bool anyInput = ins.Any(d => d.Kind != AudioDeviceKind.Loopback);
            inSection.Visibility = anyInput ? Visibility.Visible : Visibility.Collapsed;
            bool inOff = st.InputMuted;
            inSwitch.IsChecked = !inOff;
            inSwitch.IsEnabled = st.HasInput;
            inBody.Visibility = inOff ? Visibility.Collapsed : Visibility.Visible;
            inMuted.Visibility = inOff && anyInput ? Visibility.Visible : Visibility.Collapsed;
            inControls.Visibility = st.HasInput ? Visibility.Visible : Visibility.Collapsed;
            if (!inSlider.IsDragging) inSlider.Value = st.InputVolume;
            meterOn(anyInput && st.HasInput && !inOff && IsLoaded);
            var inDefault = ins.FirstOrDefault(d => d.IsDefault);
            inNav.Text.Text = inDefault is null ? Loc.T("입력 장치 없음") : Loc.F($"마이크: {ShortDeviceName(inDefault.Name)}");
            // 세부 화면
            FillDevices();
            if (page is SoundPage.Output or SoundPage.Input) FitDetail();
            if (anyInput) ReadInputUse();
            ReadApps();
        });
        return host;
    }

    /// <summary>「음소거됨」 한 줄 (스위치를 끄면 그 아래 내용 대신).</summary>
    private TextBlock MutedLine()
    {
        var t = Sub(Loc.T("음소거됨"), 13.5);
        t.Margin = new Thickness(0, 2, 0, 2);
        t.Visibility = Visibility.Collapsed;
        return t;
    }

    /// <summary>「글자 ›」 한 줄 버튼 — 누르면 판 안 세부 화면으로.</summary>
    private (Button Button, TextBlock Text) NavRow(Action open)
    {
        var text = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock { Text = ChevronRightGlyph, FontFamily = IconFont, FontSize = 11, Foreground = _p.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chevron, Dock.Right);
        dock.Children.Add(chevron);
        dock.Children.Add(text);
        var b = new Button
        {
            Style = (Style)FindStyle("PanelRowButton"),
            Content = dock,
            Foreground = _p.Text,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 4 + TouchSupport.RowPad, 6, 4 + TouchSupport.RowPad),
            Margin = new Thickness(-6, 0, -6, 0),
        };
        b.Click += (_, _) =>
        {
            try { open(); }
            catch (Exception ex) { Log.Error("소리 판 세부 화면 열기 실패", ex); }
        };
        return (b, text);
    }

    /// <summary>
    /// 입력 음량 막대 + 레벨 점 10개 (맥 사운드 설정처럼 — 0.4초 동안의 최댓값을 0.2초마다 한 칸 단위로만 바꿈, 옅은 글자색).
    /// 돌려주는 meterOn(true/false) 로 레벨 읽기를 켜고 끔 (입력 구역이 보이고 마이크가 켜져 있을 때만).
    /// </summary>
    private (UIElement Controls, PillSlider Slider, Action<bool> MeterOn) BuildInputControls()
    {
        var st = _services.Status;
        var controls = new StackPanel();
        var slider = new PillSlider(_p) { WheelAdjusts = false, Margin = new Thickness(0, 6, 0, 2) };
        slider.UserChanged += (_, v) =>
        {
            try
            {
                if (st.InputMuted && v > 0) st.SetInputMuted(false);
                st.SetInputVolume(v);
            }
            catch (Exception ex) { Log.Error("입력 음량 설정 실패", ex); }
        };
        controls.Children.Add(slider);
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 5, 0, 2), ToolTip = Loc.T("입력 레벨") };
        var dotList = new List<Ellipse>();
        for (int i = 0; i < InputMeterDots; i++)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 6, 0), Fill = _p.SubText, Opacity = 0.18 };
            dotList.Add(dot);
            dots.Children.Add(dot);
        }
        controls.Children.Add(dots);

        int lit = 0, sampleCount = 0;
        double windowMax = 0;
        var meterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = InputMeterSample };
        meterTimer.Tick += (_, _) =>
        {
            if (_closed) { meterTimer.Stop(); return; }
            double peak = st.InputMuted ? 0 : st.GetInputPeak();
            windowMax = Math.Max(windowMax, MeterLevel(peak));
            if (++sampleCount < InputMeterShowEvery) return;
            int target = (int)Math.Round(windowMax * InputMeterDots);
            sampleCount = 0;
            windowMax = 0;
            // 오를 땐 바로, 내릴 땐 한 번에 한 칸씩 (뚝뚝 끊기지 않게)
            int next = target >= lit ? target : lit - 1;
            if (next == lit) return;
            lit = next;
            for (int i = 0; i < dotList.Count; i++) dotList[i].Opacity = i < lit ? 0.65 : 0.18;
        };
        Closed += (_, _) => meterTimer.Stop();
        void MeterOn(bool on)
        {
            if (on && !meterTimer.IsEnabled) meterTimer.Start();
            else if (!on && meterTimer.IsEnabled) meterTimer.Stop();
        }
        return (controls, slider, MeterOn);
    }
}
