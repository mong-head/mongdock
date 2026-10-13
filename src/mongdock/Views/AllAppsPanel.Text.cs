using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 판 글자는 판 배율(UiScale — 아이콘·칸만 키우는 LayoutTransform)을 받지 않고 몽독 공통 크기(UiText) 그대로 (2026-10-13 사용자: 판 글씨가 다른 화면보다 큼).
/// 판 안 글자 크기를 배율로 나눠 두면 화면에서는 원래 크기로 보임. 아이콘 글꼴(Segoe Fluent Icons)과 큰 기호(16 이상 — "+" 등)는 칸과 같이 커지게 둠.
/// </summary>
internal sealed partial class AllAppsPanel
{
    private static readonly DependencyProperty BaseFontSizeProperty =
        DependencyProperty.RegisterAttached("BaseFontSize", typeof(double), typeof(AllAppsPanel), new PropertyMetadata(double.NaN));

    /// <summary>root 아래(논리 트리) 글자 크기를 배율만큼 줄여 화면에서 공통 크기로 보이게. 같은 요소를 두 번 줄이지 않음.</summary>
    /// <summary>
    /// 판 글자 배율 안 (2026-10-13 사용자 고르기 중): "A" = 아이콘과 같이(예전), "B" = 설정 창 크기, "C" = 아이콘 배율의 절반만.
    /// 앱 이름·폴더 이름(Uid "name")은 어느 안이든 아이콘과 같이 커짐.
    /// </summary>
    internal static string TextMode { get; set; } = "A"; // 2026-10-13 사용자 선택: A (글자도 아이콘과 같이)

    private double TextScale => TextMode switch { "A" => UiScale, "C" => 1 + (UiScale - 1) / 2, _ => 1 };

    internal void UnscaleText(DependencyObject? root)
    {
        if (root is null || UiScale <= 1.001 || TextMode == "A") return;
        foreach (var d in Logical(root))
        {
            if (d is not FrameworkElement fe || fe.Uid == "name") continue;
            var prop = fe is Control ? Control.FontSizeProperty : fe is TextBlock ? TextBlock.FontSizeProperty : null;
            if (prop is null) continue;
            if (fe.ReadLocalValue(prop) == DependencyProperty.UnsetValue) continue; // 물려받는 크기는 위에서 이미 줄임
            double baseSize = (double)fe.GetValue(BaseFontSizeProperty);
            if (double.IsNaN(baseSize))
            {
                baseSize = (double)fe.GetValue(prop);
                fe.SetValue(BaseFontSizeProperty, baseSize);
            }
            var family = fe is TextBlock tb ? tb.FontFamily : ((Control)fe).FontFamily;
            if (baseSize >= 16 || family?.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase) == true || family?.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase) == true) continue;
            fe.SetValue(prop, baseSize * TextScale / UiScale);
        }
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Logical(child)) yield return d;
    }

    /// <summary>판 기본 글자(물려받는 크기): 공통 본문 크기를 배율로 나눔.</summary>
    private void ApplyBaseText(FrameworkElement shell)
    {
        if (UiScale > 1.001 && TextMode != "A") TextElement.SetFontSize(shell, UiText.Body * TextScale / UiScale);
        UnscaleText(shell);
    }
}
