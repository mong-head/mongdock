using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 루틴 카드 창 공통 뼈대 (#24-A, 지금 화면 저장 카드·편집 창): 둥근 카드 + 그림자, 주 화면 가운데, 포커스를 받음(이름 칸 바로 입력).
/// 바깥을 눌러도 닫히지 않음(고치던 내용이 사라지지 않게) — [취소]·Esc 로만. 같은 종류 창은 하나만.
/// </summary>
internal abstract class RoutineCardWindow : Window
{
    protected readonly AppServices Services;
    protected readonly UiPalette P;
    protected readonly StackPanel Body = new();

    protected RoutineCardWindow(AppServices services, double width, string windowTitle)
    {
        Services = services;
        P = UiTheme.Palette(services.Settings.Current);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = windowTitle;
        AppIcon.Apply(this);
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 13;
        Foreground = P.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        Body.Width = width;
        // 오른쪽 여백 20 중 12 를 스크롤 막대 자리로 (막대가 내용 위에 겹쳐 그려지는 스타일 — 스위치·[아이콘] 버튼 끝을 가리지 않게, QA)
        Body.Margin = new Thickness(0, 0, 12, 0);
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = P.CardBackground,
            BorderBrush = P.CardBorder,
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(20, 16, 8, 16),
            Child = _scroll = new ScrollViewer
            {
                Content = Body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Style = (Style)Application.Current.FindResource("OverlayScrollViewer"),
                Focusable = false,
                MaxHeight = Usable().Height - 80, // 카드 여백·그림자 자리
            },
        };
        var root = new Grid { Margin = new Thickness(24, 16, 24, 32) };
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = P.CardBackground,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = P.ShadowOpacity + 0.08 },
        });
        root.Children.Add(card);
        Content = root;

        Loaded += (_, _) => Center();
        SizeChanged += (_, e) => { if (e.HeightChanged && IsLoaded) KeepOnScreen(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !(Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }))
            {
                e.Handled = true;
                Close();
            }
        };
    }

    /// <summary>화면 높이의 이 비율까지 (넘치면 목록이 스크롤).</summary>
    protected double MaxListHeight => SystemParameters.WorkArea.Height * 0.5;

    private readonly ScrollViewer _scroll;

    /// <summary>
    /// 카드를 둘 수 있는 곳: 주 모니터 작업 영역에서 화면에 보이는 독을 뺀 부분 (독이 작업 영역을 예약하지 않는 겹침 모드라
    /// 작업 영역만 보면 길어진 카드가 아래 독과 겹침 — QA 1080p·아래 독·크기 52).
    /// </summary>
    private static Rect Usable()
    {
        var work = SystemParameters.WorkArea;
        var dock = DockState.VisiblePanel;
        if (!dock.IsEmpty && dock.Width > 0 && dock.Height > 0)
        {
            const double gap = 8;
            if (dock.Top > work.Top + work.Height / 2 && dock.Top < work.Bottom) work = new Rect(work.Left, work.Top, work.Width, Math.Max(200, dock.Top - gap - work.Top)); // 아래 독
            else if (dock.Bottom < work.Top + work.Height / 2 && dock.Bottom > work.Top) work = new Rect(work.Left, dock.Bottom + gap, work.Width, Math.Max(200, work.Bottom - dock.Bottom - gap)); // 위 독
        }
        return work;
    }

    private void Center()
    {
        var area = Usable();
        Left = Math.Round(area.Left + (area.Width - ActualWidth) / 2);
        Top = Math.Round(area.Top + Math.Max(8, (area.Height - ActualHeight) * 0.4));
    }

    /// <summary>세부·더 보기를 펼쳐 길어졌을 때 아래가 독·화면 밖으로 나가지 않게 (위로만 당김 — 순간 이동). 그래도 넘치면 카드 안 스크롤.</summary>
    private void KeepOnScreen()
    {
        var area = Usable();
        if (Top + ActualHeight > area.Bottom) Top = Math.Max(area.Top, area.Bottom - ActualHeight);
    }

    // ───────────────────────── 작은 부품 ─────────────────────────

    protected TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };

    protected TextBlock Muted(string text, double size = 12) => new() { Text = text, FontSize = size, Foreground = P.SubText, TextWrapping = TextWrapping.Wrap };

    protected TextBox Field(string text, double width = double.NaN) => MongField.Apply(new TextBox
    {
        Text = text,
        Width = width,
        Height = 28,
        Padding = new Thickness(6, 0, 6, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = P.Tile,
        Foreground = P.Text,
        BorderBrush = P.Divider,
        CaretBrush = P.Text,
    }, P);

    /// <summary>빈 칸에 흐린 안내 글자 (입력하면 사라짐) — 칸을 감싼 Grid 를 돌려줌.</summary>
    protected Grid Hinted(TextBox box, string hint)
    {
        var g = new Grid();
        var text = new TextBlock { Text = hint, Foreground = P.SubText, Opacity = 0.8, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        void Paint() => text.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Paint();
        Paint();
        g.Children.Add(box);
        g.Children.Add(text);
        return g;
    }

    /// <summary>왼쪽 이름 칸(고정 폭) + 내용 한 줄.</summary>
    protected Grid LabeledRow(string label, UIElement content, double labelWidth = 76, double bottom = 10)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, bottom) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 8, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(content, 1);
        g.Children.Add(content);
        return g;
    }

    /// <summary>알약 고르기 (하나만 켜짐). 누르면 pick(인덱스).</summary>
    protected WrapPanel Segments(IReadOnlyList<string> labels, int selected, Action<int> pick)
    {
        var wrap = new WrapPanel();
        void Paint()
        {
            for (int i = 0; i < wrap.Children.Count; i++)
            {
                var b = (Border)wrap.Children[i];
                bool on = i == selected;
                b.Background = on ? P.SoftAccent : P.Tile;
                b.BorderBrush = on ? P.SoftAccentLine : Brushes.Transparent;
                ((TextBlock)b.Child).Foreground = on ? P.SoftAccentText : P.Text;
            }
        }
        for (int i = 0; i < labels.Count; i++)
        {
            int at = i;
            var b = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 2, 9, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Focusable = true,
                Child = new TextBlock { Text = labels[i], FontSize = 12 },
            };
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; selected = at; Paint(); pick(at); };
            b.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { e.Handled = true; selected = at; Paint(); pick(at); } };
            wrap.Children.Add(b);
        }
        Paint();
        return wrap;
    }

    /// <summary>작은 고르기 칸 (글자 + ⌄): 누르면 몽독 메뉴로 목록 — 기본 콤보 상자는 어두운 테마에서 흰색이라 쓰지 않음.</summary>
    protected Border MenuPicker(IReadOnlyList<string> labels, int selected, Action<int> pick, double width = 72)
    {
        var text = new TextBlock { Text = labels[Math.Clamp(selected, 0, labels.Count - 1)], FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { LastChildFill = true };
        var chevron = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) };
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        row.Children.Add(text);
        var box = new Border { Width = width, Height = 26, CornerRadius = new CornerRadius(7), Background = P.Tile, Padding = new Thickness(10, 0, 8, 0), Cursor = Cursors.Hand, Focusable = true, Child = row, VerticalAlignment = VerticalAlignment.Top };
        void Open()
        {
            if (!box.IsEnabled) return;
            var menu = new ContextMenu { PlacementTarget = box, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            for (int i = 0; i < labels.Count; i++)
            {
                int at = i;
                menu.Items.Add(DockMenus.Item(labels[i], () => { selected = at; text.Text = labels[at]; pick(at); }, isChecked: i == selected));
            }
            menu.IsOpen = true;
        }
        box.MouseLeftButtonUp += (_, e) => { e.Handled = true; Open(); };
        box.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space or Key.Down) { e.Handled = true; Open(); } };
        box.IsEnabledChanged += (_, _) => box.Opacity = box.IsEnabled ? 1 : 0.45;
        return box;
    }

    protected Button CardButton(string text, bool primary = false, bool danger = false) => new()
    {
        Style = (Style)Application.Current.FindResource("CardButton"),
        Content = new TextBlock { Text = text, FontSize = 13, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal },
        Background = primary ? P.SoftAccent : P.Tile,
        Foreground = primary ? P.SoftAccentText : danger ? new SolidColorBrush(Color.FromRgb(0xD6, 0x3B, 0x30)) : P.Text,
        Height = 30,
        Padding = new Thickness(16, 0, 16, 0),
        MinWidth = 72,
    };

    /// <summary>"모니터 2 · 왼쪽 반" 같은 작은 알약.</summary>
    protected Border Pill(string text) => new()
    {
        CornerRadius = new CornerRadius(9),
        Background = P.Tile,
        Padding = new Thickness(8, 1, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = P.SubText },
    };

    protected ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        MaxHeight = MaxListHeight,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Style = (Style)Application.Current.FindResource("OverlayScrollViewer"),
        Focusable = false,
    };
}
