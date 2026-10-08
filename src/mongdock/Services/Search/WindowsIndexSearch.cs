using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Mongdock.Services.Search;

/// <summary>파일 검색 결과 분류 (설정의 카테고리 토글과 1:1).</summary>
public enum FileCategory { Folder, Document, Media, Other }

/// <summary>색인에서 찾은 파일·폴더 하나.</summary>
/// <param name="Path">실제 파일 시스템 경로(System.ItemUrl) — 열기·폴더에서 보기·아이콘용.</param>
/// <param name="DisplayPath">화면 표시용 경로(System.ItemPathDisplay). 한국어 윈도우는 "C:\사용자\…\다운로드" 처럼 현지화돼
/// 실제 경로가 아님 — 이걸로 탐색기를 열면 경로를 못 찾고 홈이 열린다.</param>
public sealed record IndexedFile(string Path, string Name, FileCategory Category, string DisplayPath);

/// <summary>
/// 윈도우 검색 색인(Windows Search, SystemIndex)에서 파일 이름으로 찾기.
/// NuGet 없이 윈도우 내장 ADO(ADODB.Connection, Search.CollatorDSO 공급자)를 late-bound(dynamic)로 사용.
/// - 이름 일치: CONTAINS(System.FileName, '"단어*"') = 단어 앞부분 일치(색인 사용, 수십 ms).
///   한 건도 없으면 LIKE '%q%'(부분 일치, 느릴 수 있음)로 한 번 더 — 2글자 이상일 때만.
/// - 카테고리마다 따로 TOP N 쿼리 (흔한 종류가 다른 종류를 밀어내지 않게), 최근 수정순.
/// - 색인 서비스(WSearch)가 꺼져 있거나 쿼리가 실패하면 빈 결과 + 로그 (호출 쪽은 카테고리만 생략).
/// 쿼리는 백그라운드 스레드에서만 호출 (ADO 공급자는 Both 스레딩 모델이라 MTA 스레드 풀에서 동작).
/// </summary>
public static class WindowsIndexSearch
{
    private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";
    /// <summary>같은 오류 로그를 검색할 때마다 반복하지 않게.</summary>
    private static string? _lastError;
    /// <summary>쿼리는 한 번에 하나 (빠르게 타이핑할 때 느린 LIKE 쿼리가 쌓이지 않게 — 기다리는 동안 취소되면 건너뜀).</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// 이름에 query 가 들어간 파일·폴더. categories 에 든 분류만, 분류마다 최대 maxPerCategory 개.
    /// 결과 순서 = 분류 순서(폴더·문서·미디어·기타) → 최근 수정순.
    /// </summary>
    public static async Task<IReadOnlyList<IndexedFile>> SearchAsync(string query, IReadOnlyList<string> folders,
        IReadOnlyCollection<FileCategory> categories, int maxPerCategory, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0 || categories.Count == 0 || folders.Count == 0) return Array.Empty<IndexedFile>();
        string? scope = ScopeClause(folders);
        if (scope is null) return Array.Empty<IndexedFile>();

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() => Run(query, scope, categories, maxPerCategory, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IReadOnlyList<IndexedFile> Run(string query, string scope, IReadOnlyCollection<FileCategory> categories, int max, CancellationToken ct)
    {
        if (!IsIndexServiceRunning())
        {
            LogOnce("윈도우 검색 색인 서비스(WSearch)가 실행 중이 아니어서 파일 검색 생략");
            return Array.Empty<IndexedFile>();
        }

        dynamic? conn = null;
        try
        {
            var type = Type.GetTypeFromProgID("ADODB.Connection");
            if (type is null)
            {
                LogOnce("ADODB.Connection 이 없어 파일 검색 생략");
                return Array.Empty<IndexedFile>();
            }
            conn = Activator.CreateInstance(type);
            if (conn is null) return Array.Empty<IndexedFile>();
            conn.Open(ConnectionString);

            var results = new List<IndexedFile>();
            string? contains = ContainsClause(query);
            if (contains is not null)
            {
                foreach (var cat in Ordered(categories))
                {
                    ct.ThrowIfCancellationRequested();
                    results.AddRange((List<IndexedFile>)Query(conn, $"{scope} AND {contains} AND {CategoryClause(cat)}", max, cat));
                }
            }
            // 단어 앞부분으로 하나도 못 찾았으면 부분 일치로 (예 "보고" → "주간보고서")
            // LIKE 는 색인을 못 타 느리므로 쿼리 한 번(분류는 받아 온 System.Kind 로 직접)만.
            // 와일드카드 문자(% _ [)가 든 검색어는 이스케이프한 LIKE 가 수십 초 걸려(실측) 생략.
            if (results.Count == 0 && query.Length >= 2 && query.IndexOfAny(new[] { '%', '_', '[' }) < 0)
            {
                ct.ThrowIfCancellationRequested();
                string like = $"System.FileName LIKE '%{EscapeLike(query)}%'";
                List<IndexedFile> found = Query(conn, $"{scope} AND {like}", max * 4, null);
                foreach (var cat in Ordered(categories))
                    results.AddRange(found.Where(f => f.Category == cat).Take(max));
            }
            _lastError = null;
            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogOnce($"윈도우 검색 색인 쿼리 실패: {ex.GetType().Name} {ex.Message}");
            return Array.Empty<IndexedFile>();
        }
        finally
        {
            try { conn?.Close(); } catch { /* 이미 닫힘 */ }
            if (conn is not null && Marshal.IsComObject(conn)) Marshal.ReleaseComObject(conn);
        }
    }

    private static IEnumerable<FileCategory> Ordered(IReadOnlyCollection<FileCategory> categories)
        => new[] { FileCategory.Folder, FileCategory.Document, FileCategory.Media, FileCategory.Other }.Where(categories.Contains);

    /// <summary>cat 이 null 이면 System.ItemType·System.Kind 를 같이 받아 분류.</summary>
    private static List<IndexedFile> Query(dynamic conn, string where, int max, FileCategory? cat)
    {
        string sql = $"SELECT TOP {Math.Clamp(max, 1, 50)} System.ItemPathDisplay, System.ItemNameDisplay, System.ItemType, System.Kind, System.ItemUrl FROM SystemIndex WHERE {where} ORDER BY System.DateModified DESC";
        var list = new List<IndexedFile>();
        dynamic? rs = null;
        try
        {
            var type = Type.GetTypeFromProgID("ADODB.Recordset");
            if (type is null) return list;
            rs = Activator.CreateInstance(type);
            if (rs is null) return list;
            rs.Open(sql, conn);
            while (!(bool)rs.EOF)
            {
                string? display = rs.Fields.Item(0).Value as string;
                string? name = rs.Fields.Item(1).Value as string;
                string? path = PathFromItemUrl(rs.Fields.Item(4).Value as string) ?? display;
                if (!string.IsNullOrEmpty(path))
                {
                    FileCategory c = cat ?? Classify(rs.Fields.Item(2).Value as string, Kinds((object?)rs.Fields.Item(3).Value));
                    list.Add(new IndexedFile(path, string.IsNullOrEmpty(name) ? Path.GetFileName(path) : name, c, display ?? path));
                }
                rs.MoveNext();
            }
            rs.Close();
        }
        finally
        {
            if (rs is not null && Marshal.IsComObject(rs)) Marshal.ReleaseComObject(rs);
        }
        return list;
    }

    /// <summary>System.ItemUrl("file:C:/Users/x/Downloads/a.txt", "file:///C:/…", "file://server/share/…") → 실제 경로. 파일이 아니면 null.</summary>
    internal static string? PathFromItemUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        string rest = url[5..].TrimStart('/');
        if (rest.Length < 2) return null;
        if (rest.Contains('%')) rest = Uri.UnescapeDataString(rest); // 공백 등이 %20 으로 오는 경우 (드묾)
        string p = rest.Replace('/', '\\');
        bool drive = p.Length > 1 && p[1] == ':';
        return drive ? p : @"\\" + p; // 드라이브 문자가 없으면 UNC
    }

