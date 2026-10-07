using System.Runtime.InteropServices;
using System.Text;

namespace MyDock.Native;

/// <summary>
/// Windows 내장 SQLite (System32\winsqlite3.dll, Windows 10+) 최소 바인딩 — NuGet 없이 읽기 전용 조회용.
/// winsqlite3 는 __stdcall(WINAPI) 로 빌드돼 있음 → DllImport 기본 호출 규약(Winapi)과 같음. 오류 코드는 반환값으로 받으므로 SetLastError 불필요.
/// </summary>
internal static class Sqlite
{
    private const string Dll = "winsqlite3.dll";

    public const int SQLITE_OK = 0;
    public const int SQLITE_BUSY = 5;
    public const int SQLITE_LOCKED = 6;
    public const int SQLITE_ROW = 100;
    public const int SQLITE_DONE = 101;

    public const int SQLITE_OPEN_READONLY = 0x00000001;
    public const int SQLITE_OPEN_URI = 0x00000040;
    public const int SQLITE_OPEN_NOMUTEX = 0x00008000;

    public const int SQLITE_INTEGER = 1;
    public const int SQLITE_FLOAT = 2;
    public const int SQLITE_TEXT = 3;
    public const int SQLITE_BLOB = 4;
    public const int SQLITE_NULL = 5;

    [DllImport(Dll, EntryPoint = "sqlite3_open_v2", ExactSpelling = true)]
    public static extern int sqlite3_open_v2(byte[] filenameUtf8, out IntPtr db, int flags, IntPtr zVfs);

    [DllImport(Dll, EntryPoint = "sqlite3_close_v2", ExactSpelling = true)]
    public static extern int sqlite3_close_v2(IntPtr db);

    [DllImport(Dll, EntryPoint = "sqlite3_busy_timeout", ExactSpelling = true)]
    public static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport(Dll, EntryPoint = "sqlite3_prepare_v2", ExactSpelling = true)]
    public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sqlUtf8, int nByte, out IntPtr stmt, IntPtr pzTail);

    [DllImport(Dll, EntryPoint = "sqlite3_step", ExactSpelling = true)]
    public static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Dll, EntryPoint = "sqlite3_finalize", ExactSpelling = true)]
    public static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport(Dll, EntryPoint = "sqlite3_bind_int64", ExactSpelling = true)]
    public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DllImport(Dll, EntryPoint = "sqlite3_column_count", ExactSpelling = true)]
    public static extern int sqlite3_column_count(IntPtr stmt);

    [DllImport(Dll, EntryPoint = "sqlite3_column_type", ExactSpelling = true)]
    public static extern int sqlite3_column_type(IntPtr stmt, int col);

    [DllImport(Dll, EntryPoint = "sqlite3_column_int64", ExactSpelling = true)]
    public static extern long sqlite3_column_int64(IntPtr stmt, int col);

    [DllImport(Dll, EntryPoint = "sqlite3_column_text", ExactSpelling = true)]
    public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport(Dll, EntryPoint = "sqlite3_column_blob", ExactSpelling = true)]
    public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);

    [DllImport(Dll, EntryPoint = "sqlite3_column_bytes", ExactSpelling = true)]
    public static extern int sqlite3_column_bytes(IntPtr stmt, int col);

    [DllImport(Dll, EntryPoint = "sqlite3_column_name", ExactSpelling = true)]
    public static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);

    [DllImport(Dll, EntryPoint = "sqlite3_errmsg", ExactSpelling = true)]
    public static extern IntPtr sqlite3_errmsg(IntPtr db);

    public static byte[] Utf8Z(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    public static string? PtrToUtf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}

/// <summary>읽기 전용 SQLite 연결. 예외: <see cref="SqliteException"/>.</summary>
internal sealed class SqliteReader : IDisposable
{
    private IntPtr _db;

    private SqliteReader(IntPtr db) => _db = db;

