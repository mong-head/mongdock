using System.Globalization;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 디버그: 환경 변수 MONGDOCK_FAKE_SCREEN="1366x768@125" (또는 "1080x1920@150", 배율 생략 = 100) 이면
/// 주 모니터를 그 해상도·배율의 화면처럼 보이게 해 상단바·독·패널·Spotlight·코치마크 배치를 낮은 노트북/세로 모니터로 시험한다.
/// 없으면 아무 영향 없음 (Release 에서도 무해).
///
/// 방식: 창의 실제 DPI 는 바꿀 수 없으므로 배율은 그대로 두고, 주 모니터 영역을 "가짜 화면의 DIP 크기" 만큼으로 줄인다.
///   예) 실제 1920×1080 @100% 에서 "1366x768@125" → DIP 1093×614 → 주 모니터 영역 = 왼쪽 위부터 1093×614 px.
///   레이아웃 계산(PanelFit·DockFit·TopBarFit·패널 최대 높이·Spotlight 폭)은 모두 DIP 기준이라 그 노트북과 같은 결과가 나오고,
///   글자·아이콘은 실제 배율(이 PC 100%)로 그려져 조금 작게 보일 뿐이다.
/// 작업 영역: 왼쪽·위는 실제 작업 영역 그대로(같은 원점), 오른쪽·아래는 가짜 영역 안에 예약(몽독 독 등)이 있으면 그 값,
///   아니면 실제 화면 가장자리의 예약 두께(작업 표시줄)를 가짜 가장자리에서 뺀다.
/// 적용 범위: <see cref="Monitors"/> 가 만드는 MonitorInfo(주 모니터)와 DesktopWindowService 의 주 모니터 영역.
///   창 밀어내기(WindowNudger)·전체 화면 판정·알림 숨김처럼 다른 앱 창을 다루는 곳은 실제 값 그대로 (다른 창을 건드리지 않게).
/// </summary>
internal static class FakeScreen
{
    public const string Variable = "MONGDOCK_FAKE_SCREEN";

    private static readonly Lazy<(int W, int H, double Scale)?> Spec = new(() =>
    {
        var raw = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!TryParse(raw, out int w, out int h, out double s))
        {
            Log.Warn($"{Variable} 형식 오류 (예: 1366x768@125): '{raw}' — 무시");
            return null;
        }
        Log.Info($"가짜 화면 사용 ({Variable}): {w}×{h} @{s * 100:0}% → DIP {w / s:0}×{h / s:0}");
        return (w, h, s);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>가짜 화면을 쓰는 중인지.</summary>
    public static bool Active => Spec.Value is not null;

    /// <summary>"1366x768@125", "1080×1920@150%", "1280x800" → 물리 해상도와 배율(1.25). 배율 100~500%.</summary>
    internal static bool TryParse(string spec, out int width, out int height, out double scale)
    {
        width = height = 0;
        scale = 1;
        var s = spec.Trim().Replace('×', 'x').Replace('X', 'x').Replace(" ", "");
        string size = s;
        int at = s.IndexOf('@');
        if (at >= 0)
        {
            size = s[..at];
            var pct = s[(at + 1)..].TrimEnd('%');
            if (!double.TryParse(pct, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) || p < 100 || p > 500) return false;
            scale = p / 100.0;
        }
        var parts = size.Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
            && width >= 320 && height >= 240 && width <= 16384 && height <= 16384;
    }

    /// <summary>주 모니터의 실제 영역(px)·배율 → 가짜 영역(px). 가짜를 쓰지 않으면 그대로.</summary>
    internal static void Apply(ref RECT bounds, ref RECT work, double realScale)
    {
        if (Spec.Value is not { } spec) return;
        (bounds, work) = Compute(bounds, work, realScale, spec.W, spec.H, spec.Scale);
    }

    /// <summary>계산만 (시험용으로 분리).</summary>
    internal static (RECT Bounds, RECT Work) Compute(RECT realBounds, RECT realWork, double realScale, int fakeW, int fakeH, double fakeScale)
    {
        if (realScale <= 0) realScale = 1;
        int w = Math.Max(1, (int)Math.Round(fakeW / fakeScale * realScale));
        int h = Math.Max(1, (int)Math.Round(fakeH / fakeScale * realScale));
        var b = new RECT(realBounds.Left, realBounds.Top, realBounds.Left + w, realBounds.Top + h);

        int left = Math.Clamp(realWork.Left, b.Left, b.Right);
        int top = Math.Clamp(realWork.Top, b.Top, b.Bottom);
        // 가짜가 실제보다 작고 그 안에 예약이 있으면(몽독 독) 그 값, 아니면 실제 가장자리 예약 두께를 가짜 가장자리에서 뺌
        int right = b.Right <= realBounds.Right && realWork.Right < b.Right ? realWork.Right : b.Right - Math.Max(0, realBounds.Right - realWork.Right);
        int bottom = b.Bottom <= realBounds.Bottom && realWork.Bottom < b.Bottom ? realWork.Bottom : b.Bottom - Math.Max(0, realBounds.Bottom - realWork.Bottom);
        right = Math.Max(left + 1, right);
        bottom = Math.Max(top + 1, bottom);
        return (b, new RECT(left, top, right, bottom));
    }
}
