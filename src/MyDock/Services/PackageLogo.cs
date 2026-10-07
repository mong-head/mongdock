using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace MyDock.Services;

/// <summary>
/// 패키지 로고(Assets) 파일 읽기.
/// - Load: 패키지 로고(StoreLogo) — shell 아이콘을 못 얻을 때의 fallback.
/// - LoadUnplated: AppxManifest 의 Square44x44Logo 기준 "타일 배경이 없는" 변형
///   (*.targetsize-N_altform-lightunplated.png / _altform-unplated.png 중 가장 큰 것) — 맥 스타일 흰 판 위에 올릴 용도.
/// </summary>
internal static partial class PackageLogo
{
    /// <summary>마이크로소프트(윈도우/스토어 기본) 패키지인지 — 게시자 ID 로 판정.</summary>
    public static bool IsWindowsPackage(string aumidOrFamily)
    {
        string family = AppsFolder.FamilyOf(aumidOrFamily.Trim());
        return family.EndsWith("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)   // Microsoft Corporation (스토어 앱)
            || family.EndsWith("_cw5n1h2txyewy", StringComparison.OrdinalIgnoreCase);  // Microsoft Windows (시스템 앱: 설정 등)
    }

    public static BitmapSource? Load(string family)
    {
        try
        {
            var pkg = new PackageManager().FindPackagesForUser("", family).FirstOrDefault();
            var uri = pkg?.Logo;
            if (uri is null) return null;
            string? file = ResolveScaled(uri.LocalPath);
            return file is null ? null : LoadFile(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"패키지 로고 로드 실패: {family} ({ex.Message})");
            return null;
        }
    }

    /// <summary>앱(AUMID)의 unplated 로고 중 가장 큰 targetsize. 없으면 null.</summary>
    public static BitmapSource? LoadUnplated(string aumid)
    {
        try
        {
            string family = AppsFolder.FamilyOf(aumid);
            int bang = aumid.IndexOf('!');
            string appId = bang > 0 ? aumid[(bang + 1)..] : "";
            var pkg = new PackageManager().FindPackagesForUser("", family).FirstOrDefault();
            string? root = pkg?.InstalledLocation?.Path;
            if (root is null) return null;

            string? logo = FindSquare44Logo(Path.Combine(root, "AppxManifest.xml"), appId);
            if (logo is null) return null;
            string? file = FindUnplated(Path.Combine(root, logo));
            return file is null ? null : LoadFile(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"unplated 로고 로드 실패: {aumid} ({ex.Message})");
            return null;
        }
    }

    /// <summary>AppxManifest 의 Application(Id=appId) → VisualElements@Square44x44Logo (예: "Assets\AppList.png").</summary>
    private static string? FindSquare44Logo(string manifest, string appId)
    {
        if (!File.Exists(manifest)) return null;
        var doc = XDocument.Load(manifest);
        var apps = doc.Descendants().Where(e => e.Name.LocalName == "Application").ToList();
        var app = apps.FirstOrDefault(a => string.Equals((string?)a.Attribute("Id"), appId, StringComparison.OrdinalIgnoreCase))
                  ?? apps.FirstOrDefault();
        var ve = app?.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
        return (string?)ve?.Attribute("Square44x44Logo");
    }

    [GeneratedRegex(@"targetsize-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TargetSizeRegex();

    /// <summary>
    /// "Assets\AppList.png" → 같은 폴더(및 언어/테마 하위 폴더 제외)의 "AppList.targetsize-256_altform-lightunplated.png" 등.
    /// 흰 판 위에 올리므로 lightunplated(밝은 배경용)를 우선, 그다음 unplated. targetsize 가 큰 것 우선.
    /// </summary>
    private static string? FindUnplated(string logoPath)
    {
        string? dir = Path.GetDirectoryName(logoPath);
        if (dir is null || !Directory.Exists(dir)) return null;
        string stem = Path.GetFileNameWithoutExtension(logoPath);
        var candidates = Directory.EnumerateFiles(dir, stem + ".*.png")
            .Select(f => (File: f, Name: Path.GetFileName(f).ToLowerInvariant()))
            .Where(c => c.Name.Contains("unplated") && !c.Name.Contains("contrast"))
            .Select(c =>
            {
                var m = TargetSizeRegex().Match(c.Name);
                int size = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                int pref = c.Name.Contains("altform-lightunplated") ? 2 : c.Name.Contains("altform-unplated") ? 1 : 0;
                return (c.File, size, pref);
            })
            .OrderByDescending(c => c.size).ThenByDescending(c => c.pref)
            .ToList();
        return candidates.FirstOrDefault().File;
    }

    private static string? ResolveScaled(string path)
    {
        if (File.Exists(path)) return path;
        string? dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return null;
        string stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        return Directory.EnumerateFiles(dir, stem + ".*" + ext)
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    private static BitmapSource LoadFile(string file)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.UriSource = new Uri(file);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
