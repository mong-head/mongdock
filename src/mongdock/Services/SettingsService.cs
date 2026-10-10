using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// %APPDATA%\mongdock\settings.json 로드/저장 + 외부 편집 감지.
/// - 저장은 임시파일 → File.Replace/Move 로 원자적.
/// - 외부 편집은 FileSystemWatcher + 300ms 디바운스 → 다시 로드 → UI Dispatcher 에서 SettingsChanged.
/// - 자기 Save 로 생긴 변경은 파일 내용이 마지막 저장 내용과 같으면 무시.
/// - JSON 손상 시 기존 값 유지 + 로그.
/// </summary>
public sealed class SettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한글을 \uXXXX 로 바꾸지 않음
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private const int DebounceMs = 300;

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private string? _lastText; // 마지막으로 저장했거나 로드한 파일 내용
    private bool _disposed;

    public SettingsService()
        : this(AppInfo.DataDirectory)
    {
    }

    /// <summary>테스트용: 다른 폴더의 settings.json 사용.</summary>
    internal SettingsService(string directory)
    {
        _dir = directory;
        SettingsPath = Path.Combine(_dir, "settings.json");
        IconsDirectory = Path.Combine(_dir, "icons");
        Directory.CreateDirectory(_dir);

        Current = LoadInitial();

        _debounce = new Timer(_ => OnDebounced(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(_dir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += (_, e) => Log.Error("settings.json 감시 오류", e.GetException());
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("settings.json FileSystemWatcher 생성 실패", ex);
        }
    }

    public Settings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    public string SettingsPath { get; }

    public string IconsDirectory { get; }

    /// <summary>settings.json 이 이번 실행에서 새로 만들어졌는지 (첫 설치 판단용 — 첫 둘러보기).</summary>
    public bool CreatedThisRun { get; private set; }

    /// <summary>읽으면서 루틴 실행 횟수 이관(0으로)을 했음 — 시작 때 한 번 저장.</summary>
    internal static bool RoutineRunsJustReset { get; set; }

    // ───────────────────────── 로드 ─────────────────────────

    private Settings LoadInitial()
    {
        if (!File.Exists(SettingsPath))
        {
            var s = new Settings { FirstRunTourPending = true, FirstUseHintsPending = true, LastRunVersion = WhatsNew.CurrentText };
            // 새 설치 기본값: 윈도우 작업 표시줄 숨기기 켬 (+ 앱 트레이 아이콘도 같이 켜짐). 기존 사용자 파일은 건드리지 않음.
            // (SetHideWindowsTaskbar 와 같은 효과지만 작업 표시줄 고정 앱 가져오기는 App 의 첫 핀 설정이 맡음)
            s.HideWindowsTaskbar = true;
            s.TopBar.ShowTrayIcons = true;
            s.TopBar.CalendarApp = CalendarApps.DefaultForNewInstall(); // Outlook 이 있으면 Outlook, 없으면 Google 웹 (#21 결정 7)
            Current = s;
            CreatedThisRun = true;
            Save();
            Log.Info($"기본 설정 파일 생성: {SettingsPath}");
            return s;
        }

        try
        {
            string text = ReadAllTextShared(SettingsPath);
            // 새 버전 첫 실행이면 이관·저장 전에 원본 백업 (Services/UpdateBackup)
            UpdateBackup.BeforeLoad(Path.GetDirectoryName(SettingsPath)!, text);
            var s = Deserialize(text, out string repaired, out var fixes);
            _lastText = text;
            bool changed = Migrate(repaired, s);
            if (fixes.Count > 0)
            {
                // 값 몇 개만 틀림 → 그 항목만 기본값, 나머지는 살림. 원본은 .bad-시각 으로 보관하고 고친 내용을 저장
                KeepBadOriginal(text, fixes);
                changed = true;
            }
            if (s.LastRunVersion != WhatsNew.CurrentText)
            {
                s.LastRunVersion = WhatsNew.CurrentText;
                changed = true;
            }
            if (changed)
            {
                Current = s;
                Save();
            }
            return s;
        }
        catch (Exception ex)
        {
            // 손상된 파일은 덮어쓰기 전에 백업해 둔다 (다음 Save 가 덮어쓸 수 있으므로).
            Log.Error("settings.json 파싱 실패 → 기본값으로 시작 (원본은 백업)", ex);
            try
            {
                string backup = SettingsPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(SettingsPath, backup, overwrite: false);
                Log.Info($"손상된 설정 백업: {backup}");
            }
            catch (Exception bex)
            {
                Log.Error("손상된 설정 백업 실패", bex);
            }
            return new Settings();
        }
    }

    // ───────────────────────── 옛 형식 이관 ─────────────────────────

    private const string OldDockBackground = "#B0202024";
    private const string OldDockBorder = "#40FFFFFF";
    private const string OldDockIndicator = "#E0FFFFFF";
    private const string OldTopBarForeground = "#FFF2F2F2";
    private const string OldTopBarBackground = "#C0161618";
    private const double OldTopBarHeight = 32;
    private const double OldTopBarFontSize = 14;

    /// <summary>
    /// 이전 버전 settings.json 을 새 형식으로 이관. 원본 JSON 을 검사한다 (속성 기본값과 헷갈리지 않게).
    /// 키 기반(몇 번이든 안전):
    /// - dock.reserveSpace: true → Mode=Reserve, false → Overlay (dock.mode 키가 이미 있으면 mode 우선)
    /// - dock.mode=Reserve("공간 차지", v0.5 에서 삭제) → Overlay(항상 보이기) — 버전과 상관없이 매번
    /// 파일의 settingsVersion(없으면 0) 이 2 미만일 때만 한 번 (그 뒤 사용자가 같은 값을 골라도 다시 바꾸지 않음):
    /// - dock.background/borderColor/indicatorColor 가 이전 기본값과 정확히 같으면 "" (테마 기본값)
    /// - topBar.foreground 가 "#FFF2F2F2" 면 "", topBar.background 가 "#C0161618" 이면 새 기본값
    /// - topBar.height 32 / fontSize 14 (옛 기본값) 이면 새 기본값 26 / 13
    /// 3 미만일 때 한 번:
    /// - topBar.showTrayIcons 키가 없고 hideWindowsTaskbar=true 면 showTrayIcons=true (옛 기본값 true 유지)
    /// 끝나면 SettingsVersion = 현재 버전. 바뀐 게 있거나 버전을 올렸으면 true (호출자가 저장).
    /// </summary>
    internal static bool Migrate(string text, Settings s)
    {
        var notes = new List<string>();
        int version;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            version = TryGetProp(root, "settingsVersion", out var ver) && ver.ValueKind == JsonValueKind.Number && ver.TryGetInt32(out int v) ? v : 0;

            if (!TryGetProp(root, "dock", out _) && version < 4) s.Dock.Mode = DockMode.Reserve; // dock 항목 자체가 없는 옛 파일도 옛 기본값
            if (TryGetProp(root, "dock", out var dock) && dock.ValueKind == JsonValueKind.Object)
            {
                bool hasMode = TryGetProp(dock, "mode", out _);
                if (TryGetProp(dock, "reserveSpace", out var rs) && rs.ValueKind is JsonValueKind.True or JsonValueKind.False && !hasMode)
                {
                    s.Dock.Mode = rs.GetBoolean() ? DockMode.Reserve : DockMode.Overlay;
                    notes.Add($"dock.reserveSpace={rs.GetBoolean()} → mode={s.Dock.Mode}");
                }
                else if (!hasMode && version < 4)
                {
                    // mode 키 없는 옛 파일 = 옛 기본값(공간 차지) → 아래에서 항상 보이기로 (새 기본값 자동 숨김으로 바뀌지 않게)
                    s.Dock.Mode = DockMode.Reserve;
                }
                if (version < 2 && ResetIfOld(dock, "background", OldDockBackground)) { s.Dock.Background = ""; notes.Add("dock.background → \"\""); }
                if (version < 2 && ResetIfOld(dock, "borderColor", OldDockBorder)) { s.Dock.BorderColor = ""; notes.Add("dock.borderColor → \"\""); }
                if (version < 2 && ResetIfOld(dock, "indicatorColor", OldDockIndicator)) { s.Dock.IndicatorColor = ""; notes.Add("dock.indicatorColor → \"\""); }
            }

            if (version < 2 && TryGetProp(root, "topBar", out var top) && top.ValueKind == JsonValueKind.Object)
            {
                if (ResetIfOld(top, "foreground", OldTopBarForeground)) { s.TopBar.Foreground = ""; notes.Add("topBar.foreground → \"\""); }
                if (ResetIfOld(top, "background", OldTopBarBackground))
                {
                    s.TopBar.Background = new TopBarSettings().Background;
                    notes.Add($"topBar.background → {s.TopBar.Background}");
                }
                // 상단바를 맥 메뉴바 크기로 줄임 (32/14 → 26/13): 옛 기본값 그대로인 경우만, 한 번만
                if (NumberIs(top, "height", OldTopBarHeight)) { s.TopBar.Height = new TopBarSettings().Height; notes.Add($"topBar.height → {s.TopBar.Height}"); }
                if (NumberIs(top, "fontSize", OldTopBarFontSize)) { s.TopBar.FontSize = new TopBarSettings().FontSize; notes.Add($"topBar.fontSize → {s.TopBar.FontSize}"); }
            }

            // 문제 신고용 고정 PC 번호(reportClientId, v0.5 에서 없앰 — 연락처와 이어지면 같은 번호로 오는 것을 사람에게 이을 수 있음):
            // 모델에 없는 키라 다음 저장에서 빠짐 → 있으면 바로 저장하게 이관으로 표시
            if (TryGetProp(root, "reportClientId", out _)) notes.Add("reportClientId 삭제 (고정 PC 번호 안 씀)");

            // 5: 사용 통계는 동의를 받고서만 (#d20) — 이 버전 전 파일은 켜져 있었어도 다시 물음
            if (version < 5 && s.SendUsageStats is not null)
            {
                s.SendUsageStats = null;
                notes.Add("sendUsageStats → 묻기 전 (동의 받고 보냄)");
            }

            // 5: 독 핀 이름 "Finder"(파일 탐색기)·"Launchpad"(앱 모음) → 새 이름 (#d19). 이름이 정확히 같고 대상이 맞는 것만 — 사용자가 바꾼 이름은 그대로
            if (version < 5)
            {
                foreach (var pin in s.Pins)
                {
                    if (pin.Name == "Finder" && pin.Kind == PinKind.Exe
                        && Path.GetFileName(pin.Target).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        pin.Name = DefaultPins.ExplorerName;
                        notes.Add($"핀 이름 Finder → {pin.Name}");
                    }
                    else if (pin.Name == "Launchpad" && pin.Kind == PinKind.Special
                             && pin.Target.Equals("launchpad", StringComparison.OrdinalIgnoreCase))
                    {
                        pin.Name = DefaultPins.AllAppsName;
                        notes.Add($"핀 이름 Launchpad → {pin.Name}");
                    }
                }
            }

            // 3: 앱 트레이 아이콘 기본값 true → false. 키 없이(옛 기본값으로) 작업 표시줄을 숨기던 사용자는 지금처럼 켜 둔다.
            if (version < 3)
            {
                bool hasShowTray = TryGetProp(root, "topBar", out var top3) && top3.ValueKind == JsonValueKind.Object
                                   && TryGetProp(top3, "showTrayIcons", out _);
                if (!hasShowTray && s.HideWindowsTaskbar && !s.TopBar.ShowTrayIcons)
                {
                    s.TopBar.ShowTrayIcons = true;
                    notes.Add("topBar.showTrayIcons → true (작업 표시줄 숨김 사용 중)");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("설정 이관 검사 실패", ex);
            return false;
        }
        // 4: 독 "공간 차지"(Reserve) 삭제 → 항상 보이기. 버전과 상관없이 (settings.json 을 직접 고쳐 Reserve 로 둔 경우도)
        if (s.Dock.Mode == DockMode.Reserve)
        {
            s.Dock.Mode = DockMode.Overlay;
            notes.Add("dock.mode Reserve(공간 차지, 삭제됨) → Overlay(항상 보이기)");
        }
        if (version < Settings.CurrentVersion)
        {
            s.SettingsVersion = Settings.CurrentVersion;
            notes.Add($"settingsVersion {version} → {Settings.CurrentVersion}");
        }
        else
        {
            s.SettingsVersion = version; // 더 새 버전이 쓴 파일이면 그대로 둠
        }
        if (notes.Count > 0) Log.Info("설정 이관: " + string.Join(", ", notes));
        return notes.Count > 0;
    }

    private static bool ResetIfOld(JsonElement obj, string name, string oldValue) =>
        TryGetProp(obj, name, out var v) && v.ValueKind == JsonValueKind.String &&
        string.Equals(v.GetString(), oldValue, StringComparison.Ordinal);

    private static bool NumberIs(JsonElement obj, string name, double oldValue) =>
        TryGetProp(obj, name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() == oldValue;

    /// <summary>대소문자 무시 속성 찾기 (camelCase/PascalCase 모두).</summary>
    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>
    /// 너그러운 읽기: 값 하나가 틀려(모르는 enum 이름, 숫자 자리에 글자, 타입 다름) 전체 읽기가 실패하면
    /// 예외가 가리키는 그 속성만 지우고(→ 그 항목은 기본값) 다시 읽는다. 지운 경로는 fixes 로, 고친 JSON 은 repaired 로.
    /// JSON 자체가 깨졌거나(잘림·문법 오류) 최상위가 객체가 아니면 예외 → 호출한 쪽이 지금처럼 백업 후 기본값.
    /// </summary>
    internal static Settings Deserialize(string text, out string repaired, out List<string> fixes)
    {
        fixes = new List<string>();
        repaired = text;
        Settings? parsed = null;
        try
        {
            parsed = JsonSerializer.Deserialize<Settings>(text, JsonOptions);
        }
        catch (JsonException first)
        {
            // 문법 오류(잘린 파일 등)면 JsonNode.Parse 가 던짐 → 파일 전체 손상으로
            if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                is not JsonObject root) throw;
            JsonException? ex = first;
            for (int i = 0; i < 100 && ex is not null; i++)
            {
                if (ex.Path is not { Length: > 1 } path || !RemoveAt(root, path)) throw ex;
                fixes.Add(path[2..]); // "$." 떼고
                repaired = root.ToJsonString();
                try { parsed = JsonSerializer.Deserialize<Settings>(repaired, JsonOptions); ex = null; }
                catch (JsonException next) { ex = next; }
            }
            if (ex is not null) throw ex;
        }
        var s = parsed ?? throw new JsonException("settings.json 이 null 입니다.");
        // 숫자로 적힌 모르는 핀 종류도 그 핀만 빠짐 (기본값 Exe 로 바꾸지 않음)
        // 루틴: 대상 없는 항목은 버림(빈 루틴은 그대로 — 나중에 채울 수 있음), id 채움(겹치면 새로), 최대 12개·항목 15개
        s.Routines ??= new List<RoutineDef>();
        s.RoutineRestores ??= new List<RoutineRestore>();
        s.RoutineRestores.RemoveAll(r => r is null);
        s.RoutineAsked ??= new Dictionary<string, string>();
        foreach (var r in s.Routines.Where(r => r?.More is not null))
        {
            var m = r.More!;
            m.Start ??= new List<RoutineStart>();
            m.Start.RemoveAll(x => x is null || x.Kind is null); // 모르는 종류는 그 조건만 버림 ("컴퓨터를 켜면"으로 바뀌지 않게)
            foreach (var st in m.Start) st.Time = NormalizeTime(st.Time); // "9:00" → "09:00" (분 비교가 문자열)
            if (m.End is { } e0) e0.Time = NormalizeTime(e0.Time);
            m.End ??= new RoutineEnd();
            m.Change ??= new RoutineChange();
            if (m.Change.Volume is int v) m.Change.Volume = Math.Clamp(v, 0, 100);
            m.DndExceptions ??= new List<string>();
            m.DndExceptions.RemoveAll(string.IsNullOrWhiteSpace);
        }
        // 이관 (한 번): 옛 빌드가 다시 누를 때마다 센 루틴 실행 횟수는 버림
        if (!s.RoutineRunsReset)
        {
            s.RoutineRunsSinceSignal = 0;
            s.RoutineRunsReset = true;
            RoutineRunsJustReset = true; // 시작 때 한 번 저장 (파일에도 바로 남게)
        }
        var routineIds = new HashSet<string>();
        s.Routines.RemoveAll(r =>
        {
            if (r is null) return true;
            r.Items ??= new List<RoutineItem>();
            r.Items.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Target) && string.IsNullOrWhiteSpace(i.Aumid));
            if (r.Items.Count > RoutineDef.MaxItems) r.Items.RemoveRange(RoutineDef.MaxItems, r.Items.Count - RoutineDef.MaxItems);
            r.Desktop = new RoutineDesktop(); // 데스크톱은 고르지 않음 — 옛 값(지금/N번)은 무시하고 늘 새 데스크톱
            r.Name ??= "";
            if (string.IsNullOrWhiteSpace(r.Id) || !routineIds.Add(r.Id)) { r.Id = Guid.NewGuid().ToString("N"); routineIds.Add(r.Id); }
            return false;
        });
        if (s.Pins is { } pins)
        {
            for (int pi = pins.Count - 1; pi >= 0; pi--)
            {
                var pin = pins[pi];
                // 옛 모양(핀 안에 루틴 내용) → 루틴 목록으로 옮기고 핀은 Id 만
                if (pin?.Kind == PinKind.Routine && pin.Routine is { } legacy)
                {
                    legacy.Items?.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Target) && string.IsNullOrWhiteSpace(i.Aumid));
                    if (legacy.Items is { Count: > 0 })
                    {
                        string id = !string.IsNullOrWhiteSpace(pin.Id) && !routineIds.Contains(pin.Id) ? pin.Id : Guid.NewGuid().ToString("N");
                        routineIds.Add(id);
                        s.Routines.Add(new RoutineDef { Id = id, Name = pin.Name, Icon = pin.Icon, Desktop = legacy.Desktop ?? new RoutineDesktop(), Items = legacy.Items });
                        pin.Target = id;
                    }
                    pin.Routine = null;
                }
                bool bad = pin is null || !Enum.IsDefined(pin.Kind)
                           // 루틴 핀은 있는 루틴을 가리켜야, 폴더는 경로가 있어야
                           || pin.Kind == PinKind.Routine && !routineIds.Contains(pin.Target ?? "")
                           || pin.Kind == PinKind.Folder && string.IsNullOrWhiteSpace(pin.Target);
                if (bad) { pins.RemoveAt(pi); fixes.Add($"pins[{pi}]"); continue; }
                if (pin!.Kind == PinKind.Folder) (pin.Folder ??= new FolderOptions()).LastOpened ??= DateTime.UtcNow;
                if (pin.Kind == PinKind.Folder) pin.Id ??= Guid.NewGuid().ToString("N");
            }
            KeepOneTrash(pins);
        }
        FixUndefinedEnums(s, "", fixes); // 숫자로 적은 없는 enum 값(예 "colorMode": 7)은 예외 없이 들어오므로 따로
        // 수동 편집으로 null 이 들어와도 UI 가 죽지 않게 보정.
        s.Dock ??= new DockSettings();
        s.TopBar ??= new TopBarSettings();
        s.TopBar.TrayIconPlacement ??= new Dictionary<string, TrayIconPlacement>();
        foreach (var k in s.TopBar.TrayIconPlacement.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            s.TopBar.TrayIconPlacement.Remove(k);
        s.Notifications ??= new NotificationSettings();
        s.Search ??= new SearchSettings();
        s.Search.FileSearchFolders ??= new List<string>();
        s.Search.FileSearchFolders.RemoveAll(string.IsNullOrWhiteSpace);
        s.Search.MaxPerCategory = Math.Clamp(s.Search.MaxPerCategory, 3, 10);
        s.FontFamily ??= "Pretendard";
        s.AppMenus ??= new Dictionary<string, List<AppMenuDef>>();
        s.SeenHints ??= new List<string>();
        s.NewSince ??= new Dictionary<string, DateTime>();
        s.NewSeen ??= new List<string>();
        if (s.Routines.Count > RoutineDef.MaxRoutines)
        {
            var keep = s.Routines.Take(RoutineDef.MaxRoutines).Select(r => r.Id).ToHashSet();
            s.Routines.RemoveRange(RoutineDef.MaxRoutines, s.Routines.Count - RoutineDef.MaxRoutines);
            s.Pins?.RemoveAll(p => p.Kind == PinKind.Routine && !keep.Contains(p.Target));
        }
        s.AllApps ??= new AllAppsSettings();
        s.AllApps.Favorites ??= new List<string>();
        s.AllApps.Groups ??= new List<AppGroupDef>();
        s.AllApps.Groups.RemoveAll(g => g is null || string.IsNullOrWhiteSpace(g.Id));
        foreach (var g in s.AllApps.Groups)
        {
            g.Apps?.RemoveAll(k => string.IsNullOrWhiteSpace(k));
            g.AutoApps?.RemoveAll(k => string.IsNullOrWhiteSpace(k));
        }
        s.AllApps.Overrides ??= new Dictionary<string, string>();
        s.AllApps.Hidden ??= new List<string>();
        s.AllApps.DismissedSuggestions ??= new Dictionary<string, DateTime>();
        s.Pins ??= new List<PinItem>();
        s.Pins.RemoveAll(p => p is null);
        foreach (var p in s.Pins)
        {
            p.Name ??= "";
            p.Target ??= "";
        }
        return s;
    }

    /// <summary>"9:0"·"9:00" 같은 시각 → "HH:mm". 못 읽으면 null.</summary>
    private static string? NormalizeTime(string? t) =>
        t is not null && TimeSpan.TryParse(t.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= TimeSpan.Zero && v < TimeSpan.FromDays(1)
            ? $"{v.Hours:00}:{v.Minutes:00}" : null;

    private static string ReadAllTextShared(string path)
    {
        // 편집기가 쓰는 중일 수 있으므로 몇 번 재시도.
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    // ───────────────────────── 저장 ─────────────────────────

    /// <summary>원자적으로 저장하고 SettingsChanged 를 UI 스레드에서 발생 (자기 저장으로 인한 파일 변경은 다시 로드하지 않음).</summary>
    public void Save()
    {
        bool saved = false;
        ImportTaskbarPinsIfRequested();
        lock (_gate)
        {
            string text = JsonSerializer.Serialize(Current, JsonOptions);
            string tmp = SettingsPath + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _lastText = text; // watcher 이벤트보다 먼저 기록 → 자기 저장 무시
                if (File.Exists(SettingsPath))
                {
                    try
                    {
                        File.Replace(tmp, SettingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    catch (IOException)
                    {
                        // 대상이 다른 프로세스에 열려 있는 경우 등 → 덮어쓰기 이동으로 대체.
                        File.Move(tmp, SettingsPath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tmp, SettingsPath);
                }
                saved = true;
            }
            catch (Exception ex)
            {
                Log.Error("settings.json 저장 실패", ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 무시 */ }
            }
        }

        // 독 ↔ 상단바처럼 UI 안에서 바꾼 설정도 다른 창에 반영되게 알림.
        if (saved) RunOnUi(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>"작업 표시줄 숨기기" 를 처음 켰으면(Settings.SetHideWindowsTaskbar) 작업 표시줄 고정 앱 중 독에 없는 것을 끝에 추가.</summary>
    private void ImportTaskbarPinsIfRequested()
    {
        var s = Current;
        if (!s.TaskbarPinImportRequested) return;
        s.TaskbarPinImportRequested = false;
        if (s.TaskbarPinsImported) return;
        try
        {
            TaskbarPins.AddMissingTo(s, this);
        }
        catch (Exception ex)
        {
            Log.Error("작업 표시줄 고정 앱 가져오기 실패", ex);
        }
        s.TaskbarPinsImported = true;
    }

    // ───────────────────────── 감시 ─────────────────────────

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (e is RenamedEventArgs r && !string.Equals(r.Name, "settings.json", StringComparison.OrdinalIgnoreCase))
            return;
        if (_disposed) return;
        _debounce.Change(DebounceMs, Timeout.Infinite);
    }

    private void OnDebounced()
    {
        if (_disposed) return;
        Settings? loaded;
        lock (_gate)
        {
            string text;
            try
            {
                if (!File.Exists(SettingsPath)) return;
                text = ReadAllTextShared(SettingsPath);
            }
            catch (Exception ex)
            {
                Log.Error("settings.json 다시 읽기 실패", ex);
                return;
            }

            if (text == _lastText) return; // 자기 Save 이거나 내용 변화 없음

            try
            {
                loaded = Deserialize(text, out _, out var fixes);
                if (fixes.Count > 0) Log.Warn($"외부에서 편집된 settings.json 에 잘못된 값 {fixes.Count}개 → 그 항목만 기본값: {string.Join(", ", fixes)} (파일은 그대로)");
                // 직접 편집으로 삭제된 "공간 차지"를 고른 경우 → 항상 보이기 (저장은 하지 않음 — 파일은 사용자 것)
                if (loaded.Dock.Mode == DockMode.Reserve) loaded.Dock.Mode = DockMode.Overlay;
            }
            catch (Exception ex)
            {
                Log.Error("외부에서 편집된 settings.json 파싱 실패 → 기존 값 유지", ex);
                _lastText = text; // 같은 손상 내용으로 반복 로그 방지
                return;
            }
            _lastText = text;
        }

        Log.Info("settings.json 외부 변경 감지 → 다시 로드");
        RunOnUi(() =>
        {
            // UI 가 잡고 있는 참조가 유효하도록 Current / Dock / TopBar / Pins 객체는 그대로 두고 값만 복사.
            lock (_gate) CopyInto(loaded, Current);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>독 휴지통(Special "recyclebin")은 하나만 (처음 것의 자리 그대로 — 다른 핀처럼 어디든 옮길 수 있음).</summary>
    public static void KeepOneTrash(List<PinItem> pins)
    {
        int first = pins.FindIndex(IsTrashPin);
        if (first < 0) return;
        for (int i = pins.Count - 1; i > first; i--)
            if (IsTrashPin(pins[i])) pins.RemoveAt(i);
    }

    private static bool IsTrashPin(PinItem? p) =>
        p is { Kind: PinKind.Special } && string.Equals(p.Target, DefaultPins.RecycleBinTarget, StringComparison.OrdinalIgnoreCase);

    /// <summary>값만 틀린 원본을 settings.json.bad-시각 으로 보관 (고친 내용으로 덮기 전에).</summary>
    private void KeepBadOriginal(string text, List<string> fixes)
    {
        string bad = SettingsPath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try { File.WriteAllText(bad, text); }
        catch (Exception ex) { Log.Warn($"잘못된 값 원본 보관 실패: {ex.Message}"); }
        Log.Warn($"settings.json 잘못된 값 {fixes.Count}개 → 그 항목만 기본값, 나머지는 그대로: {string.Join(", ", fixes)} (원본 {Path.GetFileName(bad)})");
    }

    /// <summary>
    /// System.Text.Json 예외 경로("$.topBar.colorMode", "$.pins[3].kind", "$.appMenus['a.exe'][0].title")가 가리키는 값을 지움.
    /// 배열 원소 자체가 틀렸으면 그 원소를 지움. 못 찾으면 false.
    /// </summary>
    private static bool RemoveAt(JsonObject root, string path)
    {
        var parts = new List<object>(); // string = 속성, int = 배열 위치
        int i = 1; // "$" 다음
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int end = i + 1;
                while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
                parts.Add(path[(i + 1)..end]);
                i = end;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                int end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                parts.Add(path[(i + 2)..end]);
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0 || !int.TryParse(path[(i + 1)..end], out int n)) return false;
                parts.Add(n);
                i = end + 1;
            }
            else return false;
        }
        if (parts.Count == 0) return false;
        // 독 핀(pins[n]) 안의 값이 틀리면 그 값만 지우지 않고 핀 하나를 통째로 건너뜀 —
        // 모르는 종류(kind, 예: 더 새 몽독의 루틴·폴더)가 기본값 Exe 로 바뀌어 엉뚱한 핀이 되지 않게
        if (parts.Count > 2 && parts[0] is string top && top.Equals("pins", StringComparison.OrdinalIgnoreCase) && parts[1] is int)
            parts.RemoveRange(2, parts.Count - 2);
        JsonNode? node = root;
        for (int k = 0; k < parts.Count - 1; k++)
        {
            node = parts[k] switch
            {
                string name when node is JsonObject o => FindProperty(o, name),
                int idx when node is JsonArray a && idx < a.Count => a[idx],
                _ => null,
            };
            if (node is null) return false;
        }
        switch (parts[^1])
        {
            case string name when node is JsonObject o:
                string? key = o.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                return key is not null && o.Remove(key);
            case int idx when node is JsonArray a && idx < a.Count:
                a.RemoveAt(idx);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 설정 객체(Models 의 클래스·그 리스트)를 돌며 정의되지 않은 enum 값을 그 클래스의 기본값으로 (새 인스턴스의 값).
    /// [Flags] enum 은 건너뜀.
    /// </summary>
    private static void FixUndefinedEnums(object obj, string prefix, List<string> fixes, int depth = 0)
    {
        if (depth > 6) return;
        var type = obj.GetType();
        object? defaults = null;
        foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            var pt = p.PropertyType;
            if (pt.IsEnum && p.CanWrite && !pt.IsDefined(typeof(FlagsAttribute), false))
            {
                object? v = p.GetValue(obj);
                if (v is not null && !Enum.IsDefined(pt, v))
                {
                    try { defaults ??= Activator.CreateInstance(type); } catch { /* 기본 생성자 없음 → enum 기본값 */ }
                    p.SetValue(obj, defaults is null ? Activator.CreateInstance(pt) : p.GetValue(defaults));
                    fixes.Add(prefix + JsonNamingPolicy.CamelCase.ConvertName(p.Name));
                }
            }
            else if (pt.Namespace == typeof(Settings).Namespace && pt.IsClass && p.GetValue(obj) is { } child)
            {
                FixUndefinedEnums(child, prefix + JsonNamingPolicy.CamelCase.ConvertName(p.Name) + ".", fixes, depth + 1);
            }
            else if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(List<>)
                     && pt.GetGenericArguments()[0] is { IsClass: true } et && et.Namespace == typeof(Settings).Namespace
                     && p.GetValue(obj) is System.Collections.IList list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is { } item)
                        FixUndefinedEnums(item, $"{prefix}{JsonNamingPolicy.CamelCase.ConvertName(p.Name)}[{i}].", fixes, depth + 1);
            }
        }
    }

    private static JsonNode? FindProperty(JsonObject o, string name) =>
        o.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    // ───────────────────────── 설정 옮기기 (Services/SettingsTransfer) ─────────────────────────

    /// <summary>내보내기용 지금 설정 JSON (저장 파일과 같은 형식).</summary>
    public string ExportJson()
    {
        lock (_gate) return JsonSerializer.Serialize(Current, JsonOptions);
    }

    /// <summary>가져온 settings.json 을 읽고 이관까지 거침 (지금 설정은 바꾸지 않음). 손상이면 예외.</summary>
    public static Settings ParseForImport(string json)
    {
        var s = Deserialize(json, out string repaired, out var fixes);
        if (fixes.Count > 0) Log.Warn($"가져온 설정에 잘못된 값 {fixes.Count}개 → 그 항목만 기본값: {string.Join(", ", fixes)}");
        Migrate(repaired, s);
        return s;
    }

    /// <summary>
    /// 가져온 설정 적용: 이 PC 에만 맞는 값(자동 실행 등록, 작업 표시줄 숨기기, 윈도우 알림 소리, 화면 언어, 사용 통계, 실행·안내 기록)은 지금 것을 유지하고
    /// 나머지를 값만 복사(참조 유지) → 저장 → SettingsChanged 로 독·상단바에 바로 반영.
    /// </summary>
    public void ApplyImported(Settings imported)
    {
        var cur = Current;
        imported.StartWithWindows = cur.StartWithWindows;
        imported.Notifications.Sound = cur.Notifications.Sound;
        imported.Notifications.OriginalSound = cur.Notifications.OriginalSound;
        imported.LastRunVersion = cur.LastRunVersion;
        imported.LastSeenVersion = cur.LastSeenVersion;
        imported.FirstRunTourPending = cur.FirstRunTourPending;
        imported.StartupPromptPending = cur.StartupPromptPending;
        imported.NotifiedUpdateVersion = cur.NotifiedUpdateVersion;
        imported.SkippedUpdateVersion = cur.SkippedUpdateVersion;
        imported.TaskbarPinsImported = cur.TaskbarPinsImported;
        imported.TaskbarPinImportRequested = false;
        imported.ImportedFromMyDockFinder = cur.ImportedFromMyDockFinder;
        // 이 PC 사람이 정한 것: 사용 통계 동의(가져온 파일이 옛 버전이면 키가 없어 기본 true 로 되살아남), 화면 언어(다시 시작해야 반영),
        // 크래시 안내 끔, 처음 쓰기 힌트 기록
        imported.SendUsageStats = cur.SendUsageStats;
        imported.StatsAskDay = cur.StatsAskDay;
        imported.NewSince = cur.NewSince; // NEW 배지 기록은 이 PC 것
        imported.NewSeen = cur.NewSeen;
        imported.AllApps.DismissedSuggestions = cur.AllApps.DismissedSuggestions; // 추천 거절도 이 PC 것
        imported.RoutineRestores = cur.RoutineRestores; // 루틴이 바꾼 소리 설정의 되돌릴 값·물은 날도
        imported.RoutineAsked = cur.RoutineAsked;
        // 실행 기록에서 나온 것(자동 폴더 내용, 씨앗·갱신·정리 띠 날짜)도 이 PC 것 — 다른 PC 의 날짜가 오면 독 핀 유예가 꺼지거나 씨앗을 안 읽음
        imported.AllApps.UsageSeededAt = cur.AllApps.UsageSeededAt;
        imported.AllApps.CleanupPromptMonth = cur.AllApps.CleanupPromptMonth;
        imported.AllApps.CleanupUsed = cur.AllApps.CleanupUsed;
        imported.AllApps.AutoFoldersDay = null; // 가져온 폴더 기준으로 다음에 다시 채움
        foreach (var g in imported.AllApps.Groups)
            g.AutoApps = g.Touched ? null : cur.AllApps.Groups.FirstOrDefault(c => c.Id == g.Id && !c.Touched)?.AutoApps;
        imported.StatsAskCount = cur.StatsAskCount;
        // 작업 표시줄 숨기기도 이 PC 의 윈도우를 바꾸는 설정 → 그대로 (숨긴 채면 트레이 아이콘을 볼 곳이 상단바뿐이라 그것도 유지)
        imported.HideWindowsTaskbar = cur.HideWindowsTaskbar;
        imported.TaskbarOverlapAsked = cur.TaskbarOverlapAsked; // 이 PC 작업 표시줄 안내
        if (cur.HideWindowsTaskbar && cur.TopBar.ShowTrayIcons) imported.TopBar.ShowTrayIcons = true;
        imported.Language = cur.Language;
        imported.CrashPromptDisabled = cur.CrashPromptDisabled;
        imported.FirstUseHintsPending = cur.FirstUseHintsPending;
        imported.SeenHints = cur.SeenHints.ToList();
        imported.SettingsVersion = Settings.CurrentVersion;
        lock (_gate) CopyInto(imported, cur);
        Save();
    }

    /// <summary>
    /// 외부 편집으로 다시 읽은 값을 현재 객체에 반영. 하위 설정 객체(Dock/TopBar/Notifications/Search)와 Pins 리스트는
    /// 참조를 유지한 채 값만 복사하고, 나머지 최상위 속성(FontFamily, HideWindowsTaskbar, AppMenus, 앞으로 추가될 것 포함)은 그대로 대입.
    /// 빠진 속성이 있으면 다음 Save 가 외부 편집을 되돌리므로 리플렉션으로 전부 다룬다.
    /// </summary>
    private static void CopyInto(Settings src, Settings dst)
    {
        CopyProperties(src.Dock, dst.Dock);
        CopyProperties(src.TopBar, dst.TopBar);
        CopyProperties(src.Notifications, dst.Notifications);
        CopyProperties(src.Search, dst.Search);
        dst.Pins.Clear();
        dst.Pins.AddRange(src.Pins);
        foreach (var p in typeof(Settings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length != 0) continue;
            if (p.Name is nameof(Settings.Dock) or nameof(Settings.TopBar) or nameof(Settings.Notifications) or nameof(Settings.Search) or nameof(Settings.Pins)) continue;
            p.SetValue(dst, p.GetValue(src));
        }
    }

    /// <summary>public 읽기/쓰기 속성을 얕게 복사 (DockSettings/TopBarSettings 는 값 타입·문자열 속성만 가짐).</summary>
    private static void CopyProperties<T>(T src, T dst) where T : class
    {
        foreach (var p in typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                p.SetValue(dst, p.GetValue(src));
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SafeInvoke(action);
        }
        else
        {
            dispatcher.InvokeAsync(() => SafeInvoke(action));
        }
    }

    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("SettingsChanged 처리 중 예외", ex); }
    }

    // ───────────────────────── 아이콘 ─────────────────────────

    /// <summary>
    /// 원본 이미지를 %APPDATA%\mongdock\icons\ 로 복사하고 복사본 경로를 반환.
    /// 이미 icons 폴더 안이면 그대로 반환. 같은 이름의 다른 파일이 있으면 "이름-2.png" 처럼 고유 이름 사용,
    /// 같은 내용의 파일이 이미 있으면 그 경로를 재사용. 원본이 없으면 FileNotFoundException.
    /// </summary>
    public string ImportIcon(string sourcePath) => ImportIcon(sourcePath, null);

    public string ImportIcon(string sourcePath, string? subfolder)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("경로가 비어 있습니다.", nameof(sourcePath));
        string src = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourcePath.Trim().Trim('"')));
        string root = Path.GetFullPath(IconsDirectory);
        string iconsDir = subfolder is { Length: > 0 } sub ? Path.Combine(root, sub) : root;

        if (src.StartsWith(iconsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(src))
            return src;
        if (!File.Exists(src)) throw new FileNotFoundException("아이콘 파일이 없습니다.", src);

        Directory.CreateDirectory(iconsDir);
        string name = Path.GetFileNameWithoutExtension(src);
        string ext = Path.GetExtension(src);
        string dest = Path.Combine(iconsDir, name + ext);
        for (int i = 2; File.Exists(dest); i++)
        {
            if (SameContent(src, dest)) return dest;
            dest = Path.Combine(iconsDir, $"{name}-{i}{ext}");
        }
        File.Copy(src, dest);
        Log.Info($"아이콘 복사: {src} → {dest}");
        return dest;
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
