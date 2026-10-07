using System.Diagnostics;
using System.IO;
using MyDock.Models;

namespace MyDock.ViewModels;

/// <summary>창 → 사람이 읽을 앱 이름 (exe 의 FileDescription, 없으면 파일명). 경로별 캐시.</summary>
public static class AppNames
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Get(AppWindowInfo w)
    {
        if (string.IsNullOrEmpty(w.ProcessPath)) return w.Title;
        if (Cache.TryGetValue(w.ProcessPath, out var cached)) return cached;

        string name = "";
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(w.ProcessPath).FileDescription?.Trim();
            // 일부 앱은 FileDescription 이 비었거나 너무 김 → 짧은 것만 사용
            if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) name = desc;
        }
        catch
        {
            // WindowsApps 접근 거부 등 → 파일명으로
        }

        if (name.Length == 0)
        {
            name = Path.GetFileNameWithoutExtension(w.ProcessPath);
            if (name.Length > 0) name = char.ToUpperInvariant(name[0]) + name[1..];
        }
        if (name.Length == 0) name = w.Title;

        Cache[w.ProcessPath] = name;
        return name;
    }
}
