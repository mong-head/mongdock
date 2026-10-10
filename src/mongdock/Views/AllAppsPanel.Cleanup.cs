using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// "안 쓰는 앱 정리" (#24, 2026-10-10): 즐겨찾기·폴더·독 핀 중 60일 넘게 안 쓴 앱(또는 쓴 적 없는 앱)을 골라
/// [그대로] [즐겨찾기·폴더·독에서 빼기](기본) [판에서 숨기기] [Windows에서 제거…] 중 하나로.
/// - 한 달에 한 번, 판을 열 때 3개 이상이면 맨 위에 작은 띠 (× = 다음 달까지 안 띄움).
/// - 쓴 적 없는 독 핀은 기록을 시작하고(씨앗) 30일이 지나기 전엔 빼고 봄.
/// - "Windows에서 제거…"는 설정의 앱 목록을 여는 것뿐 — 몽독이 직접 지우지 않음.
/// - 실행 기록("자주 쓰는 앱 기록")이 꺼져 있으면 이 기능도 꺼짐.
/// </summary>
internal sealed partial class AllAppsPanel
{
    private const int StaleDays = 60, MinStale = 3, NewPinGraceDays = 30;

    private enum CleanupChoice { Keep, Remove, Hide, Uninstall }

    /// <summary>시험 그림에서만: "안 쓴 지" 기준 날 수 (이 셸 기록엔 두 달 넘은 앱이 없어서).</summary>
    internal static int? StaleDaysForTest { get; set; }

    private sealed class CleanupRow
    {
        public required string Name { get; init; }
        public AppEntry? App { get; init; }
        public PinItem? Pin { get; init; }
        public required string Key { get; init; }
        public DateTime? Last { get; init; }
        public CleanupChoice Choice { get; set; } = CleanupChoice.Remove;
    }

    private static string ThisMonth => DateTime.Now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>정리 후보: 즐겨찾기·폴더·독 핀에 있는 앱 중 60일 넘게 안 쓴 것(같은 앱은 한 줄).</summary>
    private List<CleanupRow> CleanupCandidates()
    {
        var settings = Services.Settings.Current;
        var rows = new Dictionary<string, CleanupRow>();
        var cutoff = DateTime.Now.AddDays(-(StaleDaysForTest ?? StaleDays));
        bool pinGrace = settings.AllApps.UsageSeededAt is not { } seeded || DateTime.Now - seeded < TimeSpan.FromDays(NewPinGraceDays);
        var byKey = _apps.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);

        // 지금 창이 떠 있는 앱은 쓰는 중 — 후보에서 뺌
        var running = Services.Windows.Windows.Select(w => System.IO.Path.GetFileNameWithoutExtension(w.ProcessPath).ToLowerInvariant())
            .Where(e => e.Length > 0).Select(e => "exe:" + e).ToHashSet();
        var dockPins = settings.Pins.Where(p => p.Kind is PinKind.Exe or PinKind.Aumid && !string.IsNullOrWhiteSpace(p.Target)).ToList();
        var docked = dockPins.Select(AllAppsCatalog.Identity).OfType<string>().ToHashSet();

        void Consider(AppEntry? app, PinItem? pin)
        {
            string? id = app is not null ? AllAppsCatalog.Identity(app) : pin is not null ? AllAppsCatalog.Identity(pin) : null;
            if (id is null || running.Contains(id) || id == "exe:explorer") return; // 파일 탐색기는 늘 쓰는 시스템 앱
            string key = app is not null ? "k:" + app.Key : "p:" + id; // 같은 exe 를 쓰는 다른 앱(Squirrel update·PWA)과 섞이지 않게 앱 키로
            if (rows.ContainsKey(key)) return;
            var ids = app is not null ? AllAppsCatalog.Identities(app).Append(id) : new[] { id };
            var last = AppUsage.LastUsed(ids);
            if (last is { } l && l >= cutoff) return;
            // 독에 있는 앱은(폴더·즐겨찾기로 먼저 만나도) 기록 30일이 쌓이기 전엔 "쓴 적 없음"으로 보지 않음 — 다른 독에서 켜던 앱은 기록이 없음
            if (last is null && pinGrace && (pin is not null || docked.Contains(id))) return;
            rows[key] = new CleanupRow
            {
                Name = app?.Name ?? (string.IsNullOrWhiteSpace(pin?.Name) ? System.IO.Path.GetFileNameWithoutExtension(pin?.Target ?? "") : pin!.Name),
                App = app,
                Pin = pin,
                Key = key,
                Last = last,
            };
        }

