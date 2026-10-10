using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 독 폴더 (#24-B): 폴더 내용 읽기·정렬, 독 아이콘(최근 파일 겹치기 / 폴더 아이콘) 만들기, 폴더 변화 감시(1초 묶음).
/// 독 창(UI 스레드)이 하나 만들어 씀. 폴더가 없어지면 회색 폴더 그림.
/// </summary>
internal sealed class DockFolderService : IDisposable
{
    private const int StackPx = 256;
    private readonly IIconService _icons;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Key, ImageSource Image)> _stackCache = new(StringComparer.OrdinalIgnoreCase);
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

    public DockFolderService(IIconService icons)
    {
        _icons = icons;
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

    /// <summary>
    /// 독 아이콘: Display=Folder 면 폴더 셸 아이콘, Stack 이면 최근 파일 3개(셸 썸네일/아이콘)를 살짝 어긋나게 겹침.
    /// 빈 폴더는 폴더 아이콘, 없는 폴더는 회색 폴더 그림. 같은 내용이면 캐시.
    /// </summary>
    public ImageSource Icon(PinItem pin, IconStyle style)
    {
        string path = pin.Target;
        var opts = pin.Folder ?? new FolderOptions();
        if (!IsAvailable(path)) return MissingIcon;
        if (opts.Display == FolderDisplay.Folder) return FolderIcon(path, style);

        var listed = List(path, FolderSort.Added, 3);
        if (listed is not { Items.Count: > 0 } l) return FolderIcon(path, style);
        string key = string.Join("|", l.Items.Select(f => f.FullName + ":" + Added(f).Ticks)) + "|" + style;
        if (_stackCache.TryGetValue(Normalize(path), out var cached) && cached.Key == key) return cached.Image;

        var rtb = ComposeStack(l.Items.Select(f => (BitmapSource?)Thumbnail(f.FullName, 160)).ToList());
        _stackCache[Normalize(path)] = (key, rtb);
        return rtb;
    }

    /// <summary>썸네일(최근 것 먼저) 최대 3장을 오른쪽 위로 조금씩 어긋나게 겹친 256 그림.</summary>
    internal static BitmapSource ComposeStack(IList<BitmapSource?> images)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // 뒤(오래된 것) → 앞(최근 것): 오른쪽 위로 조금씩 어긋나게
            const double box = 168, step = 22;
            for (int i = images.Count - 1; i >= 0; i--)
            {
                var img = images[i];
                if (img is null) continue;
                double s = Math.Min(box / img.Width, box / img.Height);
                double w = img.Width * s, h = img.Height * s;
                double cx = StackPx / 2.0 - step + i * step, cy = StackPx / 2.0 + step - i * step;
                var rect = new Rect(cx - w / 2, cy - h / 2, w, h);
                dc.PushOpacity(i == 0 ? 1 : 0.92);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), null, new Rect(rect.X + 2, rect.Y + 4, w, h));
                dc.DrawImage(img, rect);
                dc.Pop();
            }
        }
        var rtb = new RenderTargetBitmap(StackPx, StackPx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private ImageSource FolderIcon(string path, IconStyle style) =>
        _icons.GetIcon(new PinItem { Kind = PinKind.Exe, Target = path, Name = Path.GetFileName(path) }, style);

    /// <summary>없어진 폴더: 회색 둥근 판 + 폴더 기호.</summary>
    private static readonly ImageSource MissingIcon = CreateMissingIcon();

    private static ImageSource CreateMissingIcon()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC)), null, new Rect(28, 28, 200, 200), 46, 46);
            var text = new FormattedText("", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 110, Brushes.White, 1.0);
            dc.DrawText(text, new Point(128 - text.Width / 2, 128 - text.Height / 2));
        }
        var rtb = new RenderTargetBitmap(StackPx, StackPx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
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
        foreach (var dict in new System.Collections.IDictionary[] { _versions, _stackCache, _exists, _lastBump, _retryWatchAt })
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
    public static string? DownloadsFolder()
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) != 0) return null;
            string? s = Marshal.PtrToStringUni(p);
            return s is { Length: > 0 } && Directory.Exists(s) ? s : null;
        }
        catch { return null; }
        finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    }
}
