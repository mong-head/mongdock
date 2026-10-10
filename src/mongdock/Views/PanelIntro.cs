using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 앱 모음 판이 나타나는 모양 (#24, 사용자에게 고르게 할 두 가지 — 닫기는 둘 다 즉시):
/// - center: 화면 가운데에서 0.9 → 1.0 + 페이드, 160ms ease-out (되튐 없음)
/// - icon:   독의 앱 모음 아이콘 자리에서 작게 시작해 가운데로 옮겨 가며 커짐, 200ms ease-out (되튐 없음)
/// - fade:   예전 그대로 (제자리 0.5 → 1.0 페이드 120ms)
/// 판(일반 창 — 크기를 매 프레임 바꾸면 번쩍임)은 화면 밖에서 다 그려 두고, 그 그림을 가벼운 투명 창에서 움직인 뒤
/// 끝나는 순간 진짜 판을 제자리에 놓고 그림 창을 닫음. 고르기: 환경 변수 MONGDOCK_ALLAPPS_ANIM 또는 settings.json "allApps"."openAnimation".
/// </summary>
/// <summary>설정 창 고르기용 (임시 — 사용자가 둘 중 고르는 동안).</summary>
internal enum PanelIntroStyle { Center, Icon }

internal static class PanelIntro
{
    public const string Center = "center", Icon = "icon", Fade = "fade";

    public static string Mode(Models.Settings settings)
    {
        string? m = Environment.GetEnvironmentVariable("MONGDOCK_ALLAPPS_ANIM") ?? settings.AllApps.OpenAnimation;
        return m?.Trim().ToLowerInvariant() switch
        {
            Icon => Icon,
            Fade => Fade,
            _ => Center,
        };
    }

    public static int DurationMs(string mode) => mode == Icon ? 200 : 160;

    private static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// <summary>t(0~1) 에서 판 그림이 놓일 자리(DIP)와 불투명도. 시연 그림(report-test)도 이것으로 그림.</summary>
    public static (Rect Rect, double Opacity) Sample(string mode, double t, Rect final, Rect icon)
    {
        double e = EaseOut(t);
        if (mode == Icon && !icon.IsEmpty)
        {
            double w0 = Math.Max(icon.Width, icon.Height) * 1.1, h0 = w0 * final.Height / Math.Max(1, final.Width);
            var c0 = new Point(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2);
            var c1 = new Point(final.Left + final.Width / 2, final.Top + final.Height / 2);
            double w = w0 + (final.Width - w0) * e, h = h0 + (final.Height - h0) * e;
            double cx = c0.X + (c1.X - c0.X) * e, cy = c0.Y + (c1.Y - c0.Y) * e;
            return (new Rect(cx - w / 2, cy - h / 2, w, h), Math.Min(1, 0.35 + e));
        }
        double s = 0.9 + 0.1 * e;
        double sw = final.Width * s, sh = final.Height * s;
        return (new Rect(final.Left + (final.Width - sw) / 2, final.Top + (final.Height - sh) / 2, sw, sh), e);
    }

    /// <summary>판 그림을 움직임. 끝나면 done (진짜 판을 제자리에) — 그 다음 프레임에 그림 창을 닫음.</summary>
    public static Window Play(AppServices services, string mode, ImageSource picture, Rect final, Rect icon, Action done)
    {
        var area = final;
        if (mode == Icon && !icon.IsEmpty) area.Union(icon);
        area.Inflate(28, 28); // 그림자 자리
        var shape = new Rectangle
        {
            Fill = new ImageBrush(picture) { Stretch = Stretch.Fill },
            RadiusX = 8,
            RadiusY = 8,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
        };
        var canvas = new System.Windows.Controls.Canvas { Width = area.Width, Height = area.Height };
        canvas.Children.Add(shape);
        var ghost = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Focusable = false,
            IsHitTestVisible = false,
            Title = "mongdock Panel Intro",
            Left = area.Left,
            Top = area.Top,
            Width = area.Width,
            Height = area.Height,
            Content = canvas,
        };
        void Apply(double t)
        {
            var (r, opacity) = Sample(mode, t, final, icon);
            System.Windows.Controls.Canvas.SetLeft(shape, r.Left - area.Left);
            System.Windows.Controls.Canvas.SetTop(shape, r.Top - area.Top);
            shape.Width = Math.Max(1, r.Width);
            shape.Height = Math.Max(1, r.Height);
            shape.RadiusX = shape.RadiusY = 8 * r.Width / Math.Max(1, final.Width);
            shape.Opacity = opacity;
        }
        Apply(0);
        ghost.SourceInitialized += (_, _) => services.DesktopWindows.MakeOverlay(ghost);
        var clock = new Stopwatch();
        var total = Stopwatch.StartNew(); // 보이기 요청부터 (QA 프레임 로그)
        long firstFrame = -1, lastTick = -1, maxGap = 0, placedAt = -1;
        int frames = 0;
        int duration = DurationMs(mode);
        bool finished = false;
        void Frame(object? s, EventArgs e)
        {
            if (!ghost.IsVisible) { CompositionTarget.Rendering -= Frame; return; } // 판이 먼저 닫힘 (Esc 등)
            long now = total.ElapsedMilliseconds;
            if (lastTick >= 0) maxGap = Math.Max(maxGap, now - lastTick);
            lastTick = now;
            frames++;
            double t = clock.ElapsedMilliseconds / (double)duration;
            Apply(t);
            if (t < 1 || finished) return;
            finished = true;
            CompositionTarget.Rendering -= Frame;
            try { done(); }
            catch (Exception ex) { Log.Error("판 나타나기 끝 처리 실패", ex); }
            placedAt = total.ElapsedMilliseconds;
            // 진짜 판이 화면에 나간 다음에 그림 창을 닫음 (사이에 빈 프레임 없게)
            var close = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(34) };
            close.Tick += (_, _) =>
            {
                close.Stop();
                if (ghost.IsVisible) ghost.Close();
                // 프레임 로그 (번쩍임 확인용): 그림 창 첫 프레임 · 움직인 프레임 수 · 가장 긴 프레임 간격 · 진짜 판을 놓은 때 · 그림 창을 닫은 때
                Log.Info($"앱 모음 판 나타나기({mode}): 그림 창 첫 프레임 {firstFrame}ms, 프레임 {frames}개(가장 긴 간격 {maxGap}ms), 진짜 판 제자리 {placedAt}ms, 그림 창 닫음 {total.ElapsedMilliseconds}ms");
            };
            close.Start();
        }
        ghost.ContentRendered += (_, _) =>
        {
            firstFrame = total.ElapsedMilliseconds;
            clock.Start();
            CompositionTarget.Rendering += Frame;
        };
        ghost.Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= Frame;
            if (!finished) Log.Info($"앱 모음 판 나타나기({mode}): 도중에 닫힘 ({total.ElapsedMilliseconds}ms, 프레임 {frames}개)");
        };
        ghost.Show();
        return ghost;
    }
}
