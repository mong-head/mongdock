using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mongdock.Models;

/// <summary>JSON 에 소문자 시작(camelCase)으로 쓰는 enum ("current", "added" …). 읽을 때는 대소문자 무시.</summary>
public sealed class CamelEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase) where T : struct, Enum;

// ───────────────────────── 독 아이콘 고르기 (루틴·독 폴더 공통) ─────────────────────────

[JsonConverter(typeof(CamelEnumConverter<PinIconMode>))]
public enum PinIconMode { Auto, Glyph, File }

/// <summary>
/// 루틴·독 폴더 아이콘 (#24 "아이콘 고르기"). auto = 기본 그림(루틴은 항목 2x2, 폴더는 최근 파일 겹침/폴더),
/// glyph = 둥근 판(color) 위 흰 기호(Segoe Fluent Icons 글리프) 또는 글자 1~2자(text), file = icons\routines\ 안 그림.
/// </summary>
public sealed class PinIcon
{
    public PinIconMode Mode { get; set; } = PinIconMode.Auto;
    /// <summary>Segoe Fluent Icons 글리프 한 글자 (예 "").</summary>
    public string? Glyph { get; set; }
    /// <summary>판 색 이름: mongdock · blue · green · orange · red · pink · gray · black.</summary>
    public string? Color { get; set; }
    /// <summary>기호 대신 넣을 글자 1~2자.</summary>
    public string? Text { get; set; }
    /// <summary>그림 파일 (몽독 데이터 폴더 안 복사본 경로).</summary>
    public string? File { get; set; }
}

// ───────────────────────── 독 폴더 (#24-B) ─────────────────────────

[JsonConverter(typeof(CamelEnumConverter<FolderSort>))]
public enum FolderSort { Added, Name }


/// <summary>독 폴더 핀(PinKind.Folder)의 옵션. 폴더 경로는 PinItem.Target.</summary>
public sealed class FolderOptions
{
    /// <summary>판의 정렬: 추가된 날짜(최근 것 먼저) / 이름.</summary>
    public FolderSort Sort { get; set; } = FolderSort.Added;
    /// <summary>마지막으로 판을 연(또는 독에 넣은) 시각 (UTC). 그 뒤 추가된 파일이 있으면 아이콘에 새 파일 점. (옛 "display" 값은 읽을 때 무시)</summary>
    public DateTime? LastOpened { get; set; }
}

// ───────────────────────── 루틴 (#24-A, spec-routines.md §8) ─────────────────────────

[JsonConverter(typeof(CamelEnumConverter<RoutineDesktopMode>))]
public enum RoutineDesktopMode { Current, New, Index }

public sealed class RoutineDesktop
{
    /// <summary>기본 = 새 데스크톱 (2026-10-10 사용자 결정 — 끝내기 때 그 데스크톱도 같이 닫음).</summary>
    public RoutineDesktopMode Mode { get; set; } = RoutineDesktopMode.New;
    /// <summary>Mode=Index 일 때 데스크톱 번호 (1부터).</summary>
    public int Index { get; set; } = 1;
}

[JsonConverter(typeof(CamelEnumConverter<RoutineItemKind>))]
public enum RoutineItemKind { App, Url, Path }

[JsonConverter(typeof(CamelEnumConverter<RoutineMonitorMode>))]
public enum RoutineMonitorMode { Keep, Primary, Index }

public sealed class RoutineMonitor
{
    public RoutineMonitorMode Mode { get; set; } = RoutineMonitorMode.Keep;
    /// <summary>Mode=Index 일 때 모니터 번호 (1부터). deviceName 으로 먼저 찾고, 없으면 번호, 그것도 없으면 주 모니터.</summary>
    public int Index { get; set; } = 1;
    public string? DeviceName { get; set; }
}

[JsonConverter(typeof(CamelEnumConverter<RoutinePlacementMode>))]
public enum RoutinePlacementMode { Keep, Max, Left, Right, Top, Bottom, Saved, Min }

public sealed class RoutinePlacement
{
    public RoutinePlacementMode Mode { get; set; } = RoutinePlacementMode.Keep;
    /// <summary>Mode=Saved 일 때 모니터 작업 영역 기준 0~1 비율 [x, y, w, h].</summary>
    public double[]? Rect { get; set; }
}

[JsonConverter(typeof(CamelEnumConverter<RoutineIfRunning>))]
public enum RoutineIfRunning { Focus, New }

