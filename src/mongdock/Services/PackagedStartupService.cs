using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Mongdock.Services;

/// <summary>
/// 스토어판(MSIX): 매니페스트의 windows.startupTask(TaskId <see cref="TaskId"/>, 처음엔 꺼짐)로 로그인 시 자동 실행.
/// - 사용자가 윈도우 설정 → 시작 앱(또는 작업 관리자)에서 끄면 DisabledByUser — 앱은 다시 켤 수 없고 그 화면으로 안내만.
/// - 일반판에서 옮겨 온 사용자: Run 키의 일반판 값이 남아 있으면 두 개가 같이 뜨므로 지우고 StartupTask 로 이어받음(<see cref="Adopt"/>).
/// </summary>
public sealed class PackagedStartupService : IStartupService
{
    /// <summary>tools/msix/AppxManifest.xml 의 desktop:StartupTask TaskId 와 같아야 함.</summary>
    public const string TaskId = "mongdockStartup";

    private StartupTask? _task;
    private bool _busy;

    public event EventHandler? Changed;

    public PackagedStartupService()
    {
        try
        {
            // 패키지 정보만 읽는 짧은 호출 — UI 스레드를 막지 않게 스레드 풀에서 기다림
            _task = Task.Run(async () => await StartupTask.GetAsync(TaskId)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error($"StartupTask '{TaskId}' 를 찾지 못함 (매니페스트 확인)", ex);
        }
    }

    public StartupState State
    {
        get
        {
            if (_task is null) return StartupState.Disabled;
            try
            {
                return _task.State switch
                {
                    StartupTaskState.Enabled => StartupState.Enabled,
                    StartupTaskState.DisabledByUser => StartupState.DisabledByUser,
                    StartupTaskState.DisabledByPolicy => StartupState.DisabledByPolicy,
                    StartupTaskState.EnabledByPolicy => StartupState.EnabledByPolicy,
                    _ => StartupState.Disabled,
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"StartupTask 상태 조회 실패: {ex.Message}");
                return StartupState.Disabled;
            }
        }
    }

    public bool IsEnabled => State is StartupState.Enabled or StartupState.EnabledByPolicy;

    public bool CanChange => _task is not null && State is StartupState.Enabled or StartupState.Disabled;

    public void SetEnabled(bool enabled)
    {
        if (_task is null || _busy) return;
        if (!enabled)
        {
            try
            {
                if (State == StartupState.Enabled) _task.Disable();
                Log.Info("시작 앱(StartupTask) 끔");
            }
            catch (Exception ex)
            {
                Log.Error("시작 앱(StartupTask) 끄기 실패", ex);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        _ = EnableAsync();
    }

    private async Task EnableAsync()
    {
        if (_task is null) return;
        _busy = true;
        try
        {
            var state = await _task.RequestEnableAsync();
            Log.Info($"시작 앱(StartupTask) 켜기 요청 → {state}");
        }
        catch (Exception ex)
        {
            Log.Error("시작 앱(StartupTask) 켜기 실패", ex);
        }
        finally
        {
            _busy = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 앱 시작 때 한 번: 일반판이 남긴 Run 값(WindowsApps 밖 경로)을 지우고 그 뜻(또는 settings.StartWithWindows)을 StartupTask 로 이어받는다.
    /// StartupTask 가 꺼져 있고(사용자·정책이 끈 게 아니고) 사용자 설정이 켜짐이면 켬. 바꿨으면 true (설정 저장 필요).
    /// </summary>
    public bool Adopt(Models.Settings settings)
    {
        bool changed = false;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(StartupService.RunKey, writable: true);
            if (run?.GetValue(AppInfo.Name) is string old && !old.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                run.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
                Log.Info("일반판 시작 프로그램(Run 키) 등록을 지우고 시작 앱(StartupTask)으로 이어받음");
                if (!settings.StartWithWindows) { settings.StartWithWindows = true; changed = true; }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"일반판 Run 키 정리 실패: {ex.Message}");
        }

        if (settings.StartWithWindows && State == StartupState.Disabled) SetEnabled(true);
        return changed;
    }
}
