using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 윈도우 알림 DB(wpndatabase.db, SQLite WAL) 를 읽어 알림 목록·새 알림 이벤트를 제공.
///
/// 스키마 (Win11 26200 에서 확인):
///   Notification([Order] INTEGER PK 증가, Id UNIQUE, HandlerId → NotificationHandler.RecordId, Type 'toast'|'tile'|'badge',
///                Payload BLOB(XML), Tag, [Group], ExpiryTime/ArrivalTime INT64 FILETIME(UTC), PayloadType 'Xml', ...)
///   NotificationHandler(RecordId PK, PrimaryId = AUMID, HandlerType 'app:immersive'|'app:desktop'|'app:system', ...)
///   TransientTable(NotificationId → Notification.Id, SuppressPopup, ...)
///
/// 읽기: System32\winsqlite3.dll 을 SQLITE_OPEN_READONLY 로 (WAL 내용까지 보임, 체크포인트 안 함 — DB 를 바꾸지 않음).
///   열기/조회가 실패하면 db·-wal·-shm 을 %TEMP%\mongdock-wpn 으로 복사해서 읽는 방식으로 전환.
/// 감지: 알림 폴더 FileSystemWatcher(wpndatabase.db*) → 400ms 디바운스, 보조로 4초 폴링(가벼운 max(Order)/count 조회 후 바뀌었을 때만 전체 읽기).
/// 새 알림: 첫 읽기의 알림은 모두 "본 것" — 그 뒤 처음 보는 Id 이고 도착 시간이 시작 시점 이후면 Arrived.
/// 사생활: 알림 제목·본문은 로그에 남기지 않음 (개수·AUMID 만).
///
/// 윈도우 기본 토스트 팝업 숨기기는 NativeToastSuppressor (팝업 창을 화면 밖으로 옮김, 알림 기록은 그대로).
/// </summary>
public sealed class NotificationService : INotificationService, IDisposable
{
    private const int MaxRows = 300;
    private const int PollMs = 4000;
    private const int DebounceMs = 400;
    /// <summary>같은 앱·같은 Tag/Group 의 토스트가 이 시간 안에 다시 오면(진행률 갱신 등) 배너를 다시 띄우지 않음.</summary>
    private static readonly TimeSpan SameTagQuiet = TimeSpan.FromSeconds(20);

    private static readonly string DbFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");
    private static readonly string DbPath = Path.Combine(DbFolder, "wpndatabase.db");
    private static readonly string HiddenPath = Path.Combine(AppInfo.DataDirectory, "notifications-hidden.json");

    private readonly IWindowTracker _tracker;
    private readonly IAppLauncher _launcher;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _seen = new();
    private readonly HashSet<long> _hidden = new();
    private readonly Dictionary<string, DateTime> _lastBannerByTag = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Timer? _poll;
    private DateTime _startedUtc;
    private bool _running;
    private bool _baselineDone;
    private bool _useCopy;
    private int _reading;          // 0/1 — 동시에 한 번만 읽음
    private volatile bool _readAgain;     // 읽는 중에 또 변경 → 끝나고 한 번 더
    private (long MaxOrder, long Count) _lastStamp = (-1, -1);
    private List<Row> _rows = new();
    private IReadOnlyList<NotificationItem> _recent = Array.Empty<NotificationItem>();
    private bool _loggedFailure;

