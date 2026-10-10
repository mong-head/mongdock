using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Mongdock.Services;

/// <summary>휴지통 안 항목 하나 (셸 휴지통 네임스페이스).</summary>
internal sealed record RecycledItem(string Name, string Path, string OriginalFolder, DateTime Deleted, long Size, bool IsFolder);

/// <summary>
/// 휴지통 다루기 (#24-C): 보내기(되돌릴 수 있게만 — 몽독이 직접 완전 삭제하지 않음), 개수·용량, 최근 버린 항목, 복원, 비우기.
/// 셸 COM 은 STA 스레드에서 (UI 를 막지 않게 따로 띄움).
/// </summary>
internal static class RecycleBin
{
    // ───────────────────────── 보내기 ─────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>바뀜 (보내기·복원·비우기 끝) — 독 휴지통 아이콘이 바로 다시 봄.</summary>
    public static event Action? Touched;

    /// <summary>
    /// 휴지통으로. 휴지통에 못 넣는 경우(네트워크 드라이브·너무 큰 파일)는 윈도우가 "완전히 지울까요?" 를 묻게 둠(FOF_WANTNUKEWARNING)
    /// — 몽독이 묻지 않고 완전 삭제하는 일은 없음.
    /// </summary>
    public static bool Send(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return true;
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", paths) + "\0\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING),
            };
            int r = SHFileOperation(ref op);
            if (r != 0 || op.fAnyOperationsAborted) Log.Warn($"휴지통으로 보내기 실패/취소 (코드 {r})");
            return r == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            Log.Warn($"휴지통으로 보내기 실패: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>백그라운드(STA)에서 보내기 — 큰 폴더도 독이 멈추지 않게.</summary>
    public static void SendInBackground(IReadOnlyCollection<string> paths) => RunSta(() =>
    {
        if (Send(paths)) Log.Info($"휴지통으로 버림: {paths.Count}개");
        Touched?.Invoke();
    });

    // ───────────────────────── 개수·용량 ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    /// <summary>모든 드라이브 휴지통의 항목 수·바이트. 실패하면 null.</summary>
    public static (long Count, long Bytes)? Query()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) != 0) return null;
            return (info.i64NumItems, info.i64Size);
        }
        catch (Exception ex)
        {
            Log.Warn($"휴지통 확인 실패: {ex.GetType().Name}");
            return null;
        }
    }

    // ───────────────────────── 항목 ─────────────────────────

    /// <summary>최근 버린 순으로 최대 max 개 (셸 휴지통 폴더). STA 스레드에서 불러야 함.</summary>
    public static List<RecycledItem> List(int max)
    {
        var list = new List<RecycledItem>();
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return list;
            shell = Activator.CreateInstance(type);
            dynamic folder = ((dynamic)shell!).NameSpace(10); // ssfBITBUCKET
            if (folder is null) return list;
            foreach (dynamic item in folder.Items())
            {
                try
                {
                    string name = item.Name;
                    string path = item.Path;
                    object? deleted = item.ExtendedProperty("System.Recycle.DateDeleted");
                    object? from = item.ExtendedProperty("System.Recycle.DeletedFrom");
                    long size = 0;
                    try { size = Convert.ToInt64(item.Size); } catch { }
                    bool isFolder = false;
                    try { isFolder = item.IsFolder; } catch { }
                    list.Add(new RecycledItem(name, path, from as string ?? "", deleted is DateTime d ? d : DateTime.MinValue, size, isFolder));
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"휴지통 목록 읽기 실패: {ex.GetType().Name}");
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
        return list.OrderByDescending(i => i.Deleted).Take(max).ToList();
    }

    /// <summary>원래 자리로 되돌림. 같은 이름이 이미 있으면 그대로 두고 false. 휴지통 정보 파일($I…)도 같이 지움.</summary>
    public static bool Restore(RecycledItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(item.OriginalFolder)) return false;
            string dest = System.IO.Path.Combine(item.OriginalFolder, item.Name);
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                Log.Warn("휴지통 복원: 같은 이름이 이미 있음");
                return false;
            }
            Directory.CreateDirectory(item.OriginalFolder);
            if (Directory.Exists(item.Path)) Directory.Move(item.Path, dest);
            else File.Move(item.Path, dest);
            // $R… 과 짝인 $I… (지운 날짜·원래 경로 기록)
            string dir = System.IO.Path.GetDirectoryName(item.Path) ?? "";
            string file = System.IO.Path.GetFileName(item.Path);
            if (file.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
            {
                string info = System.IO.Path.Combine(dir, "$I" + file[2..]);
                if (File.Exists(info)) File.Delete(info);
            }
            Log.Info("휴지통에서 복원");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"휴지통 복원 실패: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            Touched?.Invoke();
        }
    }

    // ───────────────────────── 비우기 ─────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    /// <summary>모든 드라이브의 휴지통 비우기 — 몽독 확인 카드에서 "비우기" 를 누른 뒤에만 부름. 백그라운드.</summary>
    public static void EmptyInBackground() => RunSta(() =>
    {
        try
        {
            int r = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            Log.Info(r == 0 ? "휴지통 비움" : $"휴지통 비우기 결과 코드 {r}");
        }
        catch (Exception ex)
        {
            Log.Warn($"휴지통 비우기 실패: {ex.GetType().Name}");
        }
        Touched?.Invoke();
    });

    /// <summary>탐색기에서 휴지통 열기.</summary>
    public static void OpenInExplorer()
    {
        try { using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true })) { } }
        catch (Exception ex) { Log.Warn($"휴지통 열기 실패: {ex.GetType().Name}"); }
    }

    /// <summary>"340MB" 처럼 짧게 (1000 단위가 아니라 윈도우처럼 1024).</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes}B" : v >= 100 ? $"{v:0}{units[u]}" : $"{v:0.#}{units[u]}";
    }

    public static Thread RunSta(Action action)
    {
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error("휴지통 작업 실패", ex); }
        }) { IsBackground = true, Name = "mongdock recycle bin" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t;
    }
}

/// <summary>
/// 독 휴지통 상태 (빔/참, 개수·용량): 15초마다 + 몽독이 보내기·복원·비우기 했을 때 백그라운드에서 확인, 바뀌면 Changed (UI 스레드).
/// </summary>
internal sealed class RecycleBinWatcher : IDisposable
{
    private const int PollMs = 15_000;
    private readonly Dispatcher _dispatcher;
    private readonly Timer _timer;
    private int _checking;
    private bool _disposed;

    public RecycleBinWatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new Timer(_ => Check(), null, 0, PollMs);
        RecycleBin.Touched += OnTouched;
    }

    public long Count { get; private set; }
    public long Bytes { get; private set; }
    public bool Known { get; private set; }
    public event Action? Changed;

    private void OnTouched()
    {
        try { _timer.Change(300, PollMs); } catch (ObjectDisposedException) { }
    }

    public void CheckSoon() => OnTouched();

    private void Check()
    {
        if (_disposed || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var q = RecycleBin.Query();
            if (q is not { } r) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                bool changed = !Known || r.Count != Count || r.Bytes != Bytes;
                Known = true;
                Count = r.Count;
                Bytes = r.Bytes;
                if (changed) Changed?.Invoke();
            });
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        RecycleBin.Touched -= OnTouched;
        _timer.Dispose();
    }
}
