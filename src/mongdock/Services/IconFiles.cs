using System.IO;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// "아이콘 바꾸기"로 고른 그림 파일 정리 (#24): 루틴 그림은 icons\routines\, 독 폴더 그림은 icons\folders\ 에 복사해 두고,
/// 어떤 핀·루틴도 쓰지 않게 된 파일(편집 취소·루틴 지우기·아이콘 바꿈)은 지움. 다른 아이콘(icons\ 바로 아래 — 핀 아이콘 변경)은 건드리지 않음.
/// </summary>
internal static class IconFiles
{
    public const string Routines = "routines", Folders = "folders";

    /// <param name="keep">아직 저장 전인 것(편집 중인 사본의 그림 등).</param>
    public static void Cleanup(ISettingsService settings, params string?[] keep)
    {
        if (settings is not SettingsService service) return;
        try
        {
            var s = settings.Current;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Use(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try { used.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            foreach (var p in s.Pins) { Use(p.IconPath); Use(p.Icon?.File); }
            foreach (var r in s.Routines) Use(r.Icon?.File);
            foreach (var k in keep) Use(k);
            int n = 0;
            foreach (string sub in new[] { Routines, Folders })
            {
                string dir = Path.Combine(service.IconsDirectory, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    if (used.Contains(Path.GetFullPath(f))) continue;
                    try { File.Delete(f); n++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            if (n > 0) Log.Info($"쓰지 않는 아이콘 그림 {n}개 정리");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"아이콘 그림 정리 실패: {ex.GetType().Name}");
        }
    }
}
