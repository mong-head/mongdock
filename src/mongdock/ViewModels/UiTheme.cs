using System.Windows;
using System.Windows.Media;
using Mongdock.Converters;
using Mongdock.Models;

namespace Mongdock.ViewModels;

/// <summary>
/// 메뉴·상태 패널·확인 카드가 쓰는 맥 스타일 색 (라이트/다크).
/// 메뉴 템플릿(Themes/Menus.xaml)은 DynamicResource 로 이 색을 쓰므로 <see cref="Apply"/> 로 앱 리소스를 갈아 끼운다.
/// </summary>
public sealed class UiPalette
{
    public bool IsLight { get; private init; }
    public Brush CardBackground { get; private init; } = Brushes.White;
    public Brush CardBorder { get; private init; } = Brushes.Transparent;
    public Color ShadowColor { get; private init; }
    public double ShadowOpacity { get; private init; }
    public Brush Text { get; private init; } = Brushes.Black;
    public Brush SubText { get; private init; } = Brushes.Gray;
    public Brush Disabled { get; private init; } = Brushes.Gray;
    public Brush Divider { get; private init; } = Brushes.LightGray;
    public Brush Accent { get; private init; } = Brushes.Blue;
    public Brush AccentText { get; private init; } = Brushes.White;
    /// <summary>강조 글자·링크·고리 (몽독 톤 — 바탕 위에서 읽히게 진하게).</summary>
    public Brush AccentInk { get; private init; } = Brushes.SlateBlue;
    /// <summary>켜짐 채움 (스위치 켜짐·슬라이더·진행 막대·고른 동그라미): 몽독 하늘~연보라.</summary>
    public Brush AccentFill { get; private init; } = Brushes.MediumPurple;
    /// <summary>몽독 톤 강조 (#24 새 화면): 연보라 틴트 바탕 + 진한 보라 글자 — 쨍한 파랑 대신. 고른 칸·주 버튼.</summary>
    public Brush SoftAccent { get; private init; } = Brushes.Lavender;
    public Brush SoftAccentText { get; private init; } = Brushes.DarkSlateBlue;
    /// <summary>몽독 톤 선 (놓을 곳 강조·고른 칸 테두리·검색 선택).</summary>
    public Brush SoftAccentLine { get; private init; } = Brushes.MediumPurple;
    /// <summary>
    /// 앱 모음 판의 "연한 채움" (2026-10-13 사용자: 연보라 바탕이 아이콘 색과 안 어울림 → 회색기 도는 연보라): 추천 묶음·열린 폴더 칸·펼친 폴더 판·카드 버튼 바탕.
    /// 판 바탕보다 한 단계 진하게(다크는 한 단계 밝게). 글자·테두리는 PanelTintText·PanelTintLine. "지금 고른 것"(포커스·체크·토글·놓을 곳)은 SoftAccent* 그대로.
    /// </summary>
    public Brush PanelTint { get; private init; } = Brushes.Lavender;
    public Brush PanelTintText { get; private init; } = Brushes.DimGray;
    public Brush PanelTintLine { get; private init; } = Brushes.LightGray;
    /// <summary>판 칸 마우스 올림 (회색 채움 대신): 흰 판 위 = 아주 연한 회보라, 연한 채움 판 위(OnTint) = 흰색 반투명. 목록 줄 누름 = CellPress.</summary>
    public Brush CellHover { get; private init; } = Brushes.WhiteSmoke;
    public Brush CellHoverOnTint { get; private init; } = Brushes.White;
    public Brush CellPress { get; private init; } = Brushes.Gainsboro;
    /// <summary>판 "추가" 칸 기호 (+ · 폴더+): 진한 보라. 칸 바탕은 PanelTint, 연한 판 위에서는 AddTileOnTint.</summary>
    public Brush AddGlyph { get; private init; } = Brushes.SlateBlue;
    public Brush AddTileOnTint { get; private init; } = Brushes.White;
    /// <summary>몽독 체크 (안 켜짐 바탕·테두리). 켜지면 하늘~연보라 그라데이션.</summary>
    public Brush CheckOff { get; private init; } = Brushes.White;
    public Brush CheckBorder { get; private init; } = Brushes.Gray;
    public Brush Hover { get; private init; } = Brushes.LightGray;
    public Brush Tile { get; private init; } = Brushes.WhiteSmoke;
    public Brush CircleOff { get; private init; } = Brushes.LightGray;
    public Brush CircleOffGlyph { get; private init; } = Brushes.Black;
    public Brush SliderTrack { get; private init; } = Brushes.LightGray;
    public Brush SliderFill { get; private init; } = Brushes.White;
    public Brush SliderBorder { get; private init; } = Brushes.Gray;
    // 설정 창 (맥 "시스템 설정" 느낌: 사이드바 + 내용 + 둥근 그룹 카드)
    public Brush WindowBackground { get; private init; } = Brushes.White;
    public Brush SidebarBackground { get; private init; } = Brushes.WhiteSmoke;
    public Brush GroupBackground { get; private init; } = Brushes.White;
    /// <summary>설정·신고 창 카드 테두리 (아주 옅은 라벤더).</summary>
    public Brush GroupBorder { get; private init; } = Brushes.Lavender;
    // 달력: 일요일·공휴일(빨강), 토요일(파랑) 글자색
    public Brush HolidayText { get; private init; } = Brushes.Red;
    public Brush SaturdayText { get; private init; } = Brushes.Blue;

