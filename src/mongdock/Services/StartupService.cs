using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Mongdock.Services;

/// <summary>
/// "윈도우 시작 시 실행". 기본은 작업 스케줄러의 현재 사용자 전용 로그온 작업(<see cref="TaskName"/>) —
/// HKCU Run 키는 윈도우가 로그온 후 수십 초 늦게(시작 지연 + 다른 시작 앱과 경쟁) 실행하지만 로그온 트리거는 바로 실행된다.
/// 작업 등록이 안 되면(회사 정책 등) 예전처럼 HKCU\...\Run 의 "mongdock" 값으로 대체. 둘 중 하나라도 있으면 켜짐.
/// 작업 스케줄러는 COM(Schedule.Service) late-bound 로 쓴다 (외부 의존성 없음, 관리자 권한 불필요: 최소 권한 + 대화형 토큰).
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppInfo.Name;
    /// <summary>작업 스케줄러가 실행할 때 붙이는 인수 — 탐색기(작업 표시줄)가 뜰 때까지 잠깐 기다리게 (App.OnStartup).</summary>
    public const string AutoStartArgument = "--autostart";

    // 작업 스케줄러 상수
    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
    private const int HrFileNotFound = unchecked((int)0x80070002);
    private const int HrPathNotFound = unchecked((int)0x80070003);
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static readonly object Gate = new();

    /// <summary>
    /// 작업 이름 (루트 폴더). 작업 이름은 PC 전체에서 하나라 사용자 이름을 붙여 계정마다 따로 둔다 (예 "mongdock-melon").
    /// 설치 프로그램(installer/mongdock.iss)도 같은 규칙('mongdock-' + GetUserNameString)으로 지운다.
    /// </summary>
    public static string TaskName { get; } = $"{AppInfo.Name}-{SanitizeTaskName(Environment.UserName)}";

    public bool IsEnabled
    {
        get
        {
            try { return IsRegistered(); }
            catch (Exception ex)
            {
                Log.Error("시작 프로그램 상태 조회 실패", ex);
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (Gate)
        {
            if (enabled) Enable(CurrentExe());
            else Disable();
        }
    }

    /// <summary>작업(사용 중) 또는 Run 값이 있으면 true. 조회 실패는 예외로 (호출한 쪽이 판단).</summary>
    public static bool IsRegistered()
    {
        if (ReadRunValue() is not null) return true;
        var task = QueryTask();
        return task is { Enabled: true };
    }

    /// <summary>
    /// 기존 사용자 이관·경로 갱신 (시작할 때 백그라운드에서 한 번). 켜져 있지 않으면 아무것도 안 한다.
    /// - Run 값만 있으면 → 작업으로 옮기고 Run 값 삭제 (실행 대상은 Run 값의 exe 가 있으면 그것, 없으면 지금 exe).
    /// - 작업이 가리키는 exe 가 없어졌으면(설치 위치가 바뀜) → 지금 exe 로 다시 등록.
    /// 작업 등록이 안 되면 Run 값을 그대로 둔다 (다음 시작 때 다시 시도).
    /// </summary>
    public static void MigrateInBackground()
    {
        Task.Run(() =>
        {
            try { Migrate(); }
            catch (Exception ex) { Log.Error("시작 프로그램 이관 실패", ex); }
        });
    }

    private static void Migrate()
    {
        lock (Gate)
        {
            string? run = ReadRunValue();
            TaskInfo? task = QueryTask();
            if (run is null && task is not { Enabled: true }) return; // 꺼져 있음 (사용자가 작업 스케줄러에서 끈 작업도 존중)

            string? taskExe = task?.Command;
            bool taskOk = task is { Enabled: true } && !string.IsNullOrEmpty(taskExe) && File.Exists(taskExe);
            if (taskOk)
            {
                if (run is not null)
                {
                    DeleteRunValue();
                    Log.Info($"시작 프로그램: 작업 '{TaskName}' 이 있어 Run 값 정리 (두 번 실행 방지)");
                }
                return;
            }

            string? runExe = ExeFromCommandLine(run);
            string exe = runExe is not null && File.Exists(runExe) ? runExe : CurrentExe() ?? "";
            if (exe.Length == 0) return;
            if (TryRegisterTask(exe))
            {
                DeleteRunValue();
                Log.Info(run is not null
                    ? $"시작 프로그램: Run 키 → 작업 스케줄러 '{TaskName}' 로 이관 ({exe})"
                    : $"시작 프로그램: 작업 실행 경로 갱신 ({taskExe} → {exe})");
            }
            else if (run is null)
            {
                // 작업이 깨졌고 다시 등록도 안 됨 → Run 키로라도 켜 둠
                WriteRunValue(exe);
            }
        }
    }

    /// <summary>설치 프로그램용 (mongdock.exe --register-startup): 지금 exe 로 켬. 성공하면 true.</summary>
    public static bool RegisterFromCommandLine()
    {
        lock (Gate) return Enable(CurrentExe());
    }

    /// <summary>설치 프로그램용 (mongdock.exe --unregister-startup): 작업과 Run 값 모두 삭제.</summary>
    public static void UnregisterFromCommandLine()
    {
        lock (Gate) Disable();
    }

    // ── 켜기 / 끄기 ──

    private static bool Enable(string? exe)
    {
        if (string.IsNullOrEmpty(exe))
        {
            Log.Error("시작 프로그램 등록 실패: 실행 파일 경로를 알 수 없음");
            return false;
        }
        if (TryRegisterTask(exe))
        {
            // 작업과 Run 키가 둘 다 있으면 두 번 실행되고, 두 번째 실행은 첫 인스턴스의 일시 정지를 풀어 버림 → 하나만
            DeleteRunValue();
            Log.Info($"시작 프로그램 등록 (작업 스케줄러 '{TaskName}'): {exe}");
            return true;
        }
        bool ok = WriteRunValue(exe);
        if (ok) Log.Info($"시작 프로그램 등록 (Run 키로 대체): {exe}");
        return ok;
    }

    private static void Disable()
    {
        bool task = TryDeleteTask();
        DeleteRunValue();
        Log.Info(task ? "시작 프로그램 해제 (작업·Run 키)" : "시작 프로그램 해제 (Run 키 — 작업은 지우지 못함)");
    }

    // ── 작업 스케줄러 (COM late-bound) ──

    private sealed record TaskInfo(bool Enabled, string? Command);

    /// <summary>작업이 없으면 null. 작업 스케줄러에 접근 못 하면 null (로그).</summary>
    private static TaskInfo? QueryTask()
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = ConnectService();
            if (service is null) return null;
            folder = Invoke(service, "GetFolder", @"\");
            try { task = Invoke(folder!, "GetTask", @"\" + TaskName); }
            catch (Exception ex) when (IsNotFound(ex)) { return null; }
            bool enabled = Get(task!, "Enabled") is true;
            string? command = null;
            if (Get(task!, "Xml") is string xml)
            {
                var exec = XDocument.Parse(xml).Root?.Element(TaskNs + "Actions")?.Element(TaskNs + "Exec");
                command = exec?.Element(TaskNs + "Command")?.Value.Trim().Trim('"');
                if (command is not null) command = Environment.ExpandEnvironmentVariables(command);
            }
            return new TaskInfo(enabled, command);
        }
        catch (Exception ex)
        {
            Log.Warn($"시작 작업 조회 실패: {Unwrap(ex).Message}");
            return null;
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    private static bool TryRegisterTask(string exe)
    {
        object? service = null, folder = null, registered = null;
        try
        {
            service = ConnectService();
            if (service is null) return false;
            folder = Invoke(service, "GetFolder", @"\");
            registered = Invoke(folder!, "RegisterTask",
                TaskName, BuildTaskXml(exe), TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
            return registered is not null;
        }
        catch (Exception ex)
        {
            Log.Warn($"작업 스케줄러 등록 실패 → Run 키로 대체: {Unwrap(ex).Message}");
            return false;
        }
        finally
        {
            Release(registered);
            Release(folder);
            Release(service);
        }
    }

    /// <summary>작업 삭제. 없으면 성공으로 본다.</summary>
    private static bool TryDeleteTask()
    {
        object? service = null, folder = null;
        try
        {
            service = ConnectService();
            if (service is null) return false;
            folder = Invoke(service, "GetFolder", @"\");
            try { Invoke(folder!, "DeleteTask", TaskName, 0); }
            catch (Exception ex) when (IsNotFound(ex)) { }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"시작 작업 삭제 실패: {Unwrap(ex).Message}");
            return false;
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    private static object? ConnectService()
    {
        Type? type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null)
        {
            Log.Warn("작업 스케줄러(Schedule.Service)를 찾지 못함");
            return null;
        }
        object service = Activator.CreateInstance(type)!;
        try
        {
            Invoke(service, "Connect");
            return service;
        }
        catch
        {
            Release(service);
            throw;
        }
    }

    /// <summary>
    /// 현재 사용자 로그온 시 바로 실행, 최소 권한, 배터리여도 시작·유지, 시간 제한 없음, 중복 실행 무시,
    /// 우선순위 4(보통 — 기본 7 은 CPU·IO 우선순위가 낮음), 지연 없음, 놓친 실행 나중에 하지 않음.
    /// </summary>
    internal static string BuildTaskXml(string exe)
    {
        string user = SecurityElement.Escape(CurrentUserName());
        string command = SecurityElement.Escape(exe);
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>{AppInfo.Name}</Author>
                <Description>mongdock (몽독) 을 로그인할 때 바로 실행합니다. 몽독 설정의 "윈도우 시작 시 실행" 으로 켜고 끕니다.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{AutoStartArgument}</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string CurrentUserName()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.Name; // DOMAIN\user (로컬 계정은 PC이름\user)
        }
        catch
        {
            return $@"{Environment.UserDomainName}\{Environment.UserName}";
        }
    }

    private static string SanitizeTaskName(string name)
    {
        var chars = name.Select(c => c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c);
        string s = new string(chars.ToArray()).Trim();
        return s.Length == 0 ? "user" : s;
    }

    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            try { Marshal.FinalReleaseComObject(com); } catch { }
        }
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private static bool IsNotFound(Exception ex) => Unwrap(ex).HResult is HrFileNotFound or HrPathNotFound;

    // ── Run 키 ──

    private static string? ReadRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string s && s.Length > 0 ? s : null;
    }

    private static bool WriteRunValue(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("시작 프로그램(Run 키) 등록 실패", ex);
            return false;
        }
    }

    private static void DeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("시작 프로그램(Run 키) 삭제 실패", ex);
        }
    }

    /// <summary>Run 값 ("C:\...\mongdock.exe" 인수...) 에서 exe 경로만.</summary>
    private static string? ExeFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        string s = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? s[..(exe + 4)] : s;
    }

    private static string? CurrentExe() => Environment.ProcessPath;
}
