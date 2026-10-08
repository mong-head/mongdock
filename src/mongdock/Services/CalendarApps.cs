using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 시계 달력의 "캘린더에서 열기" 대상 (TopBar.CalendarApp).
/// - 웹(항상 사용 가능): Google·Outlook(outlook.live.com)·네이버 — 기본 브라우저로 그 날짜 보기
/// - 데스크톱(설치 감지): 새 Outlook(패키지), 클래식 Outlook(App Paths), 윈도우 "메일 및 일정"(지원 종료, 감지될 때만)
/// 감지는 설정 창의 드롭다운을 펼칠 때·열기 직전에만 (AppsFolder 60초 캐시 + 레지스트리 한 번이라 가벼움).
/// </summary>
public static class CalendarApps
{
    public const string NewOutlookFamily = "Microsoft.OutlookForWindows_8wekyb3d8bbwe";
    public const string MailCalendarFamily = "microsoft.windowscommunicationsapps_8wekyb3d8bbwe";
    /// <summary>"메일 및 일정" 패키지 안의 일정 앱 (매니페스트 Application Id = microsoft.windowslive.calendar).</summary>
    public const string MailCalendarAumid = MailCalendarFamily + "!microsoft.windowslive.calendar";
    /// <summary>Microsoft Store 의 새 Outlook (Outlook for Windows, 제품 ID 9NRX63209R7B).</summary>
    public const string NewOutlookStoreUri = "ms-windows-store://pdp/?productid=9NRX63209R7B";

    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\outlook.exe";

    /// <summary>설정 드롭다운에 보일 순서.</summary>
    public static readonly CalendarApp[] All =
    {
        CalendarApp.Google, CalendarApp.OutlookWeb, CalendarApp.Naver,
        CalendarApp.NewOutlook, CalendarApp.ClassicOutlook, CalendarApp.WindowsCalendar,
    };

    public static bool IsWeb(CalendarApp app) => app is CalendarApp.Google or CalendarApp.OutlookWeb or CalendarApp.Naver;

    public static string DisplayName(CalendarApp app) => app switch
    {
        CalendarApp.OutlookWeb => "Outlook (웹)",
        CalendarApp.Naver => "네이버 캘린더 (웹)",
        CalendarApp.NewOutlook => "새 Outlook",
        CalendarApp.ClassicOutlook => "Outlook (클래식)",
        CalendarApp.WindowsCalendar => "메일 및 일정",
        _ => "Google 캘린더 (웹)",
    };

    /// <summary>설치 안 됐을 때 받을 수 있는 스토어 주소 (확인된 것만). 없으면 null.</summary>
    public static string? InstallUri(CalendarApp app) => app == CalendarApp.NewOutlook ? NewOutlookStoreUri : null;

    /// <summary>웹은 항상 true, 데스크톱 앱은 설치 감지.</summary>
    public static bool IsInstalled(CalendarApp app)
    {
        try
        {
            return app switch
            {
                CalendarApp.NewOutlook => AppsFolder.FindAumidByFamily(NewOutlookFamily) != null,
                CalendarApp.ClassicOutlook => ClassicOutlookPath() != null,
                CalendarApp.WindowsCalendar => AppsFolder.FindAumidByFamily(MailCalendarFamily) != null,
                _ => true,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"캘린더 앱 감지 실패 ({app}): {ex.Message}");
            return false;
        }
    }

    /// <summary>클래식 Outlook(outlook.exe) 경로 — App Paths (HKCU 먼저, 그다음 HKLM). 파일이 없으면 null.</summary>
    public static string? ClassicOutlookPath()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(AppPathsKey, writable: false);
            if (key?.GetValue(null) is string path)
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (path.Length > 0 && File.Exists(path)) return path;
            }
        }
        return null;
    }

    /// <summary>
    /// 그 날짜를 연다. 고른 데스크톱 앱이 지워졌으면 Google 웹으로 대신 열고 Log.
    /// 데스크톱 앱은 날짜 지정 실행 방법이 없어 캘린더 화면만 연다.
    /// </summary>
    public static void Open(CalendarApp app, DateTime date)
    {
        if (!IsWeb(app) && !IsInstalled(app))
        {
            Log.Warn($"캘린더 앱 '{app}' 이 설치돼 있지 않아 Google 캘린더(웹)로 엽니다");
            app = CalendarApp.Google;
        }
        switch (app)
        {
            case CalendarApp.NewOutlook:
                StartAumid(AppsFolder.FindAumidByFamily(NewOutlookFamily)!);
                break;
            case CalendarApp.ClassicOutlook:
                // /select outlook:calendar = 캘린더 폴더로 열기 (이미 켜져 있으면 그 창이 캘린더로)
                using (Process.Start(new ProcessStartInfo(ClassicOutlookPath()!, "/select outlook:calendar") { UseShellExecute = true })) { }
                break;
            case CalendarApp.WindowsCalendar:
                StartAumid(MailCalendarAumid); // 같은 패키지의 메일이 아니라 일정 앱
                break;
            default:
                using (Process.Start(new ProcessStartInfo(WebUrl(app, date)) { UseShellExecute = true })) { }
                break;
        }
    }

    /// <summary>
    /// 웹 캘린더의 "그 날 보기" 주소.
    /// - Google: /calendar/r/day/YYYY/M/D
    /// - Outlook(개인 계정 웹): /calendar/0/view/day/YYYY/M/D
    /// - 네이버: 날짜를 지정하는 공개 URL 이 없어 기본 페이지
    /// </summary>
    public static string WebUrl(CalendarApp app, DateTime d)
    {
        string ymd = string.Format(CultureInfo.InvariantCulture, "{0}/{1}/{2}", d.Year, d.Month, d.Day);
        return app switch
        {
            CalendarApp.OutlookWeb => "https://outlook.live.com/calendar/0/view/day/" + ymd,
            CalendarApp.Naver => "https://calendar.naver.com/",
            _ => "https://calendar.google.com/calendar/r/day/" + ymd,
        };
    }

    private static void StartAumid(string aumid)
    {
        using (Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + aumid) { UseShellExecute = true })) { }
    }
}
