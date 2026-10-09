using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Mongdock.Native;

namespace Mongdock.Services;

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
///   열기/조회가 실패하면 db·-wal·-shm 을 %APPDATA%\mongdock\cache\wpn 으로 복사해서 읽는 방식으로 전환
///   (원본 크기·수정 시각이 그대로면 복사 생략, 1분마다 직접 읽기 재시도, Stop 때 사본 삭제).
/// 감지: 알림 폴더 FileSystemWatcher(wpndatabase.db*) → 400ms 디바운스, 보조로 4초 폴링(가벼운 max(Order)/count/max(ArrivalTime) 조회 후 바뀌었을 때만 전체 읽기).
///   Stop(일시 정지 포함) 때 감시·폴링을 멈추고, 다시 Start 하면 그 사이 쌓인 알림은 배너 없이 "본 것" 으로 처리.
/// 새 알림: 첫 읽기의 알림은 모두 "본 것" — 그 뒤 처음 보는 Id 이고 도착 시간이 시작 시점 이후면 Arrived.
/// 사생활: 알림 제목·본문은 로그에 남기지 않음 (개수·AUMID 만).
///
/// 윈도우 기본 토스트 팝업 숨기기는 NativeToastSuppressor (팝업 창을 화면 밖으로 옮김, 알림 기록은 그대로).
///
/// 스토어판 (#7): 윈도우 알림 접근(UserNotificationListener)이 허용돼 있으면 목록·새 알림은 그 API 로 읽고(NotificationListener),
///   DB 는 같은 알림의 보낸 사람 사진·이미지·태그를 채우는 데만 씀 (못 읽어도 글자만으로 동작). 감지(폴더 감시·4초 폴링)는 같음.
///   몽독에서 지우면(열기·숨기기·모두 지우기) 윈도우 알림 센터에서도 지움. 접근이 거부·실패하면 DB 방식으로 돌아감.
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
    private static readonly string CopyDir = Path.Combine(AppInfo.DataDirectory, "cache", "wpn");
    /// <summary>복사본 모드에서 이 시간이 지나면 직접 읽기를 다시 시도.</summary>
    private const long DirectRetryMs = 60_000;
    private static readonly string[] DbSuffixes = { "", "-wal", "-shm" };

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
    private volatile bool _useCopy;
    private long _copySinceTick;
    /// <summary>마지막 복사 때 원본(db/-wal/-shm)의 (크기, 수정 시각). 같으면 다시 복사하지 않음.</summary>
    private readonly (long Size, DateTime Mtime)[] _copiedStamp = new (long, DateTime)[3];
    private volatile bool _deleteCopiesAfterRead;
    private int _reading;          // 0/1 — 동시에 한 번만 읽음
    private volatile bool _readAgain;     // 읽는 중에 또 변경 → 끝나고 한 번 더
    private (long MaxOrder, long Count, long MaxArrival) _lastStamp = (-1, -1, -1);
    private List<Row> _rows = new();
    private IReadOnlyList<NotificationItem> _recent = Array.Empty<NotificationItem>();
    private bool _loggedFailure;
    /// <summary>스토어판에서 윈도우 알림 접근이 허용돼 목록을 UserNotificationListener 로 읽는 중.</summary>
    private volatile bool _listener;

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

    // ───────────────────────── 토스트 숨기기용 신호 (NativeToastSuppressor, UI 스레드) ─────────────────────────

    /// <summary>마지막으로 Arrived(배너)를 발생시킨 시각 (Environment.TickCount64, 없으면 0).</summary>
    internal long LastBannerTick { get; private set; }
    /// <summary>마지막으로 새로 들어온 토스트 중 사용자 조작이 필요한 시나리오(reminder/alarm/incomingCall/urgent)가 있던 시각 (없으면 0).
    /// 배너 여부와 상관없이 기록 — 이런 토스트는 윈도우 팝업을 숨기지 않는다.</summary>
    internal long LastInteractiveToastTick { get; private set; }
    /// <summary>
    /// 마지막으로 새 토스트 행의 배너를 <b>일부러</b> 생략한 시각 (같은 태그 갱신 — 디스코드 등, 또는 SuppressPopup. 없으면 0).
    /// 파싱 실패 등 "배너를 못 낸" 경우는 포함하지 않음 → 토스트 숨기기가 이미 숨긴 셸 팝업을 계속 숨겨도 되는지 판단.
    /// </summary>
    internal long LastSkippedBannerTick { get; private set; }
    /// <summary>새 토스트 행을 반영함 (배너가 나갔든 아니든). LastBannerTick/LastInteractiveToastTick/LastSkippedBannerTick 갱신 직후.</summary>
    internal event EventHandler? ToastActivity;

    /// <summary>디바운스 없이 곧바로 DB 변경 확인 (가벼운 stamp 조회, 바뀌었을 때만 전체 읽기). 아무 스레드.</summary>
    internal void RequestReadNow() => RequestRead(force: false);

    // ───────────────────────── 시작/정지 ─────────────────────────

    public void Start()
    {
        if (_running) return;
        _running = true;
        _startedUtc = DateTime.UtcNow;
        // 다시 시작(일시 정지 해제)이면 그 사이 도착한 알림은 배너 없이 기준선으로
        _baselineDone = false;
        _lastStamp = (-1, -1, -1);
        LoadHidden();
        DeleteLegacyTempCopy();
        try
        {
            _listener = NotificationListener.Supported && NotificationListener.IsAllowed;
            if (NotificationListener.Supported)
                Log.Info(_listener ? "알림: 윈도우 알림 접근 허용됨 → 공식 API 로 읽음 (DB 는 사진 보강)" : $"알림: 윈도우 알림 접근 {NotificationListener.Status()} → DB 방식");
            if (!_listener && !DbUsable())
            {
                Log.Warn("알림: winsqlite3.dll 또는 wpndatabase.db 없음 → 알림 기능 끔");
                return;
            }
            IsAvailable = true;
            _debounce = new Timer(_ => RequestRead(force: false), null, Timeout.Infinite, Timeout.Infinite);
            _poll = new Timer(_ => RequestRead(force: false), null, PollMs, PollMs);
            try
            {
                if (!Directory.Exists(DbFolder)) throw new DirectoryNotFoundException("알림 폴더 없음");
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

    private static bool DbUsable() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll")) && File.Exists(DbPath);

    /// <summary>윈도우 알림 접근 허용 상태가 바뀌었을 수 있음 (허락 창 뒤) → 읽는 방식을 다시 정함. UI 스레드.</summary>
    internal void ReconsiderSource()
    {
        if (!_running || _listener == NotificationListener.IsAllowed) return;
        Stop();
        Start();
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
        // 읽는 중이면 그 작업이 끝난 뒤 지움 (열린 사본을 지우려다 실패하지 않게)
        if (Volatile.Read(ref _reading) == 0) DeleteCopies();
        else _deleteCopiesAfterRead = true;
    }

    /// <summary>예전 버전이 %TEMP%\mongdock-wpn 에 남긴 사본 정리 (한 번, 실패 무시).</summary>
    private static void DeleteLegacyTempCopy()
    {
        try
        {
            string old = Path.Combine(Path.GetTempPath(), "mongdock-wpn");
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
        }
        catch (Exception) { /* 다른 인스턴스가 쓰는 중 등 — 무시 */ }
    }

    /// <summary>복사본 모드의 임시 사본 삭제 (Stop·종료·직접 읽기 복구 때).</summary>
    private void DeleteCopies()
    {
        _deleteCopiesAfterRead = false;
        Array.Clear(_copiedStamp);
        try
        {
            if (Directory.Exists(CopyDir)) Directory.Delete(CopyDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"알림 DB 사본 삭제 실패: {ex.Message}");
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
                if (!_running && _deleteCopiesAfterRead) DeleteCopies();
            }
        });
    }

    private void ReadOnce(bool force)
    {
        if (_listener)
        {
            try
            {
                ReadFromListener(force);
                return;
            }
            catch (Exception ex)
            {
                // 접근 취소(윈도우 설정에서 끔) 등 → DB 방식으로
                _listener = false;
                _lastStamp = (-1, -1, -1);
                Log.Warn($"윈도우 알림 접근으로 읽기 실패 → DB 방식으로: {ex.GetType().Name} {ex.Message}");
                if (!DbUsable()) return;
                force = true;
            }
        }

        // 복사본 모드가 1분 넘게 지났으면 직접 읽기 재시도 (일시적 잠금이었을 수 있음)
        if (_useCopy && Environment.TickCount64 - _copySinceTick > DirectRetryMs)
        {
            try
            {
                using (var direct = SqliteReader.OpenReadOnly(DbPath))
                    direct.Query("select count(*) from Notification", _ => { });
                _useCopy = false;
                Log.Info("알림 DB 직접 읽기 복구 → 사본 모드 끝");
                DeleteCopies();
            }
            catch (Exception)
            {
                _copySinceTick = Environment.TickCount64; // 1분 뒤 다시
            }
        }

        List<Row>? rows = null;
        try
        {
            using var db = OpenDb();
            // 같은 태그로 바꿔치기한 알림(Claude 등)은 마지막 행을 지우고 다시 넣어 [Order]·개수가 그대로일 수 있음 → 도착 시각도 봄
            (long, long, long) stamp = (-1, -1, -1);
            db.Query("select ifnull(max([Order]),0), count(*), ifnull(max(ArrivalTime),0) from Notification where Type='toast'",
                r => stamp = (r.Int64(0), r.Int64(1), r.Int64(2)));
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
                _copySinceTick = Environment.TickCount64;
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

    /// <summary>
    /// 스토어판: 목록은 UserNotificationListener, 같은 알림의 사진·이미지·태그·SuppressPopup 은 DB 에서 (Id 가 같거나, 같은 앱·3초 안 도착).
    /// DB 를 못 읽으면 글자만. 목록이 그대로면(최대 Id·개수·최근 도착) 아무것도 안 함.
    /// </summary>
    private void ReadFromListener(bool force)
    {
        var toasts = NotificationListener.Read();
        (long, long, long) stamp = (toasts.Count == 0 ? 0 : toasts.Max(t => (long)t.Id), toasts.Count,
            toasts.Count == 0 ? 0 : toasts.Max(t => t.ArrivalUtc.Ticks));
        if (!force && stamp == _lastStamp) return;

        List<Row> db = new();
        if (DbUsable())
        {
            try
            {
                using var reader = OpenDb();
                db = QueryRows(reader);
            }
            catch (Exception ex)
            {
                if (!_loggedFailure) Log.Info($"알림 DB 보강 못 함 (글자만 표시): {ex.Message}");
                _loggedFailure = true;
            }
        }
        var byId = db.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<Row>();
        foreach (var t in toasts.OrderByDescending(t => t.ArrivalUtc).ThenByDescending(t => t.Id).Take(MaxRows))
        {
            Row? match = byId.TryGetValue(t.Id, out var same) && string.Equals(same.Aumid, t.Aumid, StringComparison.OrdinalIgnoreCase)
                ? same
                : db.FirstOrDefault(r => string.Equals(r.Aumid, t.Aumid, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((r.ArrivalUtc - t.ArrivalUtc).TotalSeconds) <= 3
                    && (r.Content?.Title is null || t.Title is null || r.Content.Title == t.Title));
            var content = match?.Content ?? (t.Title is null && t.Lines.Count == 0
                ? null
                : new ToastContent(t.Title, t.Lines, null, null, false, null));
            rows.Add(new Row(t.Id, t.Id, t.Aumid, t.ArrivalUtc, match?.Tag, match?.Group, match?.SuppressPopup ?? false, content));
        }
        _lastStamp = stamp;
        int parsed = rows.Count(r => r.Content is not null);
        _dispatcher.BeginInvoke(() => Apply(rows, parsed));
    }

    private SqliteReader OpenDb()
    {
        if (!_useCopy) return SqliteReader.OpenReadOnly(DbPath);
        // 대안: db + wal + shm 을 몽독 캐시 폴더로 복사 (원본은 공유 읽기로만 엶). 원본이 그대로면 복사 생략.
        Directory.CreateDirectory(CopyDir);
        string copy = Path.Combine(CopyDir, "wpndatabase.db");
        for (int i = 0; i < DbSuffixes.Length; i++)
        {
            string src = DbPath + DbSuffixes[i], dst = copy + DbSuffixes[i];
            var info = new FileInfo(src);
            if (!info.Exists)
            {
                if (File.Exists(dst)) File.Delete(dst);
                _copiedStamp[i] = default;
                continue;
            }
            var stamp = (info.Length, info.LastWriteTimeUtc);
            if (stamp == _copiedStamp[i] && File.Exists(dst)) continue;
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            _copiedStamp[i] = stamp;
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
            bool newRows = false, interactive = false, skipped = false;
            foreach (var row in rows.OrderBy(r => r.Order))
            {
                if (!_seen.Add(row.Id) || baseline) continue;
                newRows = true;
                if (row.Content?.IsInteractiveScenario == true) interactive = true;
                if (row.SuppressPopup)
                {
                    skipped = true; // 셸도 팝업을 띄우지 않는 알림
                    continue;
                }
                if (row.Content is null) continue;
                if (row.ArrivalUtc < _startedUtc.AddMinutes(-1)) continue;
                if (!string.IsNullOrEmpty(row.Tag) || !string.IsNullOrEmpty(row.Group))
                {
                    string key = row.Aumid + "|" + row.Tag + "|" + row.Group;
                    if (_lastBannerByTag.TryGetValue(key, out var last) && now - last < SameTagQuiet)
                    {
                        _lastBannerByTag[key] = now;
                        skipped = true; // 같은 태그 갱신 (디스코드 등) — 배너를 일부러 생략
                        continue;
                    }
                    _lastBannerByTag[key] = now;
                }
                if (ToItem(row) is { } item) arrived.Add(item);
            }
            if (baseline)
            {
                _baselineDone = true;
                Log.Info($"알림 {(_listener ? "읽음(윈도우 알림 접근)" : "DB 읽음")}: 토스트 {rows.Count}개, 파싱 {parsed}개, 앱 {rows.Select(r => r.Aumid).Distinct(StringComparer.OrdinalIgnoreCase).Count()}개" +
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

            long tick = Environment.TickCount64;
            if (interactive) LastInteractiveToastTick = tick;
            if (skipped) LastSkippedBannerTick = tick;
            if (arrived.Count > 0)
            {
                Log.Info($"새 알림 {arrived.Count}개: {string.Join(", ", arrived.Select(a => a.Aumid).Distinct())}");
                LastBannerTick = tick;
                foreach (var item in arrived)
                {
                    try { Arrived?.Invoke(this, item); }
                    catch (Exception ex) { Log.Error("알림 Arrived 처리 실패", ex); }
                }
            }
            if (newRows)
            {
                try { ToastActivity?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { Log.Error("알림 ToastActivity 처리 실패", ex); }
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
            RemoveFromWindows(new[] { item.Id });
            SaveHidden();
            Publish();
        }
    }

    /// <summary>스토어판에서 윈도우 알림 접근이 허용돼 있으면 윈도우 알림 센터에서도 지움 (#7 결정 17). 일반판은 몽독 목록에서만.</summary>
    private void RemoveFromWindows(IEnumerable<long> ids)
    {
        if (_listener) NotificationListener.Remove(ids.ToList());
    }

    public void HideApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        var removed = new List<long>();
        foreach (var row in _rows)
            if (string.Equals(row.Aumid, aumid, StringComparison.OrdinalIgnoreCase) && _hidden.Add(row.Id))
                removed.Add(row.Id);
        if (removed.Count == 0) return;
        RemoveFromWindows(removed);
        SaveHidden();
        Publish();
    }

    public void HideAll()
    {
        var removed = _rows.Where(row => _hidden.Add(row.Id)).Select(row => row.Id).ToList();
        if (removed.Count == 0) return;
        RemoveFromWindows(removed);
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
            AtomicFile.WriteAllText(HiddenPath, JsonSerializer.Serialize(_hidden.OrderBy(x => x).ToList()));
        }
        catch (Exception ex)
        {
            Log.Warn($"숨긴 알림 목록 저장 실패: {ex.Message}");
        }
    }
}
