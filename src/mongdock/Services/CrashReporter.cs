using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Mongdock.Services;

/// <summary>
/// 오류 자동 신고 (#14) — 기록 쪽. 화면(다음 실행 때 묻는 카드 → 문제 신고 창)은 Views/CrashPrompt.
/// - 세션 표시: 시작 때 %APPDATA%\mongdock\cache\session.json(pid·시작 시각)을 쓰고 정상 종료(OnExit) 때 지운다.
///   다음 실행에 남아 있고 그 pid 가 이미 없으면 = 지난번 비정상 종료(강제 종료·FailFast·스택 넘침처럼 예외 처리기가 못 도는 경우 포함).
/// - 처리되지 않은 예외(UI 스레드·다른 스레드·관찰 안 된 Task): 마지막 하나를 cache\last-error.json 에 남김 (원문 — 보낼 때 가림).
/// 어느 스레드에서나 호출 가능, 실패해도 조용히 넘어감 (오류 기록 때문에 앱이 더 망가지면 안 됨).
/// </summary>
public static class CrashReporter
{
    private static readonly string Dir = Path.Combine(AppInfo.DataDirectory, "cache");
    private static readonly string SessionPath = Path.Combine(Dir, "session.json");
    private static readonly string ErrorPath = Path.Combine(Dir, "last-error.json");
    private static readonly string AskedPath = Path.Combine(Dir, "crash-asked.json");
    private static readonly object Gate = new();

    public sealed record ErrorRecord(string Kind, string Type, string Message, string Stack, DateTime Time, string Version)
    {
        /// <summary>같은 오류인지 가르는 값: 예외 종류 + 스택 첫 줄(몽독 코드 위치).</summary>
        public string Signature => Type + "|" + (Stack.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at ")) ?? "");
    }

    /// <summary>다음 실행 때 물어볼 것. Crashed = 비정상 종료, Error = 마지막 오류(없을 수 있음).</summary>
    public sealed record Pending(bool Crashed, DateTime? CrashSessionStart, ErrorRecord? Error)
    {
        public string Signature => Error?.Signature ?? "abnormal-exit";
    }

    /// <summary>
    /// 앱 시작 때 한 번 (단일 실행 확인 뒤): 지난 세션이 정상 종료됐는지 보고, 이번 세션 표시를 쓴다.
    /// 물어볼 게 있으면 돌려줌 (지난 기록은 그대로 두고, 물은 뒤 <see cref="Clear"/>).
    /// </summary>
    public static Pending? BeginSession()
    {
        Pending? pending = null;
        try
        {
            Directory.CreateDirectory(Dir);
            bool crashed = false;
            DateTime? started = null;
            if (File.Exists(SessionPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SessionPath));
                int pid = doc.RootElement.TryGetProperty("pid", out var p) ? p.GetInt32() : 0;
                if (doc.RootElement.TryGetProperty("started", out var s) && s.TryGetDateTime(out var st)) started = st;
                crashed = !IsSameProcessAlive(pid, started);
            }
            ErrorRecord? error = ReadError();
            if (crashed || error is not null) pending = new Pending(crashed, started, error);
            if (crashed) Log.Warn($"지난 실행이 정상 종료되지 않음 (시작 {started:yyyy-MM-dd HH:mm:ss})");

            var now = Process.GetCurrentProcess();
            AtomicFile.WriteAllText(SessionPath, JsonSerializer.Serialize(new { pid = Environment.ProcessId, started = now.StartTime }));
        }
        catch (Exception ex)
        {
            Log.Warn($"세션 표시 처리 실패: {ex.Message}");
        }
        return pending;
    }

    /// <summary>정상 종료 (OnExit 끝).</summary>
    public static void EndSession()
    {
        try { File.Delete(SessionPath); }
        catch { }
    }

