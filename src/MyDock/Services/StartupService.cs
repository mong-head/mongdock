using Microsoft.Win32;

namespace MyDock.Services;

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run 의 "MyDock" 값으로 시작 프로그램 등록.</summary>
public sealed class StartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MyDock";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string s && s.Length > 0;
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
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
                Log.Info($"시작 프로그램 등록: {exe}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("시작 프로그램 해제");
            }
        }
        catch (Exception ex)
        {
            Log.Error("시작 프로그램 설정 실패", ex);
        }
    }
}