    /// <summary>
    /// System.Kind 값 → 문자열 배열. ADO 는 여러 값 속성을 VARIANT SAFEARRAY 로 주므로 COM 마샬링 결과는 보통
    /// object[](요소가 string) — string[] 로 오는 경우·단일 문자열·DBNull 도 처리.
    /// </summary>
    internal static string[]? Kinds(object? value) => value switch
    {
        string[] a => a,
        string s => new[] { s },
        object[] o => o.OfType<string>().ToArray(),
        Array arr => arr.Cast<object?>().OfType<string>().ToArray(),
        _ => null, // DBNull·null
    };

    /// <summary>CategoryClause 와 같은 규칙을 코드로.</summary>
    private static FileCategory Classify(string? itemType, string[]? kinds)
    {
        if (string.Equals(itemType, "Directory", StringComparison.OrdinalIgnoreCase)) return FileCategory.Folder;
        if (kinds is null) return FileCategory.Other;
        if (kinds.Contains("document", StringComparer.OrdinalIgnoreCase)) return FileCategory.Document;
        if (kinds.Any(k => k is "picture" or "video" or "music")) return FileCategory.Media;
        return FileCategory.Other;
    }

    // ───────────────────────── SQL 조각 (인젝션 방지) ─────────────────────────

    /// <summary>System.Kind 는 여러 값 속성 — "= 'x'" 는 값 중 하나라도 x 면 참. 폴더는 ItemType 으로 구분 (압축 파일도 Kind=folder 라서).</summary>
    private static string CategoryClause(FileCategory cat) => cat switch
    {
        FileCategory.Folder => "System.ItemType = 'Directory'",
        FileCategory.Document => "System.ItemType <> 'Directory' AND System.Kind = 'document'",
        FileCategory.Media => "System.ItemType <> 'Directory' AND (System.Kind = 'picture' OR System.Kind = 'video' OR System.Kind = 'music')",
        _ => "System.ItemType <> 'Directory' AND NOT System.Kind = 'document' AND NOT System.Kind = 'picture' AND NOT System.Kind = 'video' AND NOT System.Kind = 'music'",
    };