    public NotificationService(IWindowTracker tracker, IAppLauncher launcher)
    {
        _tracker = tracker;
        _launcher = launcher;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public bool IsAvailable { get; private set; }
    public IReadOnlyList<NotificationItem> Recent => _recent;
    public event EventHandler? Changed;
    public event EventHandler<NotificationItem>? Arrived;

    // ───────────────────────── 시작/정지 ─────────────────────────

    public void Start()
    {
        if (_running) return;
        _running = true;
        _startedUtc = DateTime.UtcNow;
        LoadHidden();
        try
        {
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll")))
            {
                Log.Warn("알림: winsqlite3.dll 없음 → 알림 기능 끔");
                return;
            }
            if (!File.Exists(DbPath))
            {
                Log.Warn("알림: wpndatabase.db 없음 → 알림 기능 끔");
                return;
            }
            IsAvailable = true;
            _debounce = new Timer(_ => RequestRead(force: false), null, Timeout.Infinite, Timeout.Infinite);
            _poll = new Timer(_ => RequestRead(force: false), null, PollMs, PollMs);
            try
            {
                _watcher = new FileSystemWatcher(DbFolder, "wpndatabase.db*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Error += (_, e) => Log.Warn($"알림 폴더 감시 오류 (폴링으로 계속): {e.GetException().Message}");
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Warn($"알림 폴더 감시 실패 (폴링만 사용): {ex.Message}");
            }
            RequestRead(force: true);
        }
        catch (Exception ex)
        {
            Log.Error("알림 서비스 시작 실패", ex);
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileChanged;
                _watcher.Created -= OnFileChanged;
                _watcher.Dispose();
                _watcher = null;
            }
            _debounce?.Dispose();
            _debounce = null;
            _poll?.Dispose();
            _poll = null;
        }
        catch (Exception ex)
        {
            Log.Warn($"알림 서비스 정지 중 오류: {ex.Message}");
        }
    }

    public void Dispose() => Stop();

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try { _debounce?.Change(DebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    // ───────────────────────── 읽기 (백그라운드) ─────────────────────────

    private sealed record Row(long Order, long Id, string Aumid, DateTime ArrivalUtc, string? Tag, string? Group,
        bool SuppressPopup, ToastContent? Content);

    private void RequestRead(bool force)
    {
        if (!_running) return;
        if (Interlocked.Exchange(ref _reading, 1) == 1)
        {
            _readAgain = true;
            return;
        }
        Task.Run(() =>
        {
            try
            {
                do
                {
                    _readAgain = false;
                    ReadOnce(force);
                    force = false;
                } while (_readAgain && _running);
            }
            catch (Exception ex)
            {
                Log.Error("알림 읽기 실패", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
            }
        });
    }

    private void ReadOnce(bool force)
    {
        List<Row>? rows = null;
        try
        {
            using var db = OpenDb();
            (long, long) stamp = (-1, -1);
            db.Query("select ifnull(max([Order]),0), count(*) from Notification where Type='toast'",
                r => stamp = (r.Int64(0), r.Int64(1)));
            if (!force && stamp == _lastStamp) return;
            rows = QueryRows(db);
            _lastStamp = stamp;
            _loggedFailure = false;
        }
        catch (Exception ex)
        {
            if (!_useCopy)
            {
                Log.Warn($"알림 DB 직접 읽기 실패 → 임시 복사본으로 전환: {ex.Message}");
                _useCopy = true;
                ReadOnce(force: true);
                return;
            }
            if (!_loggedFailure)
            {
                Log.Warn($"알림 DB 읽기 실패: {ex.Message}");
                _loggedFailure = true;
            }
            return;
        }

        int parsed = rows.Count(r => r.Content is not null);
        _dispatcher.BeginInvoke(() => Apply(rows, parsed));
    }

    private SqliteReader OpenDb()
    {
        if (!_useCopy) return SqliteReader.OpenReadOnly(DbPath);
        // 대안: db + wal + shm 을 임시 폴더로 복사 (원본은 공유 읽기로만 엶)
        string dir = Path.Combine(Path.GetTempPath(), "mongdock-wpn");
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, "wpndatabase.db");
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string src = DbPath + suffix, dst = copy + suffix;
            if (!File.Exists(src))
            {
                if (File.Exists(dst)) File.Delete(dst);
                continue;
            }
            using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        return SqliteReader.OpenReadOnly(copy);
    }

    private static List<Row> QueryRows(SqliteReader db)
    {
        const string withTransient =
            "select n.[Order], n.Id, n.Payload, n.ArrivalTime, n.Tag, n.[Group], h.PrimaryId, t.SuppressPopup " +
            "from Notification n join NotificationHandler h on h.RecordId = n.HandlerId " +
            "left join TransientTable t on t.NotificationId = n.Id " +
            "where n.Type = 'toast' order by n.ArrivalTime desc, n.[Order] desc limit ?1";
        const string plain =
            "select n.[Order], n.Id, n.Payload, n.ArrivalTime, n.Tag, n.[Group], h.PrimaryId, null " +
            "from Notification n join NotificationHandler h on h.RecordId = n.HandlerId " +
            "where n.Type = 'toast' order by n.ArrivalTime desc, n.[Order] desc limit ?1";

        var rows = new List<Row>();
        void OnRow(SqliteRow r)
        {
            string aumid = r.Text(6) ?? "";
            if (aumid.Length == 0) return;
            long ft = r.Int64(3);
            DateTime arrival;
            try { arrival = ft > 0 ? DateTime.FromFileTimeUtc(ft) : DateTime.MinValue; }
            catch (ArgumentOutOfRangeException) { arrival = DateTime.MinValue; }
            string? family = aumid.Contains('!') ? AppsFolder.FamilyOf(aumid) : null;
            rows.Add(new Row(r.Int64(0), r.Int64(1), aumid, arrival, r.Text(4), r.Text(5), ReadBool(r, 7),
                ToastPayload.Parse(r.Blob(2), family)));
        }
        try
        {
            db.Query(withTransient, OnRow, MaxRows);
        }
        catch (SqliteException)
        {
            rows.Clear();
            db.Query(plain, OnRow, MaxRows); // TransientTable 이 없거나 열 이름이 다른 버전
        }
        return rows;
    }

    private static bool ReadBool(SqliteRow r, int col)
    {
        switch (r.Type(col))
        {
            case Sqlite.SQLITE_INTEGER: return r.Int64(col) != 0;
            case Sqlite.SQLITE_TEXT:
                string s = (r.Text(col) ?? "").Trim();
                return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            default: return false;
        }
    }

    // ───────────────────────── 반영 (UI 스레드) ─────────────────────────

    private void Apply(List<Row> rows, int parsed)
    {
        if (!_running) return;
        try
        {
            var arrived = new List<NotificationItem>();
            bool baseline = !_baselineDone;
            DateTime now = DateTime.UtcNow;
            // 오래된 것부터 → Arrived 순서가 도착 순서
            foreach (var row in rows.OrderBy(r => r.Order))
            {
                if (!_seen.Add(row.Id) || baseline) continue;
                if (row.Content is null || row.SuppressPopup) continue;
                if (row.ArrivalUtc < _startedUtc.AddMinutes(-1)) continue;
                if (!string.IsNullOrEmpty(row.Tag) || !string.IsNullOrEmpty(row.Group))
                {
                    string key = row.Aumid + "|" + row.Tag + "|" + row.Group;
                    if (_lastBannerByTag.TryGetValue(key, out var last) && now - last < SameTagQuiet)
                    {
                        _lastBannerByTag[key] = now;
                        continue;
                    }
                    _lastBannerByTag[key] = now;
                }
                if (ToItem(row) is { } item) arrived.Add(item);
            }
            if (baseline)
            {
                _baselineDone = true;
                Log.Info($"알림 DB 읽음: 토스트 {rows.Count}개, 파싱 {parsed}개, 앱 {rows.Select(r => r.Aumid).Distinct(StringComparer.OrdinalIgnoreCase).Count()}개" +
                         (_useCopy ? " (임시 복사본)" : ""));
            }
            if (_lastBannerByTag.Count > 200)
                foreach (var k in _lastBannerByTag.Where(kv => now - kv.Value > SameTagQuiet).Select(kv => kv.Key).ToList())
                    _lastBannerByTag.Remove(k);
            // 이미 DB 에서 사라진 Id 는 "본 것"·"숨긴 것" 에서 정리 (Id 는 계속 증가하므로 다시 쓰이지 않음)
            var ids = rows.Select(r => r.Id).ToHashSet();
            _seen.IntersectWith(ids);
            int hiddenBefore = _hidden.Count;
            _hidden.IntersectWith(ids);
            if (_hidden.Count != hiddenBefore) SaveHidden();

            _rows = rows;
            Publish();

            if (arrived.Count > 0)
            {
                Log.Info($"새 알림 {arrived.Count}개: {string.Join(", ", arrived.Select(a => a.Aumid).Distinct())}");
                foreach (var item in arrived)
                {
                    try { Arrived?.Invoke(this, item); }
                    catch (Exception ex) { Log.Error("알림 Arrived 처리 실패", ex); }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("알림 목록 반영 실패", ex);
        }
    }

    private void Publish()
    {
        var list = new List<NotificationItem>();
        foreach (var row in _rows) // 이미 최신순
        {
            if (_hidden.Contains(row.Id)) continue;
            if (ToItem(row) is { } item) list.Add(item);
        }
        bool same = list.Count == _recent.Count && list.Select(i => i.Id).SequenceEqual(_recent.Select(i => i.Id));
        _recent = list;
        if (same) return;
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("알림 Changed 처리 실패", ex); }
    }

    private NotificationItem? ToItem(Row row)
    {
        var c = row.Content;
        if (c is null) return null;
        DateTime local = row.ArrivalUtc == DateTime.MinValue ? DateTime.Now : row.ArrivalUtc.ToLocalTime();
        return new NotificationItem(row.Id, row.Aumid, AppName(row.Aumid), c.Title, c.Lines, local, c.Attribution,
            c.AppLogoPath, c.AppLogoCircle, c.ImagePath);
    }

    /// <summary>AUMID → 표시 이름 (shell:AppsFolder, UI 스레드에서 캐시). 못 찾으면 AUMID 로 짐작.</summary>
    private string AppName(string aumid)
    {
        if (_names.TryGetValue(aumid, out var cached)) return cached;
        string? name = null;
        try { name = AppsFolder.GetAppDisplayName(aumid); }
        catch (Exception ex) { Log.Warn($"알림 앱 이름 조회 실패: {aumid} ({ex.Message})"); }
        name ??= GuessName(aumid);
        _names[aumid] = name;
        return name;
    }

    internal static string GuessName(string aumid)
    {
        // "Microsoft.YourPhone_8wekyb3d8bbwe!App" → "YourPhone", "com.todoist" → "todoist", "Chrome" → "Chrome"
        string s = aumid;
        int bang = s.IndexOf('!');
        if (bang > 0) s = s[..bang];
        int us = s.IndexOf('_');
        if (us > 0) s = s[..us];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return s.Length > 0 ? s : aumid;
    }

    // ───────────────────────── 동작 ─────────────────────────

    public void Open(NotificationItem item)
    {
        if (item is null) return;
        try
        {
            var windows = _tracker.Windows;
            var match = windows.Where(w => string.Equals(w.Aumid, item.Aumid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0 && item.Aumid.Contains('!'))
            {
                string family = AppsFolder.FamilyOf(item.Aumid) + "!";
                match = windows.Where(w => w.Aumid?.StartsWith(family, StringComparison.OrdinalIgnoreCase) == true).ToList();
            }
            if (match.Count > 0)
            {
                var w = match.FirstOrDefault(x => x.OnCurrentDesktop) ?? match[0];
                _launcher.Activate(w.Hwnd);
                Log.Info($"알림 클릭 → 창 활성화: {item.Aumid}");
            }
            else if (AppsFolder.RestoreAumidCase(item.Aumid) is { } real)
            {
                _launcher.Launch(new Models.PinItem { Name = item.AppName, Kind = Models.PinKind.Aumid, Target = real });
                Log.Info($"알림 클릭 → 앱 실행: {real}");
            }
            else
            {
                Log.Warn($"알림 클릭: 실행할 수 없는 AUMID (AppsFolder 에 없음): {item.Aumid}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"알림 앱 열기 실패: {item.Aumid}", ex);
        }
        Hide(item);
    }

    public void Hide(NotificationItem item)
    {
        if (item is null) return;
        if (_hidden.Add(item.Id))
        {
            SaveHidden();
            Publish();
        }
    }

    public void HideApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        bool changed = false;
        foreach (var row in _rows)
            if (string.Equals(row.Aumid, aumid, StringComparison.OrdinalIgnoreCase))
                changed |= _hidden.Add(row.Id);
        if (!changed) return;
        SaveHidden();
        Publish();
    }

    // ───────────────────────── 숨긴 Id 저장 (몽독 전용 파일) ─────────────────────────

    private void LoadHidden()
    {
        try
        {
            if (!File.Exists(HiddenPath)) return;
            var ids = JsonSerializer.Deserialize<List<long>>(File.ReadAllText(HiddenPath));
            if (ids is not null) foreach (long id in ids) _hidden.Add(id);
        }
        catch (Exception ex)
        {
            Log.Warn($"숨긴 알림 목록 읽기 실패 (무시): {ex.Message}");
        }
    }

    private void SaveHidden()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDirectory);
            string tmp = HiddenPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_hidden.OrderBy(x => x).ToList()));
            File.Move(tmp, HiddenPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"숨긴 알림 목록 저장 실패: {ex.Message}");
        }
    }
}
