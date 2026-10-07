using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyDock.Services;

/// <summary>
/// 크로미움 계열 브라우저(크롬·엣지·웨일) 프로필: User Data\Local State 의 profile.info_cache / profiles_order.
/// 표시 이름은 크롬과 같게: 기본 이름(is_using_default_name)이면 gaia_given_name → gaia_name, 아니면 name.
/// 결과는 Local State 수정 시각으로 캐시.
/// </summary>
internal static partial class ChromiumProfiles
{
    public sealed record Profile(string Dir, string Name, string? PicturePath);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Stamp, List<Profile> List)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>exe 파일명 → User Data 폴더. 크로미움 계열이 아니면 null.</summary>
    public static string? UserDataDir(string? exePath)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFileName(exePath ?? "").ToLowerInvariant() switch
        {
            "chrome.exe" => Path.Combine(local, @"Google\Chrome\User Data"),
            "msedge.exe" => Path.Combine(local, @"Microsoft\Edge\User Data"),
            "whale.exe" => Path.Combine(local, @"Naver\Naver Whale\User Data"),
            _ => null,
        };
    }

    public static IReadOnlyList<Profile> Read(string? exePath)
    {
        string? dir = UserDataDir(exePath);
        if (dir is null) return Array.Empty<Profile>();
        string file = Path.Combine(dir, "Local State");
        try
        {
            if (!File.Exists(file)) return Array.Empty<Profile>();
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            lock (Gate)
                if (Cache.TryGetValue(file, out var c) && c.Stamp == stamp) return c.List;

            string json;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                json = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var list = new List<Profile>();
            if (doc.RootElement.TryGetProperty("profile", out var prof) &&
                prof.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            {
                var order = new List<string>();
                if (prof.TryGetProperty("profiles_order", out var po) && po.ValueKind == JsonValueKind.Array)
                    order.AddRange(po.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0));
                foreach (var p in cache.EnumerateObject())
                    if (!order.Contains(p.Name)) order.Add(p.Name);

                foreach (string d in order)
                {
                    if (!cache.TryGetProperty(d, out var info)) continue;
                    string name = DisplayName(info, d);
                    string pic = Path.Combine(dir, d, "Google Profile Picture.png");
                    list.Add(new Profile(d, name, File.Exists(pic) ? pic : null));
                }
            }
            lock (Gate) Cache[file] = (stamp, list);
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn($"브라우저 프로필 읽기 실패: {file} ({ex.Message})");
            return Array.Empty<Profile>();
        }
    }

    private static string DisplayName(JsonElement info, string dir)
    {
        string? Str(string k) => info.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        bool isDefault = info.TryGetProperty("is_using_default_name", out var d) && d.ValueKind == JsonValueKind.True;
        return (isDefault ? Str("gaia_given_name") ?? Str("gaia_name") : null) ?? Str("name") ?? dir;
    }

    [GeneratedRegex(@"--profile-directory=(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileDirRegex();

    /// <summary>재실행 명령줄에서 --profile-directory 값. 없으면 null.</summary>
    public static string? ParseProfileDir(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        var m = ProfileDirRegex().Match(commandLine);
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    public static ImageSource? LoadPicture(string? path)
    {
        if (path is null) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 96;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex)
        {
            Log.Warn($"프로필 사진 로드 실패: {path} ({ex.Message})");
            return null;
        }
    }
}