    public static readonly UiPalette Light = new()
    {
        IsLight = true,
        CardBackground = F("#FAF9F9FA"),
        CardBorder = F("#1F000000"),
        ShadowColor = Colors.Black,
        ShadowOpacity = 0.22,
        Text = F("#FF1D1D1F"),
        SubText = F("#8C000000"),
        Disabled = F("#4D000000"),
        Divider = F("#14000000"),
        Accent = F("#FFD9CFFF"),
        AccentText = F("#FF4A3AA8"),
        AccentInk = F("#FF5B4BC4"),
        AccentFill = Fill(0x8C, 0x7E, 0xEE, 0x6F, 0x5F, 0xD8), // 연보라 (파랑 기 없이 — #25 사용자: 설정 창 색 조화). 흰 카드 위 3:1 넘게
        SoftAccent = F("#FFECE7FF"),
        SoftAccentText = F("#FF4A3AA8"),
        SoftAccentLine = F("#FFB4A3FF"),
        PanelTint = F("#FFE9E5F6"),
        PanelTintText = F("#FF6E6A86"),
        PanelTintLine = F("#FFC6C0DE"),
        CellHover = F("#146E6A86"),
        CellHoverOnTint = F("#66FFFFFF"),
        CellPress = F("#246E6A86"),
        AddGlyph = F("#FF534AB7"),
        AddTileOnTint = F("#99FFFFFF"),
        CheckOff = F("#FFFFFFFF"),
        CheckBorder = F("#40000000"),
        Hover = F("#0F000000"),
        Tile = F("#FFF0F0F2"),
        CircleOff = F("#FFE1E1E4"),
        CircleOffGlyph = F("#FF3A3A3C"),
        SliderTrack = F("#FFE6E6E8"),
        SliderFill = F("#FFFFFFFF"),
        SliderBorder = F("#24000000"),
        WindowBackground = F("#FFFAF9FE"), // 라벤더 화이트 (회색 바탕이 사이드바·아이콘과 안 어울림 — 사용자)
        SidebarBackground = F("#FFF3F1FA"), // 아주 옅은 라벤더 회색 (몽독 판 아이콘과 어울리게)
        GroupBackground = F("#FFFFFFFF"),
        GroupBorder = F("#FFECE8F8"),
        HolidayText = F("#FFE0352B"),
        SaturdayText = F("#FF1F6FD6"),
    };