    /// <summary>
    /// SQLITE_OPEN_READONLY 로 엶. WAL DB 면 -wal 내용까지 읽음 (-shm 에는 일반 리더처럼 읽기 표시만 — DB 내용은 절대 안 바뀜,
    /// 읽기 전용 연결은 체크포인트도 하지 않음).
    /// </summary>
    public static SqliteReader OpenReadOnly(string path, int busyTimeoutMs = 2000)
    {
        int rc = Sqlite.sqlite3_open_v2(Sqlite.Utf8Z(path), out IntPtr db,
            Sqlite.SQLITE_OPEN_READONLY | Sqlite.SQLITE_OPEN_NOMUTEX, IntPtr.Zero);
        if (rc != Sqlite.SQLITE_OK)
        {
            string msg = Sqlite.PtrToUtf8(Sqlite.sqlite3_errmsg(db)) ?? "open";
            if (db != IntPtr.Zero) Sqlite.sqlite3_close_v2(db);
            throw new SqliteException(rc, msg);
        }
        Sqlite.sqlite3_busy_timeout(db, busyTimeoutMs);
        return new SqliteReader(db);
    }

    /// <summary>쿼리 실행. 각 행마다 onRow(row) 호출. 파라미터는 ?1, ?2 … 로 정수만.</summary>
    public void Query(string sql, Action<SqliteRow> onRow, params long[] args)
    {
        if (_db == IntPtr.Zero) throw new ObjectDisposedException(nameof(SqliteReader));
        int rc = Sqlite.sqlite3_prepare_v2(_db, Sqlite.Utf8Z(sql), -1, out IntPtr stmt, IntPtr.Zero);
        if (rc != Sqlite.SQLITE_OK) throw new SqliteException(rc, Sqlite.PtrToUtf8(Sqlite.sqlite3_errmsg(_db)) ?? "prepare");
        try
        {
            for (int i = 0; i < args.Length; i++) Sqlite.sqlite3_bind_int64(stmt, i + 1, args[i]);
            var row = new SqliteRow(stmt);
            while (true)
            {
                rc = Sqlite.sqlite3_step(stmt);
                if (rc == Sqlite.SQLITE_ROW) { onRow(row); continue; }
                if (rc == Sqlite.SQLITE_DONE) break;
                throw new SqliteException(rc, Sqlite.PtrToUtf8(Sqlite.sqlite3_errmsg(_db)) ?? "step");
            }
        }
        finally
        {
            Sqlite.sqlite3_finalize(stmt);
        }
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero)
        {
            Sqlite.sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
    }
}

internal readonly struct SqliteRow
{
    private readonly IntPtr _stmt;
    public SqliteRow(IntPtr stmt) => _stmt = stmt;

    public int ColumnCount => Sqlite.sqlite3_column_count(_stmt);
    public string? ColumnName(int i) => Sqlite.PtrToUtf8(Sqlite.sqlite3_column_name(_stmt, i));
    public int Type(int i) => Sqlite.sqlite3_column_type(_stmt, i);
    public bool IsNull(int i) => Type(i) == Sqlite.SQLITE_NULL;
    public long Int64(int i) => Sqlite.sqlite3_column_int64(_stmt, i);

    public string? Text(int i)
    {
        if (IsNull(i)) return null;
        IntPtr p = Sqlite.sqlite3_column_text(_stmt, i);
        int n = Sqlite.sqlite3_column_bytes(_stmt, i);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p, n);
    }

    public byte[]? Blob(int i)
    {
        if (IsNull(i)) return null;
        IntPtr p = Sqlite.sqlite3_column_blob(_stmt, i);
        int n = Sqlite.sqlite3_column_bytes(_stmt, i);
        if (p == IntPtr.Zero || n <= 0) return Array.Empty<byte>();
        var buf = new byte[n];
        Marshal.Copy(p, buf, 0, n);
        return buf;
    }
}

internal sealed class SqliteException : Exception
{
    public int Code { get; }
    public SqliteException(int code, string message) : base($"SQLite {code}: {message}") => Code = code;
}
