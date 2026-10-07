using Microsoft.Win32;

namespace MyDock.ViewModels;

/// <summary>Windows 앱 테마(라이트/다크) 읽기 + 변경 알림.</summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>앱 라이트 테마면 true (읽을 수 없으면 true).</summary>
    public static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>테마가 바뀌었을 수 있을 때 (UserPreferenceChanged General/VisualStyle). 구독자는 해제 필수(정적 이벤트).</summary>
    public static event EventHandler? Changed
    {
        add
        {
            if (_handlers == null) SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _handlers += value;
        }
        remove
        {
            _handlers -= value;
            if (_handlers == null) SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }

    private static EventHandler? _handlers;

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
            _handlers?.Invoke(null, EventArgs.Empty);
    }
}
