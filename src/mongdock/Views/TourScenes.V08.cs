using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 0.8 새 기능 장면 (#21 앱별 볼륨, #20 창 최소화 효과) — 업데이트 안내 카드 그림.
/// 앱별 볼륨은 실제 소리 판 코드를 예시 앱으로, 창 최소화는 실제 지니 모양 계산(MinimizeEffectController.GenieStrip)으로 창 그림을 띠로 그림.
/// </summary>
internal static partial class TourScenes
{
    private sealed partial class Scene
    {
        // ── 앱별 볼륨 ──

        private IReadOnlyList<AppVolume> SceneApps(double music) => new List<AppVolume>
        {
            new("tour:browser", Loc.T("브라우저"), null, null, new uint[] { 1 }, 0.8, false, true, false),
            new("tour:music", Loc.T("음악"), null, null, new uint[] { 2 }, music, false, true, false),
            new("tour:video", Loc.T("동영상"), null, null, new uint[] { 3 }, 0.6, true, false, false),
            new(AppVolumeService.SystemKey, Loc.T("시스템 소리"), null, null, new uint[] { 0 }, 1.0, false, false, true),
        };

        /// <summary>예시 앱으로 채운 소리 판 그림 + 「음악」 막대의 손잡이 자리(판 기준 DIP).</summary>
        private ImageSource? SoundPanelWithApps(double music, out Size size, out Point? musicThumb)
        {
            size = default;
            musicThumb = null;
            StatusPanelWindow? w = null;
            StatusPanelWindow.SceneIconForApp = a => a.Key.StartsWith("tour:", StringComparison.Ordinal) ? Icon(a.Key[5..]) : null;
            try
            {
                w = new StatusPanelWindow(_services, StatusPanelKind.Volume, _p, 0);
                w.RefreshForScene();
                w.FillAppVolumeForScene(SceneApps(music));
                var content = (FrameworkElement)w.Content;
                content.Opacity = 1;
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                size = content.DesiredSize;
                content.Arrange(new Rect(size));
                content.UpdateLayout();
                var row = Descendants(content).OfType<DockPanel>().FirstOrDefault(d => Equals(d.ToolTip, Loc.T("음악")));
                if (row?.Children.OfType<PillSlider>().FirstOrDefault() is { } slider)
                    musicThumb = slider.TranslatePoint(new Point(Math.Max(10, slider.ActualWidth * music), slider.ActualHeight / 2), content);
                else _problems?.Add("소리 판 「음악」 막대를 못 찾음");
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
                rtb.Render(content);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex)
            {
                _problems?.Add($"소리 판 그리기 실패: {ex.GetType().Name}");
                return null;
            }
            finally
            {
                StatusPanelWindow.SceneIconForApp = null;
                w?.Close();
            }
        }

        private void AppVolume(Grid g, Canvas top, int f)
        {
            var bar = Bar();
            g.Children.Add(bar);
            Layout(g);
            double barBottom = bar.ActualHeight;
            if (f < 2)
            {
                // ①② 소리 판의 앱 볼륨: 음악 막대를 잡아 줄임
                double music = f == 0 ? 0.6 : 0.2;
                var img = SoundPanelWithApps(music, out var size, out var thumb);
                if (img is null) return;
                const double k = 0.92;
                var pic = new Image { Source = img, Width = size.Width * k, Height = size.Height * k };
                double left = W - size.Width * k - 18, topY = barBottom + 4;
                Canvas.SetLeft(pic, left);
                Canvas.SetTop(pic, topY);
                top.Children.Add(pic);
                if (thumb is { } t) Cursor(top, new Point(left + t.X * k, topY + t.Y * k), pressed: f == 1);
                return;
            }
            // ③ 볼륨 키 → 오른쪽 위 몽독 볼륨 표시 + 누른 키
            var osd = VolumeCard(0.45);
            osd.Measure(new Size(W, H));
            Place(top, osd, W - osd.DesiredSize.Width - 16, barBottom + 10);
            var key = KeyPlate(new[] { "🔊" });
            key.Measure(new Size(W, H));
            Place(top, key, (W - key.DesiredSize.Width) / 2, H / 2 - key.DesiredSize.Height / 2 + 20);
        }

        /// <summary>몽독 볼륨 표시 모양 (VolumeOsdWindow 과 같은 모습).</summary>
        private Border VolumeCard(double v)
        {
            var glyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, Width = 24, Foreground = _p.Text, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = _p.AccentFill, HorizontalAlignment = HorizontalAlignment.Left, Width = 122 * v };
            var track = new Border { Height = 6, Width = 122, CornerRadius = new CornerRadius(3), Background = _p.SliderTrack, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), Child = fill };
            var pct = new TextBlock { Text = $"{Math.Round(v * 100):0}%", FontSize = 13, Foreground = _p.SubText, Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(glyph);
            row.Children.Add(track);
            row.Children.Add(pct);
            return new Border
            {
                Width = 240,
                Height = 52,
                CornerRadius = new CornerRadius(14),
                Background = _p.CardBackground,
                BorderBrush = _p.CardBorder,
                BorderThickness = new Thickness(0.75),
                Child = row,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
            };
        }

        // ── 창 최소화 효과 ──

        private void Minimize(Grid g, Canvas top, int f)
        {
            var d = BigDock(g, running: new[] { "notes", "browser" });
            var win = MockWindow("notes", 300, 200);
            win.HorizontalAlignment = HorizontalAlignment.Center;
            win.VerticalAlignment = VerticalAlignment.Top;
            win.Margin = new Thickness(0, 56, 0, 0);
            g.Children.Add(win);
            Layout(g);
            var icon = Need(d.Views[Index(d, "notes")], "독 메모 아이콘")!;
            var from = new Rect(win.TranslatePoint(new Point(0, 0), g), new Size(win.ActualWidth, win.ActualHeight));
            var iconRect = new Rect(icon.TranslatePoint(new Point(0, 0), g), icon.TranslatePoint(new Point(icon.ActualWidth, icon.ActualHeight), g));
            if (f == 0)
            {
                // ① 창의 최소화 단추를 누름
                var minus = new Border { Width = 26, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)), Child = new TextBlock { Text = "—", FontSize = 11, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                Place(top, minus, from.Right - 34, from.Top + 5);
                Cursor(top, new Point(from.Right - 22, from.Top + 14), pressed: true);
                return;
            }
            // ②③ 지니: 창 그림을 띠 24장으로 나눠 독 아이콘으로 (실제 효과와 같은 계산)
            win.Effect = null;
            // 창 그림: 자리(오프셋)와 상관없이 창 내용만 (VisualBrush — 판 그림 뜨기와 같은 방식)
            var shot = new RenderTargetBitmap((int)(from.Width * Scale), (int)(from.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            var shotVisual = new DrawingVisual();
            using (var sdc = shotVisual.RenderOpen())
                sdc.DrawRectangle(new VisualBrush(win) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(from.Size));
            shot.Render(shotVisual);
            shot.Freeze();
            g.Children.Remove(win);
            double t = f == 1 ? 0.45 : 0.78;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                const int strips = 24;
                for (int i = 0; i < strips; i++)
                {
                    double v0 = (double)i / strips, v1 = (double)(i + 1) / strips;
                    var r = MinimizeEffectController.GenieStrip(from, iconRect, v0, v1, t);
                    var brush = new ImageBrush(shot) { Viewbox = new Rect(0, v0, 1, v1 - v0), ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };
                    dc.DrawRectangle(brush, null, new Rect(r.X, r.Y, r.Width, r.Height + 0.6));
                }
            }
            var genie = new RenderTargetBitmap((int)(W * Scale), (int)(H * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            genie.Render(dv);
            genie.Freeze();
            var img = new Image { Source = genie, Width = W, Height = H, IsHitTestVisible = false };
            Canvas.SetLeft(img, 0);
            Canvas.SetTop(img, 0);
            top.Children.Insert(0, img);
        }
    }
}
