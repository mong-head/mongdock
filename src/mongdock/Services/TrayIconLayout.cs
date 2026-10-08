using System.IO;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>트레이 아이콘이 바/⌃ 중 어디로 갔는지의 근거.</summary>
public enum TrayPlacementSource
{
    /// <summary>몽독에서 직접 옮김 (TopBar.TrayIconPlacement).</summary>
    Mongdock,
    /// <summary>윈도우 설정 "작업 표시줄에 항상 표시" (NotifyIconSettings\IsPromoted).</summary>
    Windows,
    /// <summary>둘 다 없음 → ⌃ 안.</summary>
    Default,
}

/// <summary>배치 계산 결과 한 줄 (설정 창 "트레이 아이콘 정리" 목록에도 씀).</summary>
public sealed record TrayIconSlot(TrayIconInfo Info, string PlacementKey, bool OnBar, TrayPlacementSource Source, bool WindowsPromoted);

/// <summary>
/// 트레이 아이콘을 "상단바에 보임 / ⌃ 안" 으로 나누는 규칙 (윈도우 11 처럼).
/// 1) 몽독 저장값(TopBar.TrayIconPlacement) &gt; 2) 윈도우 IsPromoted &gt; 3) 기본 ⌃.
/// 순서: 몽독 Order 있는 것 먼저(Order 순) → 윈도우 설정 순서(UIOrderList) → 등록 순서.
/// 바 개수가 TrayIconsVisibleCount 를 넘으면 순서 뒤쪽부터 ⌃ 로.
/// 숨김(NIS_HIDDEN)·그림 없는 아이콘은 어디에도 넣지 않음.
/// </summary>
public static class TrayIconLayout
{
    /// <summary>바 최대 개수 범위.</summary>
    public const int MaxBarLimit = 50;

    /// <summary>아이콘별 마지막으로 로그에 남긴 판정 (같은 판정은 다시 안 남김).</summary>
    private static readonly Dictionary<string, string> Logged = new();

    /// <summary>저장 키: GUID 있으면 GUID("d" 소문자), 없으면 "exe 파일 이름 소문자:uID" (재시작해도 같음).</summary>
    public static string PlacementKey(TrayIconInfo icon)
    {
        if (icon.Guid != Guid.Empty) return icon.Guid.ToString("D").ToLowerInvariant();
        string file = icon.ProcessPath.Length > 0 ? Path.GetFileName(icon.ProcessPath) : icon.ProcessName;
        return $"{file.ToLowerInvariant()}:{icon.Uid}";
    }

    /// <summary>전체 배치 (바 → ⌃ 순, 각자 표시 순서대로).</summary>
    public static IReadOnlyList<TrayIconSlot> Compute(IReadOnlyList<TrayIconInfo> icons, TopBarSettings settings)
    {
        var reader = NotifyIconSettingsReader.Shared;
        var saved = settings.TrayIconPlacement ?? new Dictionary<string, TrayIconPlacement>();
        var rows = new List<(TrayIconSlot Slot, int Group, int SavedOrder, int WinOrder, int Seq)>();
        int seq = 0;
        foreach (var icon in icons)
        {
            seq++;
            if (icon.IsHidden || icon.Icon is null) continue;
            string key = PlacementKey(icon);
            var win = reader.Match(icon);
            bool promoted = win?.IsPromoted == true;
            bool onBar;
            TrayPlacementSource src;
            if (saved.TryGetValue(key, out var p) && p is not null)
            {
                onBar = p.OnBar;
                src = TrayPlacementSource.Mongdock;
            }
            else if (promoted)
            {
                onBar = true;
                src = TrayPlacementSource.Windows;
            }
            else
            {
                onBar = false;
                src = TrayPlacementSource.Default;
            }
            string verdict = $"{(onBar ? "바" : "⌃")} ({src}{(win is null ? ", 윈도우 설정 항목 없음" : $", 윈도우 ID {win.Id}")})";
            lock (Logged)
            {
                if (!Logged.TryGetValue(key, out var last) || last != verdict)
                {
                    Logged[key] = verdict;
                    Log.Info($"트레이 아이콘 배치: {key} → {verdict}");
                }
            }
            rows.Add((new TrayIconSlot(icon, key, onBar, src, promoted),
                p is not null ? 0 : 1, p?.Order ?? 0, win?.Order ?? int.MaxValue, seq));
        }
        var ordered = rows
            .OrderBy(r => r.Group).ThenBy(r => r.SavedOrder).ThenBy(r => r.WinOrder).ThenBy(r => r.Seq)
            .Select(r => r.Slot)
            .ToList();

        int max = Math.Clamp(settings.TrayIconsVisibleCount, 0, MaxBarLimit);
        var bar = new List<TrayIconSlot>();
        var more = new List<TrayIconSlot>();
        foreach (var s in ordered)
        {
            if (s.OnBar && bar.Count < max) bar.Add(s);
            else more.Add(s.OnBar ? s with { OnBar = false } : s);
        }
        bar.AddRange(more);
        return bar;
    }