public sealed class RoutineItem
{
    public RoutineItemKind Kind { get; set; } = RoutineItemKind.App;
    /// <summary>앱: exe 경로 / url: 주소 / path: 파일·폴더 경로.</summary>
    public string Target { get; set; } = "";
    /// <summary>스토어 앱이면 AUMID (Target 대신).</summary>
    public string? Aumid { get; set; }
    /// <summary>표시 이름 (없으면 대상에서).</summary>
    public string? Name { get; set; }
    /// <summary>앱과 함께 열 파일·폴더·웹 주소.</summary>
    public string? Open { get; set; }
    /// <summary>고급: 실행 인자 문자열.</summary>
    public string? Args { get; set; }
    public RoutineMonitor? Monitor { get; set; }
    public RoutinePlacement? Placement { get; set; }
    /// <summary>다음 항목까지 기다리기 (0~10000ms).</summary>
    public int DelayMs { get; set; }
    public RoutineIfRunning IfRunning { get; set; } = RoutineIfRunning.Focus;
    /// <summary>나중(항목마다 다른 데스크톱) 여지 — 지금은 쓰지 않음.</summary>
    public RoutineDesktop? Desktop { get; set; }
}

/// <summary>옛 모양: 루틴 핀 안에 들어 있던 내용 (읽을 때 Settings.Routines 로 옮김).</summary>
public sealed class RoutineData
{
    public RoutineDesktop Desktop { get; set; } = new();
    public List<RoutineItem> Items { get; set; } = new();
}

/// <summary>
/// 루틴 하나 (settings.json "routines"). 모든 루틴은 앱 모음 판 맨 위 "루틴" 줄에 있고, 독에는 고정한 것만
/// (PinKind.Routine 핀, Target = 이 Id). 최대 12개, 항목 최대 15개.
/// </summary>
public sealed class RoutineDef
{
    public const int MaxRoutines = 12, MaxItems = 15;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>아이콘 고르기 (null·auto = 담긴 항목 아이콘 2x2).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PinIcon? Icon { get; set; }
    public RoutineDesktop Desktop { get; set; } = new();
    public List<RoutineItem> Items { get; set; } = new();
    /// <summary>"지금 화면 저장"으로 만든 루틴인지 (사용 통계 savedFromScreen 용).</summary>
    public bool FromScreen { get; set; }
}

// ───────────────────────── 앱 모음 판 (#24) ─────────────────────────

/// <summary>앱 모음 판의 묶음 하나 (화면 순서 = 목록 순서). 기본 묶음 id: work·chat·web·media·music·games·dev·tools·other, 사용자 묶음은 "g-…".</summary>
public sealed class AppGroupDef
{
    public string Id { get; set; } = "";
    /// <summary>사용자가 바꾼 이름. null 이면 기본 이름(번역).</summary>
    public string? Name { get; set; }
}

/// <summary>
/// 앱 모음 판 (settings.json "allApps"). 앱 키 = shell:AppsFolder 파싱 이름. 설정 옮기기에 포함 (실행 횟수는 usage-local.json — 제외).
/// </summary>
public sealed class AllAppsSettings
{
    /// <summary>★ 즐겨찾기 — 사용자가 고른 앱만 (순서대로).</summary>
    public List<string> Favorites { get; set; } = new();
    /// <summary>즐겨찾기 추천 보이기 (이 PC 실행 기록으로 — 끄면 추천 칸·카드 안 보이고 실행 기록도 멈추고 지움).</summary>
    public bool ShowSuggestions { get; set; } = true;
    /// <summary>옛 이름 "fillFrequent" 를 읽을 때만 (→ ShowSuggestions). 쓰지 않음.</summary>
    [JsonPropertyName("fillFrequent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyFillFrequent { get => null; set { if (value is bool b) ShowSuggestions = b; } }
    /// <summary>추천에서 뺀 앱 → 뺀 날 (30일 동안 추천 안 함). 이 PC 것 — 설정 옮기기 제외.</summary>
    public Dictionary<string, DateTime> DismissedSuggestions { get; set; } = new();
    /// <summary>즐겨찾기 0개 카드에서 [다음에] — 이 날까지 카드 안 보임 (7일).</summary>
    public DateTime? SuggestCardSnoozedUntil { get; set; }
    /// <summary>묶음 순서·이름·사용자 묶음. 비어 있으면 기본 순서.</summary>
    public List<AppGroupDef> Groups { get; set; } = new();
    /// <summary>사용자가 옮긴 앱 → 묶음 id (자동 분류보다 우선).</summary>
    public Dictionary<string, string> Overrides { get; set; } = new();
    /// <summary>숨긴 앱.</summary>
    public List<string> Hidden { get; set; } = new();
    /// <summary>묶음을 직접 바꾼 적 있음 (사용 통계 "allAppsCustomized" 용).</summary>
    public bool Customized { get; set; }
}
