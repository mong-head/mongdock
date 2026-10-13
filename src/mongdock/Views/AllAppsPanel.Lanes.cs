using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mongdock.Views;

/// <summary>
/// 판 안 순서 바꾸기 공통 "미리 비켜 주기" (2026-10-14 사용자: 폴더 줄은 놓아야 결과가 보여 어색함 — 맥 런치패드처럼 끄는 동안 빈자리를 미리 열어 줘야 함).
/// 즐겨찾기·폴더 줄·펼친 폴더 안·루틴 줄 모두 같은 방식:
/// - 끌기 시작 때 줄(lane)의 칸들의 원래 자리를 판 본문 좌표로 적어 둠 (여러 줄·펼친 폴더 사이·스크롤과 상관없이).
/// - 커서가 있는 칸 자리(없으면 가장 가까운 자리)가 들어갈 자리. 끄는 칸을 뺀 나머지가 새 순서의 자리로 150ms 미끄러짐.
/// - 원래 자리는 비워 둠(흐린 자리 표시 없음). 놓으면 끌기 그림이 열린 빈자리로 들어가고 저장.
/// - 폴더를 끌 때는 다른 폴더 칸을 강조하지 않고 비켜 주기만 (폴더 안에 폴더는 넣지 않음).
/// </summary>
internal sealed partial class AllAppsPanel
{
    private const int LaneShiftMs = 150;

    private sealed class Lane
    {
        public required string Kind;                    // fav | inside | folders | routines
        public required List<FrameworkElement> Cells;
        public required List<Rect> Slots;               // 본문 좌표. 밖에서 들어올 수 있는 줄은 끝에 자리 하나 더
        public int Src = -1;
        public int Gap = -1;
        public Rect Bounds = Rect.Empty;
    }

    private readonly Dictionary<string, Lane> _lanes = new();

    /// <summary>순서 바꾸기 줄을 잡는 기준 (판 본문 내용 — 스크롤돼도 칸끼리의 자리는 그대로).</summary>
    private FrameworkElement? LaneRoot => _body.Content as FrameworkElement;

    /// <summary>끌기 시작: 줄마다 칸과 원래 자리를 적어 둠.</summary>
    private void BuildLanes(Border source)
    {
        _lanes.Clear();
        if (LaneRoot is not { } root) return;
        Rect SlotOf(FrameworkElement c) => new(c.TranslatePoint(new Point(0, 0), root), new Size(c.ActualWidth, c.ActualHeight));
        void Add(string kind, IEnumerable<FrameworkElement> cells, Func<List<Rect>, Rect?>? extra)
        {
            var list = cells.Where(c => c.IsVisible && c.ActualWidth > 0).ToList();
            foreach (var c in list) if (c.RenderTransform is TranslateTransform t) { t.BeginAnimation(TranslateTransform.XProperty, null); t.BeginAnimation(TranslateTransform.YProperty, null); t.X = t.Y = 0; }
            var slots = list.Select(SlotOf).ToList();
            var lane = new Lane { Kind = kind, Cells = list, Slots = slots, Src = list.IndexOf(source) };
            if (lane.Src < 0 && extra?.Invoke(slots) is { } more) slots.Add(more); // 밖에서 들어오면 끝 자리 하나 더
            foreach (var s in slots) lane.Bounds.Union(s);
            if (slots.Count > 0) _lanes[kind] = lane;
        }
        // 즐겨찾기: 끝 자리 = 마지막 칸 다음 (없으면 줄 처음)
        Add("fav", _favCells, slots => slots.Count > 0
            ? new Rect(slots[^1].X + AppCell, slots[^1].Y, slots[^1].Width, slots[^1].Height)
            : _favRow is { } row ? new Rect(row.TranslatePoint(new Point(0, 0), root), new Size(AppCell - 4, AppCell)) : null);
        // 펼친 폴더 안: 끝 자리 = 칸 격자의 다음 칸
        if (_groupGrid is { } grid)
            Add("inside", _groupCells, slots =>
            {
                int perRow = Math.Max(1, (int)(grid.Width / AppCell));
                double rowH = _groupCells.Count > 0 ? _groupCells[0].ActualHeight + 4 : AppCell;
                int n = slots.Count;
                var o = grid.TranslatePoint(new Point(n % perRow * AppCell + 2, n / perRow * rowH), root);
                return new Rect(o, slots.Count > 0 ? slots[0].Size : new Size(AppCell - 4, AppCell));
            });
        // 폴더 줄 ("기타"는 늘 끝이라 빼고, [새 폴더] 도 빼고) · 루틴 줄
        var all = Descendants(root).OfType<FrameworkElement>().ToList();
        Add("folders", all.Where(e => e.Tag is DropTag { Kind: "group" } t && t.Key != Mongdock.Services.AllAppsCatalog.Other), null);
        Add("routines", all.Where(e => e.Tag is DropTag { Kind: "routine" }), null);
    }

