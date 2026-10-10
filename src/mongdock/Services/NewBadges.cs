using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 새 기능 "NEW" 배지 (#24): 업데이트로 그 기능을 처음 받은 기존 사용자에게만, 메뉴 항목·제목 옆 작은 알약 "NEW"(모든 언어 같은 글자).
/// 새 설치는 안 보임(빈 상태 설명으로 충분). 그 기능을 한 번 쓰거나 업데이트 후 14일이 지나면 사라짐.
/// 기록(Settings.NewSince·NewSeen)은 이 PC 것 — 설정 옮기기에 넣지 않음.
/// </summary>
internal static class NewBadges
{
    public const string Folders = "folders", RecycleBin = "recycleBin";

    /// <summary>배지를 붙이는 기능들 (새 기능이 생기면 여기에 키를 더함).</summary>
    private static readonly string[] Features = { Folders, RecycleBin };

    private static readonly TimeSpan Life = TimeSpan.FromDays(14);
    private static ISettingsService? _settings;

    /// <summary>시작 때: 새 설치면 모두 본 것으로, 기존 사용자면 처음 만난 기능의 날짜를 적음.</summary>
    public static void Init(ISettingsService settings, bool createdThisRun)
    {
        _settings = settings;
        var s = settings.Current;
        bool changed = false;
        foreach (string key in Features)
        {
            if (s.NewSeen.Contains(key) || s.NewSince.ContainsKey(key)) continue;
            if (createdThisRun) s.NewSeen.Add(key);
            else s.NewSince[key] = DateTime.UtcNow;
            changed = true;
        }
        if (changed) settings.Save();
    }

    public static bool Show(string key)
    {
        var s = _settings?.Current;
        return s is not null && !s.NewSeen.Contains(key) && s.NewSince.TryGetValue(key, out var since) && DateTime.UtcNow - since < Life;
    }

    /// <summary>그 기능을 씀 → 배지 사라짐.</summary>
    public static void Used(string key)
    {
        var settings = _settings;
        if (settings is null || settings.Current.NewSeen.Contains(key)) return;
        settings.Current.NewSeen.Add(key);
        settings.Current.NewSince.Remove(key);
        settings.Save();
    }
}
