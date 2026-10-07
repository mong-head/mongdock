using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using MyDock.Models;

namespace MyDock.Services;

/// <summary>
/// %APPDATA%\MyDock\settings.json 로드/저장 + 외부 편집 감지.
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

    // ───────────────────────── 로드 ─────────────────────────

    private Settings LoadInitial()
    {
        if (!File.Exists(SettingsPath))
        {
            var s = new Settings();
            Current = s;
            Save();
            Log.Info($"기본 설정 파일 생성: {SettingsPath}");
            return s;
        }

        try
        {
            string text = ReadAllTextShared(SettingsPath);
            var s = Deserialize(text);
            _lastText = text;
            if (Migrate(text, s))
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
    /// 파일의 settingsVersion(없으면 0) 이 2 미만일 때만 한 번 (그 뒤 사용자가 같은 값을 골라도 다시 바꾸지 않음):
    /// - dock.background/borderColor/indicatorColor 가 이전 기본값과 정확히 같으면 "" (테마 기본값)
    /// - topBar.foreground 가 "#FFF2F2F2" 면 "", topBar.background 가 "#C0161618" 이면 새 기본값
    /// - topBar.height 32 / fontSize 14 (옛 기본값) 이면 새 기본값 26 / 13
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

            if (TryGetProp(root, "dock", out var dock) && dock.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProp(dock, "reserveSpace", out var rs) && rs.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && !TryGetProp(dock, "mode", out _))
                {
                    s.Dock.Mode = rs.GetBoolean() ? DockMode.Reserve : DockMode.Overlay;
                    notes.Add($"dock.reserveSpace={rs.GetBoolean()} → mode={s.Dock.Mode}");
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
        }
        catch (Exception ex)
        {
            Log.Error("설정 이관 검사 실패", ex);
            return false;
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

    private static Settings Deserialize(string text)
    {
        var s = JsonSerializer.Deserialize<Settings>(text, JsonOptions)
                ?? throw new JsonException("settings.json 이 null 입니다.");
        // 수동 편집으로 null 이 들어와도 UI 가 죽지 않게 보정.
        s.Dock ??= new DockSettings();
        s.TopBar ??= new TopBarSettings();
        s.Notifications ??= new NotificationSettings();
        s.Search ??= new SearchSettings();
        s.Search.FileSearchFolders ??= new List<string>();
        s.Search.FileSearchFolders.RemoveAll(string.IsNullOrWhiteSpace);
        s.Search.MaxPerCategory = Math.Clamp(s.Search.MaxPerCategory, 3, 10);
        s.FontFamily ??= "Pretendard";
        s.AppMenus ??= new Dictionary<string, List<AppMenuDef>>();
        s.Pins ??= new List<PinItem>();
        s.Pins.RemoveAll(p => p is null);
        foreach (var p in s.Pins)
        {
            p.Name ??= "";
            p.Target ??= "";
        }
        return s;
    }

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
                loaded = Deserialize(text);
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
    /// 원본 이미지를 %APPDATA%\MyDock\icons\ 로 복사하고 복사본 경로를 반환.
    /// 이미 icons 폴더 안이면 그대로 반환. 같은 이름의 다른 파일이 있으면 "이름-2.png" 처럼 고유 이름 사용,
    /// 같은 내용의 파일이 이미 있으면 그 경로를 재사용. 원본이 없으면 FileNotFoundException.
    /// </summary>
    public string ImportIcon(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("경로가 비어 있습니다.", nameof(sourcePath));
        string src = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourcePath.Trim().Trim('"')));
        string iconsDir = Path.GetFullPath(IconsDirectory);

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
