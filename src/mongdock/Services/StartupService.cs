using Microsoft.Win32;

namespace Mongdock.Services;

/// <summary>일반판(설치 프로그램·zip): HKCU\Software\Microsoft\Windows\CurrentVersion\Run 의 "mongdock" 값으로 시작 프로그램 등록.</summary>
public sealed class StartupService : IStartupService
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;

    /// <param name="valueName">Run 값 이름 (시험 도구만 바꿈).</param>
    public StartupService(string valueName = AppInfo.Name) => _valueName = valueName;

    public event EventHandler? Changed;

    public StartupState State => IsEnabled ? StartupState.Enabled : StartupState.Disabled;

    public bool CanChange => true;

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(_valueName) is string s && s.Length > 0;
            }
            catch (Exception ex)
            {
                Log.Error("시작 프로그램 상태 조회 실패", ex);
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    Log.Error("시작 프로그램 등록 실패: 실행 파일 경로를 알 수 없음");
                    return;
                }
                key.SetValue(_valueName, $"\"{exe}\"", RegistryValueKind.String);
                Log.Info($"시작 프로그램 등록: {exe}");
            }
            else
            {
                key.DeleteValue(_valueName, throwOnMissingValue: false);
                Log.Info("시작 프로그램 해제");
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("시작 프로그램 설정 실패", ex);
        }
    }
}