        foreach (var k in settings.AllApps.Favorites) if (byKey.TryGetValue(k, out var a)) Consider(a, null);
        foreach (var f in _folders) foreach (var a in f.Apps) Consider(a, null);
        foreach (var pin in dockPins)
        {
            string? id = AllAppsCatalog.Identity(pin);
            // 이 핀의 앱: 같은 이름(exe·AUMID)을 쓰는 앱이 하나뿐일 때만 — 여럿이면 핀만 따로 한 줄
            var matches = _apps.Where(x => AllAppsCatalog.Identity(x) == id).Take(2).ToList();
            var app = matches.Count == 1 ? matches[0] : null;
            if (app is not null && rows.TryGetValue("k:" + app.Key, out var existing))
            {
                if (existing.Pin is null) rows[existing.Key] = new CleanupRow { Name = existing.Name, App = existing.App, Pin = pin, Key = existing.Key, Last = existing.Last };
                continue;
            }
            Consider(app, pin);
        }
        return rows.Values.OrderBy(r => r.Last ?? DateTime.MinValue).ToList();
    }

    /// <summary>맨 위 작은 띠 "한동안 안 쓴 앱이 5개 있어요 [정리하기] [×]" — 기록이 켜져 있고, 이번 달에 아직 안 닫았고, 3개 이상일 때.</summary>
    private UIElement? CleanupBanner()
    {
        if (!S.ShowSuggestions || S.CleanupPromptMonth == ThisMonth || _apps.Count == 0) return null;
        int n = _bannerCount ??= CleanupCandidates().Count; // 판을 열 때 한 번만 (다시 그릴 때마다 핀·창 목록을 돌지 않게)
        if (n < MinStale) return null;
        var row = new DockPanel { LastChildFill = true };
        var close = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = P.SubText, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0), ToolTip = Loc.T("다음 달까지 안 보여요") };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            S.CleanupPromptMonth = ThisMonth;
            Save(false);
        };
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        var go = SmallLink(Loc.T("정리하기"), OpenCleanup);
        go.Margin = new Thickness(10, 0, 0, 0);
        go.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(go, Dock.Right);
        row.Children.Add(go);
        row.Children.Add(new TextBlock { Text = Loc.F($"한동안 안 쓴 앱이 {n}개 있어요"), VerticalAlignment = VerticalAlignment.Center });
        return new Border { Background = P.Tile, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7, 10, 7), Margin = new Thickness(4, 0, 4, 8), Child = row };
    }

    private int? _bannerCount;

    private void OpenCleanup()
    {
        if (!S.ShowSuggestions) return;
        _cleanup = CleanupCandidates();
        Rebuild();
    }

    private static string LastText(DateTime? last)
    {
        if (last is not { } l) return Loc.T("쓴 적 없음");
        int days = (int)(DateTime.Now.Date - l.Date).TotalDays;
        return days < 30 ? Loc.F($"{days}일 전") : days < 365 ? Loc.F($"{days / 30}개월 전") : Loc.T("1년 넘게 안 씀");
    }

    /// <summary>정리 카드: 앱마다 마지막 사용과 [그대로] [빼기] [숨기기] [Windows에서 제거…] → [적용] [취소].</summary>
    private UIElement BuildCleanup()
    {
        var rows = _cleanup!;
        var root = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        root.Children.Add(new TextBlock { Text = Loc.T("안 쓰는 앱 정리"), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 2) });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("즐겨찾기·폴더·독에 있는 앱 중 두 달 넘게 쓰지 않은 앱이에요. 고른 대로 정리해요 — 앱 자체는 지우지 않아요."),
            Foreground = P.SubText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 10),
        });
        if (rows.Count == 0) root.Children.Add(Muted(Loc.T("정리할 앱이 없어요")));
        foreach (var r in rows)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            var choices = new StackPanel { Orientation = Orientation.Horizontal };
            void Paint()
            {
                foreach (Border b in choices.Children)
                {
                    bool on = (CleanupChoice)b.Tag == r.Choice;
                    b.Background = on ? P.SoftAccent : P.Tile;
                    b.BorderBrush = on ? P.SoftAccentLine : Brushes.Transparent;
                    ((TextBlock)b.Child).Foreground = on ? P.SoftAccentText : P.Text;
                }
            }
            foreach (var (c, label) in new[] { (CleanupChoice.Keep, Loc.T("그대로")), (CleanupChoice.Remove, Loc.T("즐겨찾기·폴더·독에서 빼기")), (CleanupChoice.Hide, Loc.T("판에서 숨기기")), (CleanupChoice.Uninstall, Loc.T("Windows에서 제거…")) })
            {
                var b = new Border { Tag = c, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand, Child = new TextBlock { Text = label, FontSize = 11.5 } };
                b.MouseLeftButtonUp += (_, e) => { e.Handled = true; r.Choice = c; Paint(); };
                choices.Children.Add(b);
            }
            Paint();
            DockPanel.SetDock(choices, Dock.Right);
            line.Children.Add(choices);
            var image = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            if (r.App is not null) LoadIcon(image, r.App);
            else if (r.Pin is not null)
            {
                try { image.Source = Services.Icons.GetIcon(r.Pin, _style); } catch { /* 아이콘 없이 */ }
            }
            var who = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            who.Children.Add(image);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = r.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 });
            names.Children.Add(new TextBlock { Text = LastText(r.Last), FontSize = 11, Foreground = P.SubText });
            who.Children.Add(names);
            line.Children.Add(who);
            root.Children.Add(line);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 4) };
        var cancel = new Button { Style = (Style)Application.Current.FindResource("CardButton"), Content = new TextBlock { Text = Loc.T("취소") }, Background = P.Tile, Foreground = P.Text, Height = 28, Padding = new Thickness(18, 0, 18, 0) };
        cancel.Click += (_, _) => { _cleanup = null; Rebuild(); };
        var apply = new Button { Style = (Style)Application.Current.FindResource("CardButton"), Content = new TextBlock { Text = Loc.T("적용"), FontWeight = FontWeights.SemiBold }, Background = P.SoftAccent, Foreground = P.SoftAccentText, Height = 28, Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(8, 0, 0, 0), IsEnabled = rows.Count > 0 };
        apply.Click += (_, _) => ApplyCleanup(rows);
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        root.Children.Add(buttons);
        return root;
    }

    private void ApplyCleanup(List<CleanupRow> rows)
    {
        var settings = Services.Settings.Current;
        bool openUninstall = false;
        int removed = 0, hidden = 0;
        foreach (var r in rows)
        {
            if (r.Choice == CleanupChoice.Keep) continue;
            if (r.Choice == CleanupChoice.Uninstall) { openUninstall = true; continue; } // 지우는 건 사용자가 윈도우 설정에서
            // 즐겨찾기·폴더·독에서 빼기 (숨기기도 같이 뺌)
            if (r.App is { } app)
            {
                S.Favorites.RemoveAll(k => k.Equals(app.Key, StringComparison.OrdinalIgnoreCase));
                foreach (var f in _folders.Where(f => f.Apps.Any(a => a.Key == app.Key)).ToList())
                    AppFolders.RemoveFrom(settings, _apps, f.Id, app.Key);
                if (r.Choice == CleanupChoice.Hide && !S.Hidden.Contains(app.Key, StringComparer.OrdinalIgnoreCase)) { S.Hidden.Add(app.Key); hidden++; }
            }
            if (r.Pin is { } pin) settings.Pins.Remove(pin);
            removed++;
        }
        S.CleanupUsed = true;
        S.CleanupPromptMonth = ThisMonth;
        _cleanup = null;
        _bannerCount = null;
        Log.Info($"안 쓰는 앱 정리: 빼기 {removed}개 (숨김 {hidden}개){(openUninstall ? ", 윈도우 앱 목록 열기" : "")}");
        Save();
        if (openUninstall)
        {
            try { using (Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true })) { } }
            catch (Exception ex) { Log.Warn($"앱 설정 열기 실패: {ex.GetType().Name}"); }
        }
    }
}
