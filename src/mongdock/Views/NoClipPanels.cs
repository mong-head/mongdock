using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mongdock.Views;

/// <summary>
/// 배치 잘림(layout clip)을 하지 않는 판·줄 — 독 판이 원하는 길이가 창보다 길어지는 순간(확대 중) WPF 가 판 경계로 잘라,
/// 판 밖(세로 독은 화면 안쪽)으로 튀어나온 확대 아이콘이 잘려 보였음 (사용자 스크린샷 — report-test --clipdemo 로 재현). 창 경계로만 잘림.
/// </summary>
public sealed class NoClipBorder : Border
{
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;
}

/// <inheritdoc cref="NoClipBorder"/>
public sealed class NoClipStackPanel : StackPanel
{
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;
}
