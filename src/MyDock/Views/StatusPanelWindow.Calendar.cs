using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MyDock.Services;

namespace MyDock.Views;

/// <summary>
/// 시계를 눌렀을 때 뜨는 달력 카드 (맥 알림 센터 달력 위젯 + 한국 공휴일).
/// 오늘 날짜 크게 → 월 달력(‹ › 로 달 이동, 제목 클릭 = 이번 달) → 이번 달 공휴일 한 줄 → 링크.
/// 6주 고정 높이라 달을 넘겨도 카드 크기가 튀지 않는다. 모든 동작은 클릭만으로.
/// </summary>
internal sealed partial class StatusPanelWindow
{
    private const double CalCellHeight = 32;
    private const double CalTodaySize = 28;
    private static readonly string[] DayNames = { "일", "월", "화", "수", "목", "금", "토" };

    private UIElement BuildCalendar()
    {
        var root = new StackPanel();
        DateTime shown = new(DateTime.Today.Year, DateTime.Today.Month, 1); // 보고 있는 달
        DateTime drawnToday = DateTime.MinValue;
        DateTime drawnMonth = DateTime.MinValue;

        // ── 오늘: "10월 7일 수요일" + 작은 연도(·공휴일 이름)
        var bigDate = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold };
        root.Children.Add(bigDate);
        var yearLine = new TextBlock { FontSize = 13, Margin = new Thickness(0, 1, 0, 0) };
        var yearRun = new System.Windows.Documents.Run { Foreground = _p.SubText };
        var todayHoliday = new System.Windows.Documents.Run { Foreground = _p.HolidayText };
        yearLine.Inlines.Add(yearRun);
        yearLine.Inlines.Add(todayHoliday);
        root.Children.Add(yearLine);
        root.Children.Add(Divider());

        // ── 월 머리: [2026년 10월]          [‹][›]
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        var next = CalendarNavButton("", "다음 달"); // ChevronRight
        var prev = CalendarNavButton("", "이전 달"); // ChevronLeft
        DockPanel.SetDock(next, Dock.Right);
        DockPanel.SetDock(prev, Dock.Right);
        var monthText = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
        var monthButton = new Button
        {
            Style = (Style)FindStyle("CardButton"),
            Background = Brushes.Transparent,
            Foreground = _p.Text,
            Content = monthText,
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(-6, 0, 0, 0),
            ToolTip = "이번 달로",
        };
        header.Children.Add(next);
        header.Children.Add(prev);
        header.Children.Add(monthButton);
        root.Children.Add(header);

