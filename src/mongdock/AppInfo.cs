using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Mongdock.Services;

namespace Mongdock;

/// <summary>제품 이름과 데이터 폴더. 이름이 MyDock → mongdock 으로 바뀐 이전 설치를 한 번 옮긴다.</summary>
public static class AppInfo
{
    public const string Name = "mongdock";

    private const string LegacyName = "MyDock";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// MS 스토어(MSIX) 패키지로 실행 중인지 (GetCurrentPackageFullName 이 패키지 이름을 돌려줌).
    /// 패키지면 시작 시 실행 = StartupTask(Services/PackagedStartupService), 자체 업데이트 없음(스토어가 업데이트).
    /// </summary>
    public static bool IsPackaged { get; } = DetectPackage();

    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

    private static bool DetectPackage()
    {
        try
        {
            uint length = 0;
            int result = GetCurrentPackageFullName(ref length, null);
            return result == ERROR_INSUFFICIENT_BUFFER || result == 0; // 패키지 없음 = APPMODEL_ERROR_NO_PACKAGE(15700)
        }
        catch (Exception)
        {
            return false; // API 없음 (Windows 8 미만)
        }
    }

    /// <summary>설치 방식 (경로 없이 — 신고 진단용).</summary>
    public static string InstallKind
    {
        get
        {
            if (IsPackaged) return "스토어 패키지";
            string inno = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
            return Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(inno, StringComparison.OrdinalIgnoreCase)
                ? "설치 프로그램" : "zip·개발 빌드";
        }
    }

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    private static string LegacyDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName);

    /// <summary>
    /// 첫 실행 때 %APPDATA%\MyDock 을 %APPDATA%\mongdock 으로 복사(원본은 백업으로 남김)하고,
    /// settings.json 안의 아이콘 경로와 시작 프로그램 등록을 새 이름으로 바꾼다. Log 를 쓰기 전에 호출해야 한다.
    /// </summary>
    public static void MigrateLegacyInstall()
    {
        string? note = null;
        try
        {
            if (!Directory.Exists(DataDirectory) && Directory.Exists(LegacyDataDirectory))
            {
                CopyDirectory(LegacyDataDirectory, DataDirectory);
                string settings = Path.Combine(DataDirectory, "settings.json");
                if (File.Exists(settings))
                {
                    // JSON 안의 경로는 \ 가 \\ 로 이스케이프되어 있다.
                    string legacyJson = LegacyDataDirectory.Replace(@"\", @"\\");
                    string newJson = DataDirectory.Replace(@"\", @"\\");
                    string text = File.ReadAllText(settings);
                    File.WriteAllText(settings, text.Replace(legacyJson, newJson, StringComparison.OrdinalIgnoreCase));
                }
                note = $"{LegacyDataDirectory} → {DataDirectory} 로 설정 복사 (원본은 남겨 둠)";
            }

            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(LegacyName) is string)
            {
                run.DeleteValue(LegacyName, throwOnMissingValue: false);
                // 스토어 패키지는 Run 키 대신 StartupTask (App 시작 때 PackagedStartupService.Adopt 가 이어받음)
                if (!IsPackaged) run.SetValue(Name, $"\"{Environment.ProcessPath}\"");
                note = (note is null ? "" : note + ", ") + "시작 프로그램 등록을 새 이름으로 변경";
            }
        }
        catch (Exception ex)
        {
            note = $"이전 설치 옮기기 실패: {ex.Message}";
        }

        if (note is not null) Log.Info(note);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (string dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
