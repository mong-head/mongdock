using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 몽독 톤 입력 칸: 둥근 테두리(구분선 색), 누르면 테두리가 보라(SoftAccentLine) — 윈도우 기본 파란 포커스 테두리·검정 테두리 대신 (QA).
/// 선택 영역도 보라. 배경·글자색은 부르는 쪽이 정한 것 그대로 (안 정했으면 카드 타일색).
/// </summary>
internal static class MongField
{
    public static T Apply<T>(T box, UiPalette p) where T : TextBox
    {
        if (box.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue) box.Background = p.Tile;
        if (box.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue) box.Foreground = p.Text;
        box.BorderBrush = p.Divider;
        box.BorderThickness = new Thickness(1);
        box.CaretBrush = p.Text;
        box.SelectionBrush = p.AccentFill;
        box.SelectionOpacity = 0.35;
        box.Template = Template(p);
        return box;
    }

    private static ControlTemplate Template(UiPalette p)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(UIElement.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(Control.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        border.AppendChild(host);
        var t = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, p.SoftAccentLine, "Bd"));
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1.5), "Bd"));
        t.Triggers.Add(focus);
        var hover = new MultiTrigger();
        hover.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
        hover.Conditions.Add(new Condition(UIElement.IsKeyboardFocusedProperty, false));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, p.SoftAccentLine, "Bd"));
        t.Triggers.Add(hover);
        var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5, "Bd"));
        t.Triggers.Add(off);
        return t;
    }
}