        // ── 요일 머리 (일~토)
        var weekHead = new UniformGrid7();
        for (int i = 0; i < 7; i++)
        {
            weekHead.Add(new TextBlock
            {
                Text = DayNames[i],
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = i == 0 ? _p.HolidayText : i == 6 ? _p.SaturdayText : _p.SubText,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 4),
            });
        }
        root.Children.Add(weekHead.Grid);

        // ── 6주 × 7일 고정 칸 (셀은 한 번 만들고 내용만 바꿈)
        var days = new Grid();
        for (int c = 0; c < 7; c++) days.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 6; r++) days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CalCellHeight) });
        var cells = new (Grid Cell, Ellipse Today, TextBlock Text)[42];
        for (int i = 0; i < 42; i++)
        {
            var cell = new Grid { Background = Brushes.Transparent };
            var dot = new Ellipse { Width = CalTodaySize, Height = CalTodaySize, Fill = _p.Accent, Visibility = Visibility.Collapsed };
            var text = new TextBlock
            {
                FontSize = 13.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cell.Children.Add(dot);
            cell.Children.Add(text);
            Grid.SetRow(cell, i / 7);
            Grid.SetColumn(cell, i % 7);
            days.Children.Add(cell);
            cells[i] = (cell, dot, text);
        }
        root.Children.Add(days);

        // ── 이번 달 공휴일 한 줄 (최대 2줄 고정 높이)
        var holidayLine = new TextBlock
        {
            FontSize = 12,
            Foreground = _p.SubText,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 17,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Height = 34,
            Margin = new Thickness(2, 6, 2, 0),
        };
        root.Children.Add(holidayLine);

        root.Children.Add(Divider());
        root.Children.Add(LinkRow("알림 센터 열기", () => _services.Shell.OpenNotificationCenter()));
        root.Children.Add(LinkRow("날짜 및 시간 설정…", () =>
            Process.Start(new ProcessStartInfo("ms-settings:dateandtime") { UseShellExecute = true })?.Dispose()));

        void Draw()
        {
            var today = DateTime.Today;
            if (today == drawnToday && shown == drawnMonth) return;
            // 자정이 지나면 보고 있던 달이 "어제의 이번 달"이었을 때만 따라 넘어감
            if (drawnToday != DateTime.MinValue && today != drawnToday
                && shown == new DateTime(drawnToday.Year, drawnToday.Month, 1))
                shown = new DateTime(today.Year, today.Month, 1);
            drawnToday = today;
            drawnMonth = shown;

            bigDate.Text = $"{today.Month}월 {today.Day}일 {DayNames[(int)today.DayOfWeek]}요일";
            yearRun.Text = $"{today.Year}년";
            string? todayName = KoreanHolidays.NameOf(today);
            todayHoliday.Text = todayName != null ? $" · {todayName}" : "";

            monthText.Text = $"{shown.Year}년 {shown.Month}월";
            bool isCurrent = shown.Year == today.Year && shown.Month == today.Month;
            monthText.Foreground = isCurrent ? _p.Text : _p.Accent; // 다른 달을 보고 있으면 "누르면 돌아감" 힌트

            var first = shown.AddDays(-(int)shown.DayOfWeek); // 첫 칸 = 그 주 일요일
            for (int i = 0; i < 42; i++)
            {
                var d = first.AddDays(i);
                var (cell, dot, text) = cells[i];
                bool inMonth = d.Month == shown.Month;
                bool isToday = d == today;
                string? holiday = KoreanHolidays.NameOf(d);
                text.Text = d.Day.ToString(System.Globalization.CultureInfo.InvariantCulture);
                text.FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal;
                text.Foreground = isToday ? _p.AccentText
                    : holiday != null || d.DayOfWeek == DayOfWeek.Sunday ? _p.HolidayText
                    : d.DayOfWeek == DayOfWeek.Saturday ? _p.SaturdayText
                    : _p.Text;
                text.Opacity = inMonth || isToday ? 1 : 0.35;
                dot.Visibility = isToday ? Visibility.Visible : Visibility.Collapsed;
                cell.ToolTip = holiday;
            }

            var list = KoreanHolidays.ForMonth(shown.Year, shown.Month);
            holidayLine.Text = list.Count == 0 ? "이번 달 공휴일 없음" : FormatHolidays(list);
        }

        void Move(int months)
        {
            shown = shown.AddMonths(months);
            Draw();
        }
        prev.Click += (_, _) => Move(-1);
        next.Click += (_, _) => Move(1);
        monthButton.Click += (_, _) =>
        {
            shown = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            Draw();
        };

        _refreshers.Add(Draw);
        return root;
    }

    /// <summary>‹ › 같은 작은 원형 호버 버튼.</summary>
    private Button CalendarNavButton(string glyph, string tip) => new()
    {
        Style = (Style)FindStyle("CardButton"),
        Background = Brushes.Transparent,
        Foreground = _p.Text,
        Width = 30,
        Height = 28,
        Padding = new Thickness(0),
        ToolTip = tip,
        Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 12 },
    };

    /// <summary>"10/3 개천절 · 10/5 대체공휴일 · 10/9 한글날" — 같은 이름이 이어지는 날은 "9/24~26 추석" 으로 묶음.</summary>
    private static string FormatHolidays(IReadOnlyList<KeyValuePair<DateTime, string>> list)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < list.Count)
        {
            var start = list[i];
            int j = i;
            while (j + 1 < list.Count && list[j + 1].Value == start.Value && list[j + 1].Key == list[j].Key.AddDays(1)) j++;
            string date = j > i ? $"{start.Key.Month}/{start.Key.Day}~{list[j].Key.Day}" : $"{start.Key.Month}/{start.Key.Day}";
            parts.Add($"{date} {start.Value}");
            i = j + 1;
        }
        return string.Join(" · ", parts);
    }

    /// <summary>7칸 균등 그리드에 순서대로 넣는 작은 도우미.</summary>
    private sealed class UniformGrid7
    {
        public Grid Grid { get; } = new();
        private int _n;

        public UniformGrid7()
        {
            for (int c = 0; c < 7; c++) Grid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        public void Add(UIElement e)
        {
            Grid.SetColumn(e, _n++);
            Grid.Children.Add(e);
        }
    }
}
