using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mongdock.Models;
using Mongdock.Services;

namespace ReportTest;

/// <summary>
/// --license [DIR]: 스토어 체험판·구매(#5)·스토어판 업데이트 점검. 스토어·실제 입력 없이:
/// 1) 규칙 — 오프라인 캐시·7일 유예 표, 남은 날 셈, 출시 빌드에서 시험 스위치가 먹지 않고 exe 에 이름도 없는지.
/// 2) DIR 이 있으면 그림 — 설정 → 정보(체험 12일·3일·끝, 업데이트 줄 상태들), 3일 남음 카드, 체험 끝 카드. 언어 × 라이트·다크.
///    그림마다 그 상태에 있어야 할 글자(구매 구역·버튼)가 화면에 있는지도 봄. 하나라도 틀리면 종료 코드 1.
/// </summary>
internal static class LicenseCheck
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int _failed;

    public static int Run(string? dir, string[] langs)
    {
        Rules();
        if (dir is not null) Pictures(dir, langs);
        Console.WriteLine(_failed == 0 ? "라이선스 점검: 통과" : $"라이선스 점검: 실패 {_failed}개");
        return _failed == 0 ? 0 : 1;
    }

    private static void Expect(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "통과" : "실패")}  {what}");
        if (!ok) _failed++;
    }

    // ───────────────────────── 규칙 ─────────────────────────

    private static void Rules()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(9));
        var fromCache = typeof(LicenseService).GetMethod("FromCacheAt", Any)!;
        LicenseInfo C(LicenseState s, DateTimeOffset? exp, DateTimeOffset checkedAt) => (LicenseInfo)fromCache.Invoke(null, new object?[] { s, exp, checkedAt, now })!;
        void Row(string what, LicenseInfo got, LicenseState state, int? days = null)
            => Expect(got.State == state && (days is null || got.DaysLeft == days), $"오프라인 캐시: {what} → {got.State} {got.DaysLeft}일 ({got.Source})");

        Row("체험 5일 남음", C(LicenseState.Trial, now.AddDays(5), now.AddDays(-1)), LicenseState.Trial, 5);
        Row("체험 끝나고 1일 (유예 중)", C(LicenseState.Trial, now.AddDays(-1), now.AddDays(-3)), LicenseState.Full);
        Row("체험 끝나고 6일 23시간 (유예 중)", C(LicenseState.Trial, now.AddDays(-7).AddHours(1), now.AddDays(-10)), LicenseState.Full);
        Row("체험 끝나고 8일 (유예 끝 → 잠금)", C(LicenseState.Trial, now.AddDays(-8), now.AddDays(-20)), LicenseState.Expired);
        Row("만료로 확인되고 2일", C(LicenseState.Expired, null, now.AddDays(-2)), LicenseState.Full);
        Row("만료로 확인되고 7일 1분 (잠금)", C(LicenseState.Expired, null, now.AddDays(-7).AddMinutes(-1)), LicenseState.Expired);
        Row("정식, 100일 확인 못 함", C(LicenseState.Full, null, now.AddDays(-100)), LicenseState.Full);
        Row("체험인데 만료 시각 모름", C(LicenseState.Trial, null, now.AddDays(-1)), LicenseState.Trial, -1);

        var makeAt = typeof(LicenseService).GetMethod("MakeAt", Any)!;
        int Days(TimeSpan left) => ((LicenseInfo)makeAt.Invoke(null, new object?[] { LicenseState.Trial, now + left, "t", now })!).DaysLeft;
        Expect(Days(TimeSpan.FromDays(15)) == 15, $"남은 날: 막 시작한 15일 체험 = {Days(TimeSpan.FromDays(15))}일");
        Expect(Days(TimeSpan.FromHours(2)) == 1, $"남은 날: 2시간 남음 = {Days(TimeSpan.FromHours(2))}일 (올림)");
        Expect(Days(TimeSpan.FromHours(-2)) == 0, $"남은 날: 지남 = {Days(TimeSpan.FromHours(-2))}일");
        Expect(LicenseService.StoreId == "9NQ3NKW93F3M", "스토어 ID 9NQ3NKW93F3M");

        // 출시 빌드: 시험 스위치를 켜 두어도 일반판은 정식·관리 안 함, 스토어 업데이트도 안 만듦, exe 에 이름 없음
        bool debug = typeof(Mongdock.App).Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>()?.IsJITOptimizerDisabled == true;
        Environment.SetEnvironmentVariable("MONGDOCK_FAKE_LICENSE", "expired");
        Environment.SetEnvironmentVariable("MONGDOCK_FAKE_STOREUPDATE", "available");
        try
        {
            var lic = new LicenseService(() => IntPtr.Zero);
            if (debug)
            {
                Console.WriteLine("  (Debug 빌드 — 시험 스위치가 먹는 쪽을 봄. 출시 빌드 점검은 -c Release 로 다시)");
                Expect(lic.Current.State == LicenseState.Expired && lic.Managed, $"Debug: MONGDOCK_FAKE_LICENSE=expired → {lic.Current.State}");
                Expect(StoreUpdateService.Wanted, "Debug: MONGDOCK_FAKE_STOREUPDATE → 스토어 업데이트 만듦");
            }
            else
            {
                Expect(lic.Current.State == LicenseState.Full && !lic.Managed, $"출시 빌드: MONGDOCK_FAKE_LICENSE=expired 무시 → {lic.Current.State} ({lic.Current.Source}), 관리 {lic.Managed}");
                Expect(!StoreUpdateService.Wanted, "출시 빌드: MONGDOCK_FAKE_STOREUPDATE 무시 → 스토어 업데이트 안 만듦 (일반판)");
                byte[] dll = File.ReadAllBytes(typeof(Mongdock.App).Assembly.Location);
                foreach (string name in new[] { "MONGDOCK_FAKE_LICENSE", "MONGDOCK_FAKE_STOREUPDATE" })
                    Expect(IndexOf(dll, System.Text.Encoding.Unicode.GetBytes(name)) < 0, $"출시 빌드 mongdock.dll 에 '{name}' 없음");
            }
            lic.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MONGDOCK_FAKE_LICENSE", null);
            Environment.SetEnvironmentVariable("MONGDOCK_FAKE_STOREUPDATE", null);
        }
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    // ───────────────────────── 그림 ─────────────────────────

    private sealed record Scene(string Name, LicenseInfo License, StoreUpdateState Update, string[] MustShow, string[] MustNotShow);

    private static void Pictures(string dir, string[] langs)
    {
        Directory.CreateDirectory(dir);
        var app = new Mongdock.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var asm = typeof(Mongdock.App).Assembly;
        var settingsType = asm.GetType("Mongdock.Views.SettingsWindow")!;
        var licenseUi = asm.GetType("Mongdock.Views.LicenseUi")!;
        var cardType = asm.GetType("Mongdock.Views.ConfirmCardWindow")!;
        var now = DateTimeOffset.Now;
        var store = new StoreUpdateService(() => IntPtr.Zero, () => false); // Instance — 정보 페이지가 스토어판 업데이트 구역을 그림
        var setStore = typeof(StoreUpdateService).GetMethod("SetForTest", Any)!;
        var setLicense = typeof(LicenseService).GetMethod("SetForTest", Any)!;

        foreach (string lang in langs)
        {
            Mongdock.Loc.Init(lang);
            string T(string k) => Mongdock.Loc.T(k);
            var scenes = new[]
            {
                new Scene("trial12-notchecked", new LicenseInfo(LicenseState.Trial, 12, now.AddDays(12), "test"), StoreUpdateState.Unknown,
                    new[] { Mongdock.Loc.F($"체험판 · {12}일 남음"), T("구매하기"), T("Microsoft Store 에서 업데이트돼요"), T("스토어에서 업데이트 확인") },
                    new[] { T("자동으로 업데이트 확인") }),
                new Scene("trial3-available", new LicenseInfo(LicenseState.Trial, 3, now.AddDays(3), "test"), StoreUpdateState.Available,
                    new[] { Mongdock.Loc.F($"체험판 · {3}일 남음"), Mongdock.Loc.F($"새 버전 v{"0.7.1"} 이 있어요"), T("업데이트") }, Array.Empty<string>()),
                new Scene("expired-failed", new LicenseInfo(LicenseState.Expired, 0, null, "test"), StoreUpdateState.Failed,
                    new[] { T("체험이 끝났어요"), T("구매하기"), T("확인하지 못했어요"), T("스토어 열기") }, Array.Empty<string>()),
                new Scene("full-uptodate", new LicenseInfo(LicenseState.Full, 0, null, "test"), StoreUpdateState.UpToDate,
                    new[] { T("최신 버전이에요"), T("자동 업데이트") }, new[] { T("구매하기") }),
                new Scene("full-installing", new LicenseInfo(LicenseState.Full, 0, null, "test"), StoreUpdateState.Installing,
                    new[] { T("업데이트하는 중…") }, new[] { T("구매하기") }),
            };
            foreach (string theme in new[] { "light", "dark" })
            {
                var settings = new SettingsService();
                settings.Current.Dock.Theme = theme == "dark" ? DockTheme.Dark : DockTheme.Light;
                Mongdock.ViewModels.UiTheme.Apply(settings.Current);
                Mongdock.ViewModels.UiFonts.Apply(settings.Current);
                var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
                var tracker = new WindowTracker();
                var launcher = new AppLauncher(tracker);
                var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
                    new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
                    new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
                var license = new LicenseService(() => IntPtr.Zero);
                licenseUi.GetField("_license", Any)!.SetValue(null, license);

                var w = (Window)settingsType.GetConstructor(Any, new[] { typeof(AppServices) })!.Invoke(new object[] { services });
                w.Left = -20000; w.Top = -20000; w.ShowActivated = false;
                w.Show();
                var pageType = settingsType.GetNestedType("Page", BindingFlags.NonPublic)!;
                settingsType.GetMethod("GoToPage", Any)!.Invoke(w, new[] { Enum.Parse(pageType, "About") });
                var rebuild = settingsType.GetMethod("Rebuild", Any)!;
                foreach (var sc in scenes)
                {
                    setLicense.Invoke(license, new object[] { sc.License });
                    setStore.Invoke(store, new object?[] { sc.Update, sc.Update is StoreUpdateState.Available or StoreUpdateState.Installing ? new Version(0, 7, 1) : null, now });
                    rebuild.Invoke(w, null);
                    w.UpdateLayout(); // 디스패처를 돌리지 않음 (App.OnStartup 이 돌면 안 됨)
                    var texts = Descendants(w).OfType<TextBlock>().Select(t => t.Text).Concat(Descendants(w).OfType<Button>().Select(b => b.Content as string ?? "")).ToList();
                    foreach (string must in sc.MustShow)
                        Expect(texts.Any(t => t.Contains(must, StringComparison.Ordinal)), $"{lang} {theme} 정보 {sc.Name}: '{must}' 보임");
                    foreach (string not in sc.MustNotShow)
                        Expect(!texts.Any(t => t == not), $"{lang} {theme} 정보 {sc.Name}: '{not}' 안 보임");
                    SavePage(w, Path.Combine(dir, $"license-about-{sc.Name}-{lang}-{theme}.png"));
                }
                w.Close();

                // 카드 (LicenseUi 와 같은 문구)
                Window Card(string title, string msg, string ok, string cancel) =>
                    (Window)cardType.GetConstructors(Any)[0].Invoke(new object?[] { services, palette, title, msg, ok, (Action)(() => { }), cancel, null, null, true });
                var backdrop = new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x24, 0x24, 0x2C) : Color.FromRgb(0xE4, 0xE6, 0xEE));
                foreach (var (name, card) in new[]
                {
                    ("trial3", Card(Mongdock.Loc.F($"체험이 {3}일 남았어요"), T("체험이 끝나면 독과 상단바가 멈춰요. 지금 구매하면 그대로 계속 쓸 수 있어요."), T("구매하기"), T("나중에"))),
                    ("expired", Card(T("체험이 끝났어요"), T("몽독을 계속 쓰려면 구매해 주세요. 작업 표시줄과 알림은 원래대로 돌려 두었어요. 트레이 아이콘을 누르면 이 창을 다시 열 수 있어요."), T("구매하기"), T("나중에"))),
                })
                {
                    var content = (FrameworkElement)card.Content;
                    card.Content = null;
                    var texts = Descendants(content).OfType<TextBlock>().Select(t => t.Text).ToList();
                    Expect(texts.Count > 0 || content is not null, $"{lang} {theme} 카드 {name}");
                    SaveElement(new Border { Background = backdrop, Child = content }, Path.Combine(dir, $"license-card-{name}-{lang}-{theme}.png"));
                    card.Close();
                }
                license.Dispose();
                settings.Dispose();
            }
        }
        store.Dispose();
        licenseUi.GetField("_license", Any)!.SetValue(null, null);
        GC.KeepAlive(app);
    }

    private static void SavePage(Window w, string path)
    {
        var scroll = Descendants(w).OfType<ScrollViewer>().OrderByDescending(s => s.ActualWidth * s.ActualHeight).FirstOrDefault();
        if (scroll?.Content is not FrameworkElement body) { Expect(false, $"정보 페이지 본문 찾기 ({path})"); return; }
        double width = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
        body.Measure(new Size(width, double.PositiveInfinity));
        double height = Math.Max(body.DesiredSize.Height, 50);
        body.Arrange(new Rect(0, 0, width, height));
        body.UpdateLayout();
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(body) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.None }, null, new Rect(0, 0, width, height));
        }
        Write(dv, width, height, path);
    }

    private static void SaveElement(FrameworkElement el, string path)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        el.Arrange(new Rect(el.DesiredSize));
        el.UpdateLayout();
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None }, null, new Rect(el.DesiredSize));
        Write(dv, el.DesiredSize.Width, el.DesiredSize.Height, path);
    }

    private static void Write(Visual v, double width, double height, string path)
    {
        const double scale = 1.25;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        Console.WriteLine($"  저장  {path}");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }
}
