using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MyDock.Services;

namespace MyDock.Views;

/// <summary>
/// 시계를 눌렀을 때 뜨는 달력 카드 (맥 알림 센터 달력 위젯 + 한국 공휴일).
/// 오늘 날짜 크게 → 월 달력(‹ › 또는 휠로 달 이동, 제목 클릭 = 이번 달) → 선택한 날 정보 → 이번 달 공휴일 한 줄 → 링크.
/// 날짜 칸: 호버 = 옅은 원, 클릭 = 선택(강조색 테두리 원), 다른 달 날짜 클릭 = 그 달로 이동 + 선택,
/// 더블클릭 또는 "캘린더에서 열기" = 설정한 캘린더(TopBar.CalendarApp, Services/CalendarApps)로 열기.
/// 선택은 패널이 열려 있는 동안만 (패널은 열 때마다 새로 만들어져 다시 열면 오늘).
/// 6주·정보 줄 모두 고정 높이라 달·선택을 바꿔도 카드 크기가 튀지 않는다. 모든 동작은 마우스만으로.
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
        DateTime selected = DateTime.Today; // 선택한 날 (패널 열 때 오늘)
        DateTime drawnSelected = DateTime.MinValue;

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
        var cells = new (Grid Cell, Ellipse Today, Ellipse Ring, TextBlock Text)[42];
        var cellDates = new DateTime[42]; // 칸 i 가 지금 보여 주는 날짜 (클릭 처리용)
        for (int i = 0; i < 42; i++)
        {
            var cell = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            // 호버: 옅은 원 (90ms 페이드)
            var hover = new Ellipse { Width = CalTodaySize, Height = CalTodaySize, Fill = _p.Hover, Opacity = 0, IsHitTestVisible = false };
            var dot = new Ellipse { Width = CalTodaySize, Height = CalTodaySize, Fill = _p.Accent, Visibility = Visibility.Collapsed };
            // 선택: 강조색 테두리 원 (오늘이 선택되면 기존 채움 원만)
            var ring = new Ellipse
            {
                Width = CalTodaySize,
                Height = CalTodaySize,
                Stroke = _p.Accent,
                StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed,
            };
            var text = new TextBlock
            {
                FontSize = 13.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cell.Children.Add(hover);
            cell.Children.Add(dot);
            cell.Children.Add(ring);
            cell.Children.Add(text);
            cell.MouseEnter += (_, _) => Anim.Fade(hover, 1, 90);
            cell.MouseLeave += (_, _) => Anim.Fade(hover, 0, 90);
            Grid.SetRow(cell, i / 7);
            Grid.SetColumn(cell, i % 7);
            days.Children.Add(cell);
            cells[i] = (cell, dot, ring, text);
        }
        // 달이 바뀔 때 격자가 옆에서 미끄러져 들어오므로 카드 여백 밖으로 그려지지 않게 자름
        var daysHost = new Border { ClipToBounds = true, Child = days };
        root.Children.Add(daysHost);

        // ── 선택한 날 (고정 높이). 나중에 그 날 일정 목록(ICS 등)을 이 StackPanel 의 정보 줄 아래에 붙이면 됨
        // 1줄: "10월 9일 금요일 · 한글날"            [캘린더에서 열기]
        // 2줄: "음력 8월 29일 · 2일 후"
        var selectedDay = new StackPanel { Margin = new Thickness(2, 6, 2, 0) };
        var infoTop = new DockPanel { LastChildFill = true, Height = 19 };
        var openLink = new TextBlock
        {
            Text = "캘린더에서 열기",
            FontSize = 12,
            Foreground = _p.Accent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        openLink.MouseEnter += (_, _) => openLink.TextDecorations = TextDecorations.Underline;
        openLink.MouseLeave += (_, _) => openLink.TextDecorations = null;
        openLink.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            OpenInCalendar(selected);
        };
        DockPanel.SetDock(openLink, Dock.Right);
        infoTop.Children.Add(openLink);
        var infoDate = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var infoDateRun = new System.Windows.Documents.Run { FontWeight = FontWeights.SemiBold, Foreground = _p.Text };
        var infoHolidayRun = new System.Windows.Documents.Run { Foreground = _p.HolidayText };
        infoDate.Inlines.Add(infoDateRun);
        infoDate.Inlines.Add(infoHolidayRun);
        infoTop.Children.Add(infoDate);
        selectedDay.Children.Add(infoTop);
        var infoSub = new TextBlock
        {
            FontSize = 12,
            Foreground = _p.SubText,
            Height = 17,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        selectedDay.Children.Add(infoSub);
        root.Children.Add(selectedDay);

        // ── 이번 달 공휴일 (선택한 날 정보가 우선이라 1줄 고정, 넘치면 … + 툴팁으로 전체)
        var holidayLine = new TextBlock
        {
            FontSize = 12,
            Foreground = _p.SubText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Height = 17,
            Margin = new Thickness(2, 4, 2, 0),
        };
        root.Children.Add(holidayLine);

        // 맥 알림 센터처럼 달력 아래에 최근 알림 (앱별 묶음). 항목을 눌러 앱을 열어도 패널은 남음 (바깥 클릭으로 닫힘)
        if (_services.Notifications.IsAvailable)
        {
            root.Children.Add(Divider());
            var notifications = new NotificationListView(_services, _p, maxHeight: 320);
            root.Children.Add(notifications);
        }

        root.Children.Add(Divider());
        root.Children.Add(LinkRow("알림 센터 열기", () => _services.Shell.OpenNotificationCenter()));
        root.Children.Add(LinkRow("날짜 및 시간 설정…", () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:dateandtime") { UseShellExecute = true })?.Dispose()));

        void Draw()
        {
            var today = DateTime.Today;
            if (today == drawnToday && shown == drawnMonth && selected == drawnSelected) return;
            if (drawnToday != DateTime.MinValue && today != drawnToday)
            {
                // 자정이 지나면 보고 있던 달이 "어제의 이번 달"이었을 때만 따라 넘어감
                if (shown == new DateTime(drawnToday.Year, drawnToday.Month, 1))
                    shown = new DateTime(today.Year, today.Month, 1);
                // 어제(그때의 오늘)를 선택해 두었으면 선택도 새 오늘로
                if (selected == drawnToday) selected = today;
            }
            drawnToday = today;
            drawnMonth = shown;
            drawnSelected = selected;

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
                cellDates[i] = d;
                var (cell, dot, ring, text) = cells[i];
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
                ring.Visibility = d == selected && !isToday ? Visibility.Visible : Visibility.Collapsed;
                cell.ToolTip = holiday;
            }

            // 선택한 날: "10월 9일 금요일 · 한글날" / "음력 8월 29일 · 2일 후" (다른 해면 연도도)
            string? selHoliday = KoreanHolidays.NameOf(selected);
            infoDateRun.Text = $"{selected.Month}월 {selected.Day}일 {DayNames[(int)selected.DayOfWeek]}요일"
                + (selected.Year != today.Year ? $" ({selected.Year}년)" : "");
            infoHolidayRun.Text = selHoliday != null ? $" · {selHoliday}" : "";
            var parts = new List<string>(2);
            if (KoreanHolidays.ToLunar(selected) is (int lm, int ld, bool leap))
                parts.Add($"음력 {(leap ? "윤" : "")}{lm}월 {ld}일");
            parts.Add(RelativeDay(selected, today));
            infoSub.Text = string.Join(" · ", parts);

            var list = KoreanHolidays.ForMonth(shown.Year, shown.Month);
            holidayLine.Text = list.Count == 0 ? "이번 달 공휴일 없음" : FormatHolidays(list);
            holidayLine.ToolTip = list.Count == 0 ? null : holidayLine.Text;
        }

        // 달 이동 + 격자 슬라이드/페이드 (120ms, 다음 달 = 오른쪽에서, 이전 달 = 왼쪽에서). 시스템 애니메이션 꺼짐이면 바로.
        void ShowMonth(DateTime month, DateTime? select = null)
        {
            month = new DateTime(month.Year, month.Month, 1);
            int dir = month.CompareTo(shown);
            shown = month;
            if (select is DateTime s) selected = s;
            Draw();
            if (dir != 0) Anim.Appear(days, 120, fromX: dir * 14, ease: Anim.EaseOut);
        }

        // 날짜 칸 클릭: 같은 달이면 선택만, 다른 달(흐린 날짜)이면 그 달로 이동 + 선택
        void Select(DateTime d)
        {
            if (d.Year != shown.Year || d.Month != shown.Month) ShowMonth(d, d);
            else
            {
                selected = d;
                Draw();
            }
        }

        for (int i = 0; i < 42; i++)
        {
            int index = i;
            cells[i].Cell.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                // 두 번째 클릭은 칸이 아니라 첫 클릭으로 고른 날짜를 연다 (다른 달 날짜는 첫 클릭에 격자가 바뀌므로)
                if (e.ClickCount >= 2) OpenInCalendar(selected);
                else Select(cellDates[index]);
            };
        }
        prev.Click += (_, _) => ShowMonth(shown.AddMonths(-1));
        next.Click += (_, _) => ShowMonth(shown.AddMonths(1));
        monthButton.Click += (_, _) => ShowMonth(DateTime.Today, DateTime.Today);

        // 마우스 휠로 달 이동 (위 = 이전 달). 트랙패드처럼 잘게 오는 휠은 한 칸(120)씩 모아서
        int wheel = 0;
        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            wheel += e.Delta;
            while (Math.Abs(wheel) >= Mouse.MouseWheelDeltaForOneLine)
            {
                int step = wheel > 0 ? -1 : 1;
                wheel += step * Mouse.MouseWheelDeltaForOneLine;
                ShowMonth(shown.AddMonths(step));
            }
        }
        header.MouseWheel += OnWheel;
        weekHead.Grid.MouseWheel += OnWheel;
        daysHost.MouseWheel += OnWheel;
        selectedDay.MouseWheel += OnWheel;

        _refreshers.Add(Draw);
        return root;
    }

    /// <summary>설정한 캘린더(TopBar.CalendarApp)로 그 날을 열고 패널 닫기.</summary>
    private void OpenInCalendar(DateTime date)
    {
        var app = _services.Settings.Current.TopBar.CalendarApp;
        try { CalendarApps.Open(app, date); }
        catch (Exception ex) { Log.Error($"캘린더 열기 실패 ({app}, {date:yyyy-MM-dd})", ex); }
        Close();
    }

    /// <summary>"오늘" / "내일" / "어제" / "2일 후" / "3일 전".</summary>
    private static string RelativeDay(DateTime date, DateTime today)
    {
        int diff = (date.Date - today.Date).Days;
        return diff switch
        {
            0 => "오늘",
            1 => "내일",
            -1 => "어제",
            > 0 => $"{diff}일 후",
            _ => $"{-diff}일 전",
        };
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
