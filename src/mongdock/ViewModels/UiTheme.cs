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
        Accent = F("#FF0A64D6"),
        AccentText = F("#FFFFFFFF"),
        Hover = F("#0F000000"),
        Tile = F("#FFF0F0F2"),
        CircleOff = F("#FFE1E1E4"),
        CircleOffGlyph = F("#FF3A3A3C"),
        SliderTrack = F("#FFE6E6E8"),
        SliderFill = F("#FFFFFFFF"),
        SliderBorder = F("#24000000"),
        WindowBackground = F("#FFF5F5F7"),
        SidebarBackground = F("#FFE9E9EC"),
        GroupBackground = F("#FFFFFFFF"),
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
        Accent = F("#FF0A64D6"),
        AccentText = F("#FFFFFFFF"),
        Hover = F("#14FFFFFF"),
        Tile = F("#FF3A3A3C"),
        CircleOff = F("#FF505053"),
        CircleOffGlyph = F("#FFF2F2F2"),
        SliderTrack = F("#FF4A4A4D"),
        SliderFill = F("#FFE8E8EA"),
        SliderBorder = F("#33FFFFFF"),
        WindowBackground = F("#FF1E1E20"),
        SidebarBackground = F("#FF2A2A2C"),
        GroupBackground = F("#FF2C2C2E"),
        HolidayText = F("#FFFF6B61"),
        SaturdayText = F("#FF6AAEFF"),
    };

    private static SolidColorBrush F(string hex) => BrushParser.Frozen(BrushParser.Hex(hex));
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