    public static readonly UiPalette Dark = new()
    {
        IsLight = false,
        CardBackground = F("#F72B2B2D"),
        CardBorder = F("#33FFFFFF"),
        ShadowColor = Colors.Black,
        ShadowOpacity = 0.45,
        Text = F("#FFF2F2F2"),
        SubText = F("#99FFFFFF"),
        Disabled = F("#4DFFFFFF"),
        Divider = F("#1FFFFFFF"),
        Accent = F("#FF443C70"),
        AccentText = F("#FFE4DEFF"),
        AccentInk = F("#FFC9BEFF"),
        AccentFill = Fill(0xB4, 0xA6, 0xFF, 0x96, 0x84, 0xF6), // 어두운 카드 위엔 밝은 연보라
        SoftAccent = F("#FF3D3763"),
        SoftAccentText = F("#FFD8CFFF"),
        SoftAccentLine = F("#FF8E7EE0"),
        PanelTint = F("#FF3D3953"),
        PanelTintText = F("#FFC3BFD6"),
        PanelTintLine = F("#FF55536A"),
        CellHover = F("#17FFFFFF"),
        CellHoverOnTint = F("#17FFFFFF"),
        CellPress = F("#26FFFFFF"),
        AddGlyph = F("#FFC3B8FF"),
        AddTileOnTint = F("#14FFFFFF"),
        CheckOff = F("#FF3A3A3C"),
        CheckBorder = F("#4DFFFFFF"),
        Hover = F("#14FFFFFF"),
        Tile = F("#FF3A3A3C"),
        CircleOff = F("#FF505053"),
        CircleOffGlyph = F("#FFF2F2F2"),
        SliderTrack = F("#FF4A4A4D"),
        SliderFill = F("#FFE8E8EA"),
        SliderBorder = F("#33FFFFFF"),
        WindowBackground = F("#FF2D2B38"), // 사이드바(#262430)보다 한 단계 밝은 라벤더 그레이
        SidebarBackground = F("#FF262430"), // 라벤더 기가 도는 어두운 회색
        GroupBackground = F("#FF363443"),
        GroupBorder = F("#FF423F52"),
        HolidayText = F("#FFFF6B61"),
        SaturdayText = F("#FF6AAEFF"),
    };

    private static SolidColorBrush F(string hex) => BrushParser.Frozen(BrushParser.Hex(hex));

    /// <summary>몽독 하늘 → 연보라 채움 (라이트는 한 단계 진하게 — 흰 바탕 위 켜짐이 잘 보이게).</summary>
    private static Brush Fill(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var b = new LinearGradientBrush(Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2), 0);
        b.Freeze();
        return b;
    }
}

public static class UiTheme
{
    private static bool? _applied;

    /// <summary>독 테마 설정(System/Light/Dark)으로 라이트 여부 판단.</summary>
    public static bool IsLight(Settings settings) => settings.Dock.Theme switch
    {
        DockTheme.Light => true,
        DockTheme.Dark => false,
        _ => SystemTheme.AppsUseLightTheme(),
    };

    public static UiPalette Palette(Settings settings) => IsLight(settings) ? UiPalette.Light : UiPalette.Dark;

    /// <summary>메뉴 템플릿이 쓰는 앱 리소스 색을 테마에 맞게 갱신 (바뀔 때만).</summary>
    public static void Apply(Settings settings)
    {
        PerfMode.Sync(settings);
        Views.TouchSupport.ApplyResources(); // 터치 장치면 메뉴 행 높이 +4
        bool light = IsLight(settings);
        if (_applied == light || Application.Current == null) return;
        _applied = light;
        var p = light ? UiPalette.Light : UiPalette.Dark;
        var r = Application.Current.Resources;
        r["Menu.Background"] = p.CardBackground;
        r["Menu.Border"] = p.CardBorder;
        r["Menu.Text"] = p.Text;
        r["Menu.Disabled"] = p.Disabled;
        r["Menu.Divider"] = p.Divider;
        r["Menu.Accent"] = p.Accent;
        r["Menu.AccentText"] = p.AccentText;
        r["Menu.ShadowOpacity"] = p.ShadowOpacity;
    }
}