    /// <summary>(SCOPE='file:A' OR SCOPE='file:B'). 존재하지 않는 폴더는 뺌. 없으면 null.</summary>
    private static string? ScopeClause(IReadOnlyList<string> folders)
    {
        var parts = new List<string>();
        foreach (string f in folders)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            string full;
            try { full = Path.GetFullPath(f.Trim()); }
            catch { continue; }
            if (!Directory.Exists(full)) continue;
            parts.Add($"SCOPE='file:{EscapeString(full.TrimEnd('\\'))}'");
        }
        if (parts.Count == 0) return null;
        return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
    }

    /// <summary>
    /// 띄어쓰기로 나눈 단어마다 앞부분 일치, 모두 AND: CONTAINS(System.FileName, '"보고*" AND "주간*"').
    /// 큰따옴표·별표는 구문을 깨므로 지우고, 작은따옴표는 SQL 문자열 규칙대로 두 번. 남는 단어가 없으면 null.
    /// </summary>
    private static string? ContainsClause(string query)
    {
        var words = query.Split(' ', '\t')
            .Select(w => new string(w.Where(c => c is not ('"' or '*') && !char.IsControl(c)).ToArray()))
            .Where(w => w.Any(char.IsLetterOrDigit))
            .Take(6)
            .ToList();
        if (words.Count == 0) return null;
        string terms = string.Join(" AND ", words.Select(w => $"\"{EscapeString(w)}*\""));
        return $"CONTAINS(System.FileName, '{terms}')";
    }

    /// <summary>SQL 문자열 리터럴 안: 작은따옴표를 두 번.</summary>
    private static string EscapeString(string s) => s.Replace("'", "''");

    /// <summary>LIKE 패턴 안: 작은따옴표 두 번 + 와일드카드 문자(% _ [)는 [x] 로 감싸 글자 그대로.</summary>
    private static string EscapeLike(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '\'') sb.Append("''");
            else if (c is '%' or '_' or '[') sb.Append('[').Append(c).Append(']');
            else if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static void LogOnce(string message)
    {
        if (message == _lastError) return;
        _lastError = message;
        Log.Warn(message);
    }

    // ───────────────────────── 색인 서비스 상태 ─────────────────────────

    /// <summary>윈도우 검색 색인 서비스(WSearch)가 실행 중인지. 확인 자체가 실패하면 true (쿼리에 맡김).</summary>
    public static bool IsIndexServiceRunning()
    {
        IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return true;
            svc = OpenService(scm, "WSearch", SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return Marshal.GetLastWin32Error() != ERROR_SERVICE_DOES_NOT_EXIST;
            if (!QueryServiceStatus(svc, out SERVICE_STATUS status)) return true;
            return status.dwCurrentState == SERVICE_RUNNING;
        }
        catch
        {
            return true;
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_RUNNING = 0x00000004;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
