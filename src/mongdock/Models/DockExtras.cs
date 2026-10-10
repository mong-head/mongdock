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

[JsonConverter(typeof(CamelEnumConverter<FolderDisplay>))]
public enum FolderDisplay { Stack, Folder }

/// <summary>독 폴더 핀(PinKind.Folder)의 옵션. 폴더 경로는 PinItem.Target.</summary>
public sealed class FolderOptions
{
    /// <summary>판의 정렬: 추가된 날짜(최근 것 먼저) / 이름.</summary>
    public FolderSort Sort { get; set; } = FolderSort.Added;
    /// <summary>독 아이콘: 폴더 아이콘(기본) / 최근 파일 겹치기.</summary>
    public FolderDisplay Display { get; set; } = FolderDisplay.Folder;
}

// ───────────────────────── 루틴 (#24-A, spec-routines.md §8) ─────────────────────────

[JsonConverter(typeof(CamelEnumConverter<RoutineDesktopMode>))]
public enum RoutineDesktopMode { Current, New, Index }

public sealed class RoutineDesktop
{
    public RoutineDesktopMode Mode { get; set; } = RoutineDesktopMode.Current;
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

/// <summary>루틴 핀(PinKind.Routine)의 내용. 이름은 PinItem.Name.</summary>
public sealed class RoutineData
{
    public RoutineDesktop Desktop { get; set; } = new();
    public List<RoutineItem> Items { get; set; } = new();
}