    /// <summary>상단바에 바로 보일 아이콘 / ⌃ 패널로 갈 아이콘.</summary>
    public static (List<TrayIconInfo> OnBar, List<TrayIconInfo> Overflow) Split(IReadOnlyList<TrayIconInfo> icons, TopBarSettings settings)
    {
        var slots = Compute(icons, settings);
        return (slots.Where(s => s.OnBar).Select(s => s.Info).ToList(),
                slots.Where(s => !s.OnBar).Select(s => s.Info).ToList());
    }

    /// <summary>
    /// 몽독에서 옮김: <paramref name="key"/> 아이콘을 바의 <paramref name="barIndex"/> 자리(또는 ⌃ 안이면 onBar=false)로.
    /// 바로 옮기거나 바 안 순서를 바꾸면 현재 바의 아이콘 모두를 그 순서로 저장(재시작해도 같은 자리).
    /// 설정 객체만 바꾸며 저장(Save)은 호출한 쪽이.
    /// </summary>
    public static void Move(IReadOnlyList<TrayIconInfo> icons, TopBarSettings settings, string key, bool onBar, int barIndex)
    {
        settings.TrayIconPlacement ??= new Dictionary<string, TrayIconPlacement>();
        var slots = Compute(icons, settings);
        var bar = slots.Where(s => s.OnBar).Select(s => s.PlacementKey).ToList();
        var more = slots.Where(s => !s.OnBar).Select(s => s.PlacementKey).ToList();
        bar.Remove(key);
        more.Remove(key);
        if (onBar)
        {
            bar.Insert(Math.Clamp(barIndex, 0, bar.Count), key);
            // 최대 개수를 넘으면 맨 뒤 것이 ⌃ 로 (방금 놓은 아이콘은 지킴)
            int max = Math.Clamp(settings.TrayIconsVisibleCount, 1, MaxBarLimit);
            while (bar.Count > max)
            {
                int victim = bar[^1] == key ? bar.Count - 2 : bar.Count - 1;
                more.Insert(0, bar[victim]);
                bar.RemoveAt(victim);
            }
        }
        else
        {
            more.Insert(0, key);
        }
        for (int i = 0; i < bar.Count; i++)
            settings.TrayIconPlacement[bar[i]] = new TrayIconPlacement { OnBar = true, Order = i };
        // ⌃ 쪽: 직접 옮긴 것·이미 저장된 것만 기록 (나머지는 윈도우 설정을 계속 따름)
        for (int i = 0; i < more.Count; i++)
        {
            string k = more[i];
            if (k == key || settings.TrayIconPlacement.ContainsKey(k))
                settings.TrayIconPlacement[k] = new TrayIconPlacement { OnBar = false, Order = 1000 + i };
        }
    }

    /// <summary>바 안에서 한 칸 위/아래(왼쪽/오른쪽)로. delta = -1 / +1. 바가 아니면 ⌃ 안에서 순서만.</summary>
    public static void Nudge(IReadOnlyList<TrayIconInfo> icons, TopBarSettings settings, string key, int delta)
    {
        var slots = Compute(icons, settings);
        var me = slots.FirstOrDefault(s => s.PlacementKey == key);
        if (me is null) return;
        var group = slots.Where(s => s.OnBar == me.OnBar).Select(s => s.PlacementKey).ToList();
        int i = group.IndexOf(key), j = i + delta;
        if (i < 0 || j < 0 || j >= group.Count) return;
        (group[i], group[j]) = (group[j], group[i]);
        settings.TrayIconPlacement ??= new Dictionary<string, TrayIconPlacement>();
        for (int n = 0; n < group.Count; n++)
            settings.TrayIconPlacement[group[n]] = new TrayIconPlacement { OnBar = me.OnBar, Order = (me.OnBar ? 0 : 1000) + n };
    }
}