    /// <summary>처리되지 않은 예외 기록. kind = "UI 스레드" / "다른 스레드(종료)" / "작업(Task)".</summary>
    public static void Record(string kind, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var rec = new ErrorRecord(kind, ex.GetType().FullName ?? ex.GetType().Name, ex.Message, ex.ToString(), DateTime.Now, ReportService.AppVersion());
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                AtomicFile.WriteAllText(ErrorPath, JsonSerializer.Serialize(rec));
            }
        }
        catch { }
    }

    /// <summary>물었으면(보냄·안 보냄) 기록을 비움 — 같은 일을 다시 묻지 않게.</summary>
    public static void Clear()
    {
        try { File.Delete(ErrorPath); }
        catch { }
    }

    /// <summary>같은 오류를 오늘 이미 물었는지. 아니면 오늘 물은 것으로 적음.</summary>
    public static bool AlreadyAskedToday(string signature)
    {
        try
        {
            var map = File.Exists(AskedPath)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(AskedPath)) ?? new()
                : new Dictionary<string, DateTime>();
            if (map.TryGetValue(signature, out var last) && last.Date == DateTime.Today) return true;
            map[signature] = DateTime.Now;
            foreach (var old in map.Where(kv => kv.Value < DateTime.Today.AddDays(-30)).Select(kv => kv.Key).ToList()) map.Remove(old);
            AtomicFile.WriteAllText(AskedPath, JsonSerializer.Serialize(map));
        }
        catch { }
        return false;
    }

    /// <summary>문제 신고 창 "함께 보낼 정보" 맨 앞에 붙일 오류 정보 (가리기 전 원문 — ReportWindow 가 줄마다 가림).</summary>
    public static string Describe(Pending p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[오류]");
        if (p.Crashed) sb.AppendLine($"지난 실행이 정상 종료되지 않음 (시작 {p.CrashSessionStart:yyyy-MM-dd HH:mm:ss})");
        if (p.Error is { } e)
        {
            sb.AppendLine($"{e.Kind} 예외 · {e.Time:yyyy-MM-dd HH:mm:ss} · v{e.Version}");
            // 스택은 길면 앞부분만 (진단 정보 전체 한도 안에서 로그 자리를 남기게)
            string stack = e.Stack.Length > 3000 ? e.Stack[..3000] + "\n…" : e.Stack;
            sb.AppendLine(stack.TrimEnd());
        }
        return sb.ToString();
    }

    private static ErrorRecord? ReadError()
    {
        try { return File.Exists(ErrorPath) ? JsonSerializer.Deserialize<ErrorRecord>(File.ReadAllText(ErrorPath)) : null; }
        catch { return null; }
    }

    /// <summary>그 pid 가 살아 있고 시작 시각도 같으면(= pid 재사용 아님) 다른 몽독이 아직 도는 것 → 비정상 종료 아님.</summary>
    private static bool IsSameProcessAlive(int pid, DateTime? started)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return started is null || Math.Abs((p.StartTime - started.Value).TotalSeconds) < 2;
        }
        catch { return false; }
    }

    // ───────────────────────── 시험용 ─────────────────────────

    /// <summary>
    /// 환경 변수 MONGDOCK_TEST_CRASH=ui|bg|task|exit 이면 시작 15초 뒤 일부러 오류를 낸다 (개발 빌드 시험용).
    /// ui = UI 스레드 예외(앱은 계속), bg = 다른 스레드 예외(프로세스 종료), task = 관찰 안 된 Task 예외, exit = FailFast(처리기 없이 종료).
    /// </summary>
    public static void ScheduleTestCrash(System.Windows.Threading.Dispatcher dispatcher)
    {
        string? mode = Environment.GetEnvironmentVariable("MONGDOCK_TEST_CRASH")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode)) return;
        Log.Warn($"시험용 오류 예약: {mode} (15초 뒤)");
        _ = Task.Delay(15_000).ContinueWith(_ =>
        {
            switch (mode)
            {
                case "ui":
                    dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException($@"시험용 UI 오류 C:\Users\{Environment.UserName}\secret.txt")));
                    break;
                case "bg":
                    new Thread(() => throw new InvalidOperationException("시험용 다른 스레드 오류")) { IsBackground = true }.Start();
                    break;
                case "task":
                    _ = Task.Run(() => throw new InvalidOperationException("시험용 Task 오류"));
                    Thread.Sleep(500);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    break;
                case "exit":
                    Environment.FailFast("시험용 즉시 종료");
                    break;
            }
        }, TaskScheduler.Default);
    }
}
