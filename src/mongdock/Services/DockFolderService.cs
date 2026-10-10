using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 독 폴더 (#24-B): 폴더 내용 읽기·정렬, 독 아이콘(몽독 폴더 + 종류 그림), 새 파일 수, 폴더 변화 감시(1초 묶음).
/// 독 창(UI 스레드)이 하나 만들어 씀. 폴더가 없어지면 회색 폴더 그림.
/// </summary>
internal sealed class DockFolderService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Exists, long At)> _exists = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastBump = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _retryWatchAt = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>있는지 다시 볼 간격, 같은 폴더 아이콘을 다시 만드는 최소 간격(내려받는 동안 1초마다 다시 그리지 않게), 감시가 죽은 뒤 다시 켜기까지.</summary>
    private const long RecheckMs = 5000, MinBumpMs = 3000, WatchRetryMs = 10000;

    private sealed class Watch : IDisposable
    {
        public required FileSystemWatcher Watcher { get; init; }
        public required Timer Debounce { get; init; }
        public void Dispose()
        {
            Watcher.EnableRaisingEvents = false;
            Watcher.Dispose();
            Debounce.Dispose();
        }
    }

    public DockFolderService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>폴더 내용이 바뀜 (1초 묶음, UI 스레드). 인자 = 폴더 경로.</summary>
    public event Action<string>? Changed;

    /// <summary>폴더마다 바뀔 때 1씩 오르는 번호 (독 항목 id 에 넣어 아이콘을 다시 만들게).</summary>
    public int Version(string path) => _versions.GetValueOrDefault(Normalize(path));

    // ───────────────────────── 내용 ─────────────────────────

    /// <summary>폴더 안 항목(숨김·시스템·desktop.ini 제외)을 정렬해 최대 max 개 + 전체 수. 폴더가 없거나 못 읽으면 null.</summary>
    public static (List<FileSystemInfo> Items, int Total)? List(string path, FolderSort sort, int max)
    {
        try
        {
            var dir = new DirectoryInfo(Environment.ExpandEnvironmentVariables(path));
            if (!dir.Exists) return null;
            var all = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .Where(f => !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                .ToList();
            IEnumerable<FileSystemInfo> ordered = sort == FolderSort.Name
                ? all.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                : all.OrderByDescending(Added);
            return (ordered.Take(max).ToList(), all.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Log.Warn($"독 폴더 읽기 실패: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>"추가된 날짜": 이 폴더에 들어온 시각에 가까운 값 (만든 시각과 고친 시각 중 늦은 것 — 다운로드는 받은 시각).</summary>
    public static DateTime Added(FileSystemInfo f)
    {
        try { return f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// 폴더가 있는지 — UI 스레드용 캐시. 5초 지나면 백그라운드에서 다시 보고, 바뀌면 Changed (독이 다시 그림).
    /// 네트워크 경로(\\서버, 연결된 드라이브)는 처음에도 기다리지 않음 — 확인 전엔 없음으로 보고 곧 다시 그림.
    /// </summary>
    public bool IsAvailable(string path)
    {
        string key = Normalize(path);
        long now = Environment.TickCount64;
        if (_exists.TryGetValue(key, out var known))
        {
            if (now - known.At > RecheckMs) Recheck(key);
            return known.Exists;
        }
        if (IsNetwork(key))
        {
            _exists[key] = (false, now);
            Recheck(key);
            return false;
        }
        bool exists = Exists(key);
        _exists[key] = (exists, now);
        return exists;
    }

    private void Recheck(string key)
    {
        if (_disposed || !_checking.Add(key)) return;
        Task.Run(() => Exists(key)).ContinueWith(t => _dispatcher.BeginInvoke(() =>
        {
            _checking.Remove(key);
            if (_disposed) return;
            bool now = t.Status == TaskStatus.RanToCompletion && t.Result;
            bool changed = !_exists.TryGetValue(key, out var old) || old.Exists != now;
            _exists[key] = (now, Environment.TickCount64);
            if (changed) Bump(key);
        }), TaskScheduler.Default);
    }

    private static bool IsNetwork(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            return path.Length >= 2 && path[1] == ':' && new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch { return true; }
    }

    public static bool Exists(string path)
    {
        try { return Directory.Exists(Environment.ExpandEnvironmentVariables(path)); }
        catch { return false; }
    }

    // ───────────────────────── 독 아이콘 ─────────────────────────

    private static readonly Dictionary<(FolderGlyph, bool), ImageSource> IconCache = new();

    /// <summary>독 아이콘: 몽독 폴더 (알려진 폴더면 종류 그림 — 무슨 폴더인지 보이게). 없는 폴더는 회색. 그림 종류마다 한 번만 그림.</summary>
    public ImageSource Icon(PinItem pin, IconStyle style)
    {
        bool missing = !IsAvailable(pin.Target);
        var key = (GlyphFor(pin.Target), missing);
        if (!IconCache.TryGetValue(key, out var img)) IconCache[key] = img = MacIconRenderer.Folder(key.Item1, missing);
        return img;
    }

    private static Dictionary<string, FolderGlyph>? _known;

    /// <summary>알려진 폴더(다운로드·문서·사진·바탕 화면·음악·동영상 — 사용자가 옮겼어도 실제 위치)면 그 그림, 아니면 None.</summary>
    public static FolderGlyph GlyphFor(string path)
    {
        if (_known is null)
        {
            _known = new Dictionary<string, FolderGlyph>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, glyph) in new[]
            {
                ("374DE290-123F-4565-9164-39C4925E467B", FolderGlyph.Downloads),
                ("FDD39AD0-238F-46AF-ADB4-6C85480369C7", FolderGlyph.Documents),
                ("33E28130-4E1E-4676-835A-98395C3BC3BB", FolderGlyph.Pictures),
                ("B4BFCC3A-DB2C-424C-B029-7FE99A87C641", FolderGlyph.Desktop),
                ("4BD8D571-6D19-48D3-BE97-422220080E43", FolderGlyph.Music),
                ("18989B1D-99B5-455B-841C-AB7C74E4DDFC", FolderGlyph.Videos),
            })
                if (KnownFolder(new Guid(id)) is { } kp) _known.TryAdd(Normalize(kp), glyph);
        }
        return _known.GetValueOrDefault(Normalize(path));
    }

    // ───────────────────────── 새 파일 점 ─────────────────────────

    private readonly Dictionary<string, (int Version, DateTime Since, int Count)> _newFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _counting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 마지막으로 판을 연 뒤 추가된 항목 수 (최대 99). 폴더가 바뀌거나 연 시각이 바뀌면 백그라운드에서 다시 세고,
    /// 수가 달라지면 Changed. 세는 동안은 이전 값.
    /// </summary>
    public int NewFiles(PinItem pin)
    {
        if (pin.Folder?.LastOpened is not DateTime since || !IsAvailable(pin.Target)) return 0;
        string key = Normalize(pin.Target);
        int version = Version(key);
        bool known = _newFiles.TryGetValue(key, out var c);
        if (known && c.Version == version && c.Since == since) return c.Count;
        if (_counting.Add(key))
        {
            Task.Run(() => CountNewer(key, since)).ContinueWith(t => _dispatcher.BeginInvoke(() =>
            {
                _counting.Remove(key);
                if (_disposed || t.Status != TaskStatus.RanToCompletion) return;
                int before = _newFiles.TryGetValue(key, out var old) ? old.Count : 0;
                _newFiles[key] = (version, since, t.Result);
                if (t.Result != before) Changed?.Invoke(key);
            }), TaskScheduler.Default);
        }
        return known && c.Since == since ? c.Count : 0;
    }

    private static int CountNewer(string path, DateTime sinceUtc)
    {
        try
        {
            int n = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }))
            {
                if (f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || Added(f) <= sinceUtc) continue;
                if (++n >= 99) break;
            }
            return n;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>파일·폴더의 셸 썸네일(그림은 미리보기, 그 밖은 아이콘). 실패하면 null.</summary>
    public static BitmapSource? Thumbnail(string path, int px)
    {
        object? obj = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(Native.IShellItemImageFactory).GUID;
            if (Native.Shell32.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out obj) != 0 || obj is null) return null;
            var factory = (Native.IShellItemImageFactory)obj;
            if (factory.GetImage(new Native.SIZE(px, px), Native.ShellConst.SIIGBF_BIGGERSIZEOK, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            return IconService.HBitmapToBitmapSource(hbmp);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) Native.Gdi32.DeleteObject(hbmp);
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    // ───────────────────────── 감시 ─────────────────────────

    /// <summary>독에 있는 폴더 목록에 맞춰 감시를 켜고 끔.</summary>
    public void SetWatched(IEnumerable<string> paths)
    {
        if (_disposed) return;
        var want = paths.Select(Normalize).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _watches.Keys.Where(k => !want.Contains(k)).ToList())
        {
            _watches[gone].Dispose();
            _watches.Remove(gone);
        }
        // 독에서 뺀 폴더의 기록도 정리 (번호·아이콘 캐시·확인 결과)
        foreach (var dict in new System.Collections.IDictionary[] { _versions, _newFiles, _exists, _lastBump, _retryWatchAt })
            foreach (var k in dict.Keys.Cast<string>().Where(k => !want.Contains(k)).ToList()) dict.Remove(k);
        long now = Environment.TickCount64;
        foreach (var path in want)
        {
            if (_watches.ContainsKey(path) || !IsAvailable(path)) continue;
            if (_retryWatchAt.TryGetValue(path, out long retry) && now < retry) continue;
            try
            {
                var fsw = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                };
                string key = path;
                var debounce = new Timer(_ => _dispatcher.BeginInvoke(() => BumpThrottled(key)), null, Timeout.Infinite, Timeout.Infinite);
                void Poke(object? s, EventArgs e)
                {
                    try { debounce.Change(1000, Timeout.Infinite); } catch (ObjectDisposedException) { }
                }
                fsw.Created += Poke;
                fsw.Deleted += Poke;
                fsw.Renamed += Poke;
                fsw.Changed += Poke;
                fsw.Error += (_, e) =>
                {
                    // 버퍼 넘침: 바뀐 것을 놓쳤을 수 있음 → 다시 그림. 그 밖(폴더 삭제·네트워크 끊김): 감시를 버리고 나중에 다시 켬
                    if (e.GetException() is InternalBufferOverflowException) { Poke(s: null, e: EventArgs.Empty); return; }
                    Log.Warn($"독 폴더 감시 오류: {e.GetException().GetType().Name}");
                    _dispatcher.BeginInvoke(() => DropWatch(key));
                };
                fsw.EnableRaisingEvents = true;
                _watches[path] = new Watch { Watcher = fsw, Debounce = debounce };
            }
            catch (Exception ex)
            {
                Log.Warn($"독 폴더 감시 시작 실패: {ex.GetType().Name}");
            }
        }
    }

    private void DropWatch(string key)
    {
        if (_disposed || !_watches.Remove(key, out var w)) return;
        w.Dispose();
        _retryWatchAt[key] = Environment.TickCount64 + WatchRetryMs;
        _exists.Remove(key);
        Bump(key); // 독이 다시 그리며 있는지 다시 보고, 10초 뒤 SetWatched 가 감시를 다시 켬
    }

    /// <summary>같은 폴더는 3초에 한 번만 다시 그림 (내려받는 동안 파일 시각이 계속 바뀌어도).</summary>
    private void BumpThrottled(string key)
    {
        if (_disposed) return;
        long since = Environment.TickCount64 - _lastBump.GetValueOrDefault(key, long.MinValue / 2);
        if (since < MinBumpMs && _watches.TryGetValue(key, out var w))
        {
            try { w.Debounce.Change(MinBumpMs - since, Timeout.Infinite); } catch (ObjectDisposedException) { }
            return;
        }
        _lastBump[key] = Environment.TickCount64;
        Bump(key);
    }

    private void Bump(string path)
    {
        if (_disposed) return;
        _versions[path] = _versions.GetValueOrDefault(path) + 1;
        try { Changed?.Invoke(path); }
        catch (Exception ex) { Log.Error("독 폴더 변화 처리 실패", ex); }
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))); }
        catch { return path ?? ""; }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _watches.Values) w.Dispose();
        _watches.Clear();
    }

    // ───────────────────────── 다운로드 폴더 ─────────────────────────

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    /// <summary>다운로드 폴더 (사용자가 옮겼어도 맞게 FOLDERID_Downloads). 못 구하면 null.</summary>
    public static string? DownloadsFolder() => KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"));

    private static string? KnownFolder(Guid id)
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out p) != 0) return null;
            string? s = Marshal.PtrToStringUni(p);
            return s is { Length: > 0 } && Directory.Exists(s) ? s : null;
        }
        catch { return null; }
        finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    }
}
