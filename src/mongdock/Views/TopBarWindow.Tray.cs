using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 상단바 오른쪽 트레이 아이콘 영역 (블루투스 왼쪽). 모든 모니터의 상단바가 같은 서비스 목록을 보여 준다.
/// 바/⌃ 나누기는 TrayIconLayout (몽독 저장값 &gt; 윈도우 "작업 표시줄에 항상 표시" &gt; ⌃).
/// 끌어 옮기기(TrayIconDrag) 동안 놓을 자리 판정·비켜서기 애니메이션도 여기서.
/// </summary>
public partial class TopBarWindow
{
    private readonly Dictionary<string, TrayIconButton> _trayButtons = new();
    private Button? _trayMore;
    private List<string> _trayOrder = new();
    private bool _trayHosted;

    /// <summary>트레이 영역이 있는(열린) 상단바들 — 끌어 놓기 판정용.</summary>
    internal static readonly List<TopBarWindow> TrayHosts = new();

    private void OnTrayIconsChanged(object? sender, EventArgs e) => SyncTrayIcons();
    private void OnTrayRulesChanged(object? sender, EventArgs e) => SyncTrayIcons();

    private void EnsureTrayHosted()
    {
        if (_trayHosted || _closed) return;
        _trayHosted = true;
        TrayHosts.Add(this);
        NotifyIconSettingsReader.Shared.Changed += OnTrayRulesChanged;
        Closed += (_, _) =>
        {
            TrayHosts.Remove(this);
            NotifyIconSettingsReader.Shared.Changed -= OnTrayRulesChanged;
        };
    }

    private void SyncTrayIcons()
    {
        if (!_initialized || _closed) return;
        EnsureTrayHosted();
        if (TrayIconDrag.IsActive || IsReordering)
            return; // 끄는 중엔 자식을 바꾸지 않음 (캡처·비켜서기 유지) → 놓은 뒤 EndTrayDrag / EndReorder 에서 다시
        var s = _services.Settings.Current.TopBar;
        bool show = s.Enabled && s.ShowTrayIcons && !AppState.Paused;
        if (!show)
        {
            if (TrayArea.Children.Count > 0) TrayArea.Children.Clear();
            _trayButtons.Clear();
            _trayOrder.Clear();
            TrayArea.Visibility = Visibility.Collapsed;
            if (_panel?.Kind == StatusPanelKind.Tray) _panel.Close();
            return;
        }

        var (onBar, overflow) = TrayIconButton.Split(_services, TrayFold); // 좁은 화면이면 뒤쪽부터 ⌃ 로 (TopBarWindow.Fit.cs)
        var style = (Style)FindResource("BarButton");
        var keys = new List<string>(onBar.Count + 1);
        foreach (var info in onBar)
        {
            if (_trayButtons.TryGetValue(info.Key, out var b)) b.Apply(info);
            else
            {
                b = new TrayIconButton(_services, info, style, 16, beforeClick: () => _panel?.Close(), onBar: true, background: () => _barTarget)
                {
                    Padding = new Thickness(5, 0, 5, 0),
                    MinWidth = 0,
                };
                _trayButtons[info.Key] = b;
            }
            keys.Add(info.Key);
        }
        foreach (var gone in _trayButtons.Keys.Except(keys).ToList()) _trayButtons.Remove(gone);
        if (overflow.Count > 0) keys.Add("⌃");

        // 순서·구성이 바뀔 때만 자식 다시 배치 (호버 중인 버튼이 빠졌다 들어가며 깜빡이지 않게)
        if (!keys.SequenceEqual(_trayOrder) || TrayArea.Children.Count != keys.Count)
        {
            TrayArea.Children.Clear();
            foreach (var k in keys) TrayArea.Children.Add(k == "⌃" ? TrayMoreButton() : _trayButtons[k]);
            _trayOrder = keys;
            Remeasure(RightSection);
        }
        TrayArea.Visibility = keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (overflow.Count == 0 && _panel?.Kind == StatusPanelKind.Tray) _panel.Close();
    }

    /// <summary>바 배경색이 바뀜(앱 색 맞춤 모드에서 앱 전환 등) → 트레이 아이콘 판 다시 판정. 글자색 변화는 버튼이 스스로.</summary>
    private void RefreshTrayTint()
    {
        foreach (var b in _trayButtons.Values) b.RefreshTint();
    }

