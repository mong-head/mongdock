using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 몽독 툴팁 (2026-10-13 사용자: 윈도우 기본 툴팁(네모·회색 테두리)이 구림) — 몽독 모든 창의 ToolTip 을 앱 전체 기본 스타일로.
/// 독 이름 말풍선과 같은 모양: 둥근 모서리 8, 은은한 그림자, 메뉴와 같은 라이트·다크 색(Menu.* 리소스 — 테마가 바뀌면 같이), 몽독 글꼴, 여백 10·6.
/// 자리: 가리키는 요소 바로 위 가운데(화면 밖이면 아래), 커서를 따라다니지 않음. 0.4초(윈도우 기본 머무름 시간) 뒤 120ms 흐릿→보임, 사라질 때는 바로.
/// 윈도우 애니메이션이 꺼져 있으면 흐려짐 없이. 형식 기본값(OverrideMetadata)은 건드리지 않음 — 앱 리소스의 형식 스타일만 (시작 순서 충돌 없음).
/// </summary>
internal static class MongToolTip
{
    public static void Init()
    {
        var app = Application.Current;
        if (app is null || app.Resources.Contains(typeof(ToolTip))) return;
        var style = new Style(typeof(ToolTip));
        style.Setters.Add(new Setter(ToolTip.PlacementProperty, PlacementMode.Custom));
        style.Setters.Add(new Setter(ToolTip.CustomPopupPlacementCallbackProperty, new CustomPopupPlacementCallback(Place)));
        style.Setters.Add(new Setter(ToolTip.HasDropShadowProperty, false));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Menu.Text")));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new DynamicResourceExtension(UiFonts.Key)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, UiText.Sub + 0.5));
        style.Setters.Add(new Setter(Control.TemplateProperty, Template()));
        app.Resources[typeof(ToolTip)] = style;
    }

    private static ControlTemplate Template()
    {
        // 그림자 여백만큼 바깥 칸 (그림자가 잘리지 않게) → 둥근 판 → 내용
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.SetValue(Border.PaddingProperty, new Thickness(8));
        outer.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var card = new FrameworkElementFactory(typeof(Border));
        card.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        card.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));
        card.SetResourceReference(Border.BackgroundProperty, "Menu.Background");
        card.SetResourceReference(Border.BorderBrushProperty, "Menu.Border");
        card.SetValue(Border.BorderThicknessProperty, new Thickness(0.5));
        card.SetValue(UIElement.EffectProperty, new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.18 });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        card.AppendChild(content);
        outer.AppendChild(card);
        var t = new ControlTemplate(typeof(ToolTip)) { VisualTree = outer };
        // 나타날 때 120ms 흐릿→보임 (윈도우 애니메이션이 꺼져 있으면 Anim.Ms 가 0 — 바로)
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        var sb = new Storyboard();
        sb.Children.Add(fade);
        var opened = new EventTrigger(ToolTip.OpenedEvent);
        opened.Actions.Add(new BeginStoryboard { Storyboard = sb });
        if (Anim.Enabled) t.Triggers.Add(opened);
        return t;
    }

    /// <summary>요소 바로 위 가운데, 위로 나가면 바로 아래 가운데 (8 은 그림자 여백).</summary>
    private static CustomPopupPlacement[] Place(Size popup, Size target, Point offset) => new[]
    {
        new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, -popup.Height + 4), PopupPrimaryAxis.Horizontal),
        new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, target.Height - 4), PopupPrimaryAxis.Horizontal),
    };

    /// <summary>
    /// 잘린 이름(…)일 때만 전체 이름 툴팁 — 다 보이면 띄우지 않음 (사용자). owner 에 툴팁을 달고, 열릴 때 text 가 잘렸는지 봄.
    /// </summary>
    public static void IfTrimmed(FrameworkElement owner, TextBlock text, string full)
    {
        owner.ToolTip = full;
        owner.ToolTipOpening += (_, e) =>
        {
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (text.DesiredSize.Width <= text.ActualWidth + 0.5) e.Handled = true; // 다 보임 → 안 띄움
        };
    }
}