    /// <summary>판 좌표 p 가 그 줄 안(조금 넉넉히)인지.</summary>
    private bool InLane(string kind, Point p, double slack = 16)
    {
        if (!_lanes.TryGetValue(kind, out var lane) || LaneRoot is not { } root) return false;
        var r = lane.Bounds;
        r.Inflate(slack, slack);
        return r.Contains(TranslatePoint(p, root));
    }

    /// <summary>
    /// 그 줄에서 커서 자리에 빈자리를 열고 나머지를 비켜 줌 (다른 줄은 원래대로). kind = null 이면 모든 줄 원래대로.
    /// </summary>
    private void ShiftLanes(string? kind, Point? p)
    {
        foreach (var lane in _lanes.Values)
        {
            int gap = -1;
            if (lane.Kind == kind && p is { } pt && LaneRoot is { } root) gap = GapAt(lane, TranslatePoint(pt, root));
            if (gap == lane.Gap) continue;
            lane.Gap = gap;
            for (int i = 0; i < lane.Cells.Count; i++)
            {
                if (i == lane.Src) continue; // 끄는 칸 = 빈 원래 자리 (안 보임)
                int eff = lane.Src >= 0 && i > lane.Src ? i - 1 : i;
                int to = gap < 0 ? i : eff >= gap ? eff + 1 : eff;
                var delta = lane.Slots[Math.Min(to, lane.Slots.Count - 1)].TopLeft - lane.Slots[i].TopLeft;
                var cell = lane.Cells[i];
                if (cell.RenderTransform is not TranslateTransform tt) cell.RenderTransform = tt = new TranslateTransform();
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(delta.X, TimeSpan.FromMilliseconds(LaneShiftMs)) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(delta.Y, TimeSpan.FromMilliseconds(LaneShiftMs)) { EasingFunction = ease });
            }
        }
    }

    /// <summary>들어갈 자리: 커서가 들어 있는 칸 자리, 없으면 가운데가 가장 가까운 자리 (끄는 칸을 뺀 순서 기준 0..나머지 수).</summary>
    private static int GapAt(Lane lane, Point local)
    {
        int count = lane.Cells.Count - (lane.Src >= 0 ? 1 : 0); // 나머지 칸 수 = 들어갈 수 있는 자리 0..count
        int best = 0;
        double bestD = double.MaxValue;
        for (int g = 0; g <= count && g < lane.Slots.Count; g++)
        {
            var s = lane.Slots[g];
            if (s.Contains(local)) return g;
            var c = new Point(s.X + s.Width / 2, s.Y + s.Height / 2);
            double dd = (c - local).LengthSquared;
            if (dd < bestD) { bestD = dd; best = g; }
        }
        return best;
    }

    /// <summary>열린 빈자리의 판 좌표 (놓을 때 끌기 그림이 들어갈 곳).</summary>
    private Point? LaneGapOrigin(string kind)
    {
        if (!_lanes.TryGetValue(kind, out var lane) || lane.Gap < 0 || LaneRoot is not { } root) return null;
        return root.TranslatePoint(lane.Slots[Math.Min(lane.Gap, lane.Slots.Count - 1)].TopLeft, this);
    }

    private int LaneGap(string kind) => _lanes.TryGetValue(kind, out var lane) ? lane.Gap : -1;

    /// <summary>나머지 칸(끄는 칸 뺌)의 DropTag 키 순서.</summary>
    private List<string> LaneOthers(string kind) => _lanes.TryGetValue(kind, out var lane)
        ? lane.Cells.Where((c, i) => i != lane.Src).Select(c => c.Tag is DropTag t ? t.Key : "").ToList()
        : new List<string>();
}
