using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MyDock.Services;

/// <summary>토스트 XML 에서 꺼낸 표시용 내용. 이미지 경로는 로컬 파일로 풀린 것만 (없거나 웹 주소면 null).</summary>
internal sealed record ToastContent(
    string? Title,
    IReadOnlyList<string> Lines,
    string? Attribution,
    string? AppLogoPath,
    bool AppLogoCircle,
    string? ImagePath,
    string? Scenario = null)
{
    /// <summary>사용자 조작이 필요한 토스트 (알람·미리 알림·전화·긴급) — 윈도우 팝업을 숨기면 안 됨.</summary>
    public bool IsInteractiveScenario => Scenario is { } s &&
        (s.Equals("reminder", StringComparison.OrdinalIgnoreCase) || s.Equals("alarm", StringComparison.OrdinalIgnoreCase) ||
         s.Equals("incomingCall", StringComparison.OrdinalIgnoreCase) || s.Equals("urgent", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// wpndatabase.db Notification.Payload (토스트 XML) 파서. 예외를 던지지 않고 실패 시 null.
/// - 적응형 템플릿(ToastGeneric): binding 아래 모든 text(그룹/서브그룹 포함). placement="attribution" 은 따로.
/// - 옛 템플릿(ToastText01~04, ToastImageAndText01~04): text id 순서.
/// - image placement="appLogoOverride" → 앱 로고 대체(보낸 사람 사진 등, hint-crop="circle"), 그 외 첫 image(inline/hero) → 본문 이미지.
/// - 이미지 src: file:///, 절대 경로, ms-appdata:///local|roaming|temp/ (패키지 앱 — %LOCALAPPDATA%\Packages\패밀리\*State). ms-appx·http 는 무시.
/// </summary>
internal static class ToastPayload
{
    public static ToastContent? Parse(byte[]? payload, string? packageFamily)
    {
        if (payload is null || payload.Length == 0) return null;
        try
        {
            string xml = Decode(payload);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var sr = new StringReader(xml);
            using var reader = XmlReader.Create(sr, settings);
            var doc = XDocument.Load(reader);
            var root = doc.Root;
            if (root is null || !root.Name.LocalName.Equals("toast", StringComparison.OrdinalIgnoreCase)) return null;

            var bindings = root.Descendants().Where(e => e.Name.LocalName == "binding").ToList();
            if (bindings.Count == 0) return null;
            var binding = bindings.FirstOrDefault(b =>
                              string.Equals((string?)b.Attribute("template"), "ToastGeneric", StringComparison.OrdinalIgnoreCase))
                          ?? bindings[0];

            var texts = new List<string>();
            string? attribution = null;
            foreach (var t in binding.Descendants().Where(e => e.Name.LocalName == "text"))
            {
                string value = Clean(t.Value);
                if (value.Length == 0) continue;
                if (string.Equals((string?)t.Attribute("placement"), "attribution", StringComparison.OrdinalIgnoreCase))
                {
                    attribution ??= value;
                    continue;
                }
                texts.Add(value);
            }

            string? logo = null, image = null;
            bool circle = false;
            foreach (var img in binding.Descendants().Where(e => e.Name.LocalName == "image"))
            {
                string placement = (string?)img.Attribute("placement") ?? "";
                string? path = ResolveImage((string?)img.Attribute("src"), packageFamily);
                if (placement.Equals("appLogoOverride", StringComparison.OrdinalIgnoreCase))
                {
                    if (logo is null && path is not null)
                    {
                        logo = path;
                        circle = string.Equals((string?)img.Attribute("hint-crop"), "circle", StringComparison.OrdinalIgnoreCase);
                    }
                }
                else if (image is null && path is not null)
                {
                    image = path;
                }
            }
            // 옛 템플릿(ToastImageAndText0x)의 이미지는 앱 로고 자리 (id=1, placement 없음)
            if (logo is null && image is not null &&
                ((string?)binding.Attribute("template") ?? "").StartsWith("ToastImageAndText", StringComparison.OrdinalIgnoreCase))
            {
                logo = image;
                image = null;
            }

            if (texts.Count == 0 && attribution is null) return null;
            string? title = texts.Count > 0 ? texts[0] : null;
            string? scenario = ((string?)root.Attribute("scenario"))?.Trim();
            return new ToastContent(title, texts.Skip(1).ToList(), attribution, logo, circle, image,
                string.IsNullOrEmpty(scenario) ? null : scenario);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Payload BLOB 은 보통 UTF-8 (BOM 있을 수 있음). UTF-16 BOM 이면 그것으로.</summary>
    private static string Decode(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(data, start, data.Length - start).TrimEnd('\0');
    }

    private static string Clean(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return s;
    }

    internal static string? ResolveImage(string? src, string? packageFamily)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        src = src.Trim();
        try
        {
            if (src.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(packageFamily)) return null;
                string rest = Uri.UnescapeDataString(src["ms-appdata:///".Length..]).Replace('/', '\\');
                int slash = rest.IndexOf('\\');
                if (slash <= 0) return null;
                string folder = rest[..slash].ToLowerInvariant() switch
                {
                    "local" => "LocalState",
                    "roaming" => "RoamingState",
                    "temp" => "TempState",
                    _ => "",
                };
                if (folder.Length == 0) return null;
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", packageFamily, folder, rest[(slash + 1)..]);
                return File.Exists(path) ? path : null;
            }
            if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(src);
                return uri.IsFile && File.Exists(uri.LocalPath) ? uri.LocalPath : null;
            }
            if (Path.IsPathFullyQualified(src) && File.Exists(src)) return src;
        }
        catch
        {
            // 잘못된 경로 → 무시
        }
        return null;
    }
}