    private Button TrayMoreButton()
    {
        if (_trayMore != null) return _trayMore;
        var b = new Button { Style = (Style)FindResource("BarButton"), Padding = new Thickness(5, 0, 5, 0), MinWidth = 0, ToolTip = Loc.T("트레이 아이콘 더 보기") };
        var path = new ShapePath
        {
            Width = 14,
            Height = 16,
            Data = BarIcons.ChevronUp,
            LayoutTransform = (Transform)FindResource("BarIconScale"),
        };
        path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = b });
        b.Content = path;
        b.Click += (_, _) => TogglePanel(StatusPanelKind.Tray, b);
        _trayMore = b;
        return b;
    }

    // ───────────────────────── 끌어 옮기기 ─────────────────────────

    private const double TrayShiftMs = 140;
    private Border? _trayGap;           // ⌃ 에서 꺼내 올 때 벌어지는 빈자리
    private int _trayGapIndex = -1;
    private bool _trayMoreTemp;         // 끄는 동안만 임시로 보인 ⌃
    private object? _trayMoreTagBefore;
    private bool _trayDragBegun;

    /// <summary>끌기 시작: 바에서 끌면 ⌃ 가 없더라도 놓을 곳으로 잠시 보여 줌.</summary>
    internal void BeginTrayDrag(TrayIconButton source, bool fromBar)
    {
        if (_closed || TrayArea.Visibility != Visibility.Visible && !fromBar) return;
        if (fromBar && TrayArea.Visibility == Visibility.Visible && !TrayArea.Children.Contains(TrayMoreButton()))
        {
            TrayArea.Children.Add(TrayMoreButton());
            _trayMoreTemp = true;
        }
        _trayMoreTagBefore = _trayMore?.Tag;
        _trayDragBegun = true;
    }

    /// <summary>화면 점(물리 픽셀)이 이 상단바 트레이 영역/⌃ 위인지. 바 위면 들어갈 자리(끄는 아이콘을 뺀 바 순서 기준).</summary>
    internal TrayDropHit HitTestTrayDrop(Point screenPx, TrayIconButton source)
    {
        if (_closed || !IsVisible || TrayArea.Visibility != Visibility.Visible || PresentationSource.FromVisual(TrayArea) is null)
            return default;
        Point local = TrayArea.PointFromScreen(screenPx);
        double h = TrayArea.ActualHeight;
        // 위아래는 바 높이 전체 + 조금 여유 (상단바가 얇아 정확히 맞추기 어려움)
        if (local.Y < -6 || local.Y > Math.Max(h, ActualHeight) + 10) return default;

        // ⌃ (변형 없는 원래 자리 기준)
        if (_trayMore is not null && TrayArea.Children.Contains(_trayMore))
        {
            double mx = VisualTreeHelper.GetOffset(_trayMore).X;
            if (local.X >= mx && local.X <= mx + _trayMore.ActualWidth) return new TrayDropHit(TrayDropKind.More, 0);
        }

        var icons = BarIcons_(source);
        double left = icons.Count > 0 ? VisualTreeHelper.GetOffset(icons[0]).X : 0;
        double right = TrayArea.ActualWidth;
        if (_trayMore is not null && TrayArea.Children.Contains(_trayMore)) right = VisualTreeHelper.GetOffset(_trayMore).X;
        // 왼쪽은 아이콘 하나 폭만큼 여유 (빈 바에 놓을 때도 잡히게)
        if (local.X < left - (icons.Count == 0 ? 60 : 28) || local.X > right) return default;

        int index = 0;
        foreach (var b in icons)
        {
            double cx = VisualTreeHelper.GetOffset(b).X + b.ActualWidth / 2;
            if (local.X > cx) index++;
        }
        return new TrayDropHit(TrayDropKind.Bar, index);
    }

    /// <summary>바의 트레이 아이콘 버튼들 (끄는 버튼 제외, 현재 배치 순서).</summary>
    private List<TrayIconButton> BarIcons_(TrayIconButton? except) =>
        TrayArea.Children.OfType<TrayIconButton>().Where(b => !ReferenceEquals(b, except)).ToList();

    /// <summary>놓을 자리 미리 보기: 다른 아이콘이 비켜섬 / ⌃ 강조. hit.Kind == None 이면 원래대로.</summary>
    internal void PreviewTrayDrop(TrayIconButton source, TrayDropHit hit, bool fromBar)
    {
        if (_closed || !_trayDragBegun) return;
        if (_trayMore is not null) _trayMore.Tag = hit.Kind == TrayDropKind.More && fromBar ? "Active" : _trayMoreTagBefore;

        bool sourceHere = fromBar && TrayArea.Children.Contains(source);
        if (sourceHere)
        {
            // 바 안에서: 끄는 아이콘 자리를 비우고 새 자리에 빈칸 → 사이 아이콘들이 옆으로 (TranslateX)
            var all = TrayArea.Children.OfType<TrayIconButton>().ToList();
            var others = all.Where(b => !ReferenceEquals(b, source)).ToList();
            double gap = source.ActualWidth;
            double start = all.Count > 0 ? VisualTreeHelper.GetOffset(all[0]).X : 0;
            double x = start;
            for (int i = 0; i < others.Count; i++)
            {
                if (hit.Kind == TrayDropKind.Bar && i == hit.BarIndex) x += gap;
                var b = others[i];
                double orig = VisualTreeHelper.GetOffset(b).X;
                double target = hit.Kind == TrayDropKind.Bar ? x - orig : 0;
                ShiftTo(b, target);
                x += b.ActualWidth;
            }
            return;
        }

        // ⌃ 패널에서 꺼내 오는 중: 실제 빈칸(Border)을 넣어 폭을 벌림 (오른쪽 정렬이라 왼쪽 것들이 비켜섬)
        if (hit.Kind != TrayDropKind.Bar || fromBar)
        {
            RemoveTrayGap();
            return;
        }
        if (_trayGap is not null && _trayGapIndex == hit.BarIndex) return;
        var icons = BarIcons_(null);
        double w = icons.Count > 0 ? icons[0].ActualWidth : 26;
        bool fresh = _trayGap is null;
        if (_trayGap is not null) TrayArea.Children.Remove(_trayGap);
        _trayGap ??= new Border { Height = 1, IsHitTestVisible = false };
        int at = hit.BarIndex < icons.Count ? TrayArea.Children.IndexOf(icons[hit.BarIndex]) : (icons.Count > 0 ? TrayArea.Children.IndexOf(icons[^1]) + 1 : 0);
        TrayArea.Children.Insert(Math.Clamp(at, 0, TrayArea.Children.Count), _trayGap);
        _trayGapIndex = hit.BarIndex;
        if (fresh) _trayGap.BeginAnimation(WidthProperty, Anim.FromTo(0, w, TrayShiftMs, Anim.EaseOut));
        else
        {
            _trayGap.BeginAnimation(WidthProperty, null);
            _trayGap.Width = w;
        }
    }

    private static void ShiftTo(UIElement el, double x)
    {
        var (_, shift) = Anim.Transforms(el);
        if (Math.Abs(shift.X - x) < 0.5 && !shift.HasAnimatedProperties) return;
        shift.BeginAnimation(TranslateTransform.XProperty, Anim.To(x, TrayShiftMs, Anim.EaseOut));
    }

    private void RemoveTrayGap()
    {
        if (_trayGap is null) return;
        TrayArea.Children.Remove(_trayGap);
        _trayGap = null;
        _trayGapIndex = -1;
    }

    /// <summary>끌기 끝: 미리 보기 되돌리고, 끄는 동안 미뤄 둔 갱신 반영.</summary>
    internal void EndTrayDrag()
    {
        if (_closed || !_trayDragBegun) return;
        _trayDragBegun = false;
        RemoveTrayGap();
        foreach (var b in TrayArea.Children.OfType<TrayIconButton>())
        {
            var (_, shift) = Anim.Transforms(b);
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;
        }
        if (_trayMore is not null) _trayMore.Tag = _trayMoreTagBefore;
        if (_trayMoreTemp)
        {
            _trayMoreTemp = false;
            TrayArea.Children.Remove(_trayMore);
        }
        // 다음 SyncTrayIcons 에서 자식을 확실히 다시 맞추게
        _trayOrder = new List<string>();
        Dispatcher.BeginInvoke(SyncTrayIcons, System.Windows.Threading.DispatcherPriority.Background);
    }
}
