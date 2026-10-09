using System.Windows;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.ViewModels;

/// <summary>
/// UI 글꼴을 한 곳에서 관리. 모든 창·메뉴·패널은 앱 리소스 <see cref="Key"/> 를 DynamicResource 로 쓴다.
/// 기본 "Pretendard" 는 앱에 내장(Fonts\*.otf, OFL) — 설치 없이 사용. 그 외 이름은 시스템 글꼴로 해석.
/// </summary>
public static class UiFonts
{
    public const string Key = "UiFont";
    /// <summary>
    /// 대체 글꼴 (#23): Pretendard 에 없는 글자(일본어 가나·한자, 중국어)는 화면 언어의 윈도우 기본 UI 글꼴을 먼저 —
    /// 같은 한자라도 언어마다 글자 모양이 달라서 (일본어 Yu Gothic UI, 간체 Microsoft YaHei UI, 번체 Microsoft JhengHei UI).
    /// 그 뒤는 공통 순서 — 다른 언어 글자(예: 한국어 화면의 일본어 알림)도 네모로 안 깨지게.
    /// </summary>
    private static string Fallback => (Loc.Code switch
    {
        "ja" => "Yu Gothic UI, ",
        "zh-Hans" => "Microsoft YaHei UI, ",
        "zh-Hant" => "Microsoft JhengHei UI, ",
        _ => "",
    }) + "Segoe UI Variable Text, Malgun Gothic, Yu Gothic UI, Microsoft YaHei UI, Microsoft JhengHei UI";
    private static string? _applied;

    /// <summary>설정 값 → FontFamily. "Pretendard"(대소문자 무시)면 내장 글꼴 + 대체 글꼴.</summary>
    public static FontFamily Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals("Pretendard", StringComparison.OrdinalIgnoreCase))
        {
            // 이 어셈블리의 리소스 (미리보기 등 다른 실행 파일에서 써도 동작하도록 component URI)
            string asm = typeof(UiFonts).Assembly.GetName().Name ?? AppInfo.Name;
            return new FontFamily(new Uri($"pack://application:,,,/{asm};component/"), "./Fonts/#Pretendard, " + Fallback);
        }
        return new FontFamily(name.Trim() + ", " + Fallback); // 사용자가 고른 글꼴에 없는 글자도 대체
    }

    /// <summary>설정의 글꼴로 앱 리소스 교체 (바뀔 때만). 시작 시·SettingsChanged 시 호출.</summary>
    public static void Apply(Settings settings)
    {
        string name = settings.FontFamily ?? "";
        if (_applied == name || Application.Current == null) return;
        try
        {
            Application.Current.Resources[Key] = Resolve(name);
            _applied = name;
        }
        catch (Exception ex)
        {
            Log.Error($"글꼴 '{name}' 적용 실패", ex);
            Application.Current.Resources[Key] = new FontFamily(Fallback);
            _applied = name;
        }
    }
}
