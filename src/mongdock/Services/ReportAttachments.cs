using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mongdock.Services;

/// <summary>신고에 붙일 파일 하나 (보낼 바이트 — 그림은 줄인 것, 텍스트는 가린 것).</summary>
public sealed record ReportAttachment(string Name, string Mime, byte[] Data)
{
    public bool IsImage => Mime.StartsWith("image/", StringComparison.Ordinal);
}

/// <summary>
/// 문제 신고 첨부 (#26, spec-report-attachments.md): 그림·짧은 동영상·텍스트/로그, 최대 3개·합계 10MB.
/// 그림은 긴 변 2000px 를 넘으면 줄여 JPEG 85 로 (원본은 그대로), 텍스트·로그는 진단 정보와 같은 가리기를 거친다.
/// </summary>
public static class ReportAttachments
{
    public const int MaxCount = 3;
    public const long MaxTotalBytes = 10L * 1024 * 1024;
    private const int MaxSide = 2000;

    private static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".mp4"] = "video/mp4", [".txt"] = "text/plain", [".log"] = "text/plain",
    };

    /// <summary>파일 열기 창 거르기.</summary>
    public static string DialogFilter => Loc.T("그림·동영상·텍스트") + "|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.mp4;*.txt;*.log";

    public static bool Allowed(string path) => Mimes.ContainsKey(Path.GetExtension(path));

    /// <summary>파일 → 보낼 첨부. 허용하지 않는 종류·읽기 실패면 null (이유는 로그). 큰 파일은 읽기 전에 거름.</summary>
    public static ReportAttachment? FromFile(string path, ReportRedactor.Context ctx, out bool tooBig)
    {
        tooBig = false;
        try
        {
            string ext = Path.GetExtension(path);
            if (!Mimes.TryGetValue(ext, out var mime)) return null;
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            string name = Path.GetFileName(path);
            if (mime == "text/plain")
            {
                if (info.Length > MaxTotalBytes) { tooBig = true; return null; }
                string text = File.ReadAllText(path);
                string redacted = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => ReportRedactor.RedactLog(l, ctx)));
                return new ReportAttachment(name, mime, Encoding.UTF8.GetBytes(redacted));
            }
            if (info.Length > (mime.StartsWith("image/") && mime != "image/gif" ? MaxTotalBytes * 3 : MaxTotalBytes)) { tooBig = true; return null; } // 큰 그림은 줄이면 들어갈 수 있어 3배까지 읽어 봄
            byte[] data = File.ReadAllBytes(path);
            if (mime is "image/png" or "image/jpeg" or "image/webp" && Shrink(data) is { } small)
                return new ReportAttachment(Path.ChangeExtension(name, ".jpg"), "image/jpeg", small);
            return new ReportAttachment(name, mime, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Warn($"신고 첨부 읽기 실패: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>긴 변이 2000px 를 넘는 그림 → 줄인 JPEG 85 (안 넘거나 읽을 수 없으면 null = 그대로).</summary>
    internal static byte[]? Shrink(byte[] data)
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            int w = frame.PixelWidth, h = frame.PixelHeight;
            if (Math.Max(w, h) <= MaxSide) return null;
            double scale = (double)MaxSide / Math.Max(w, h);
            return Jpeg(new TransformedBitmap(frame, new ScaleTransform(scale, scale)));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    internal static byte[] Jpeg(BitmapSource source)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        // 알파가 있는 그림은 JPEG 에서 검게 될 수 있어 흰 바탕 위에 그림
        var rgb = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        enc.Frames.Add(BitmapFrame.Create(rgb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 커서가 있는 모니터 전체 화면 → 첨부 (긴 변 2000px 넘으면 줄인 JPEG, 아니면 PNG). 실패하면 null.
    /// </summary>
    public static ReportAttachment? CaptureScreen()
    {
        try
        {
            var m = Monitors.FromCursor();
            var r = m.BoundsRect;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return null;
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);
            using var png = new MemoryStream();
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            byte[] data = png.ToArray();
            string name = $"screen-{DateTime.Now:yyyyMMdd-HHmmss}";
            return Shrink(data) is { } small ? new ReportAttachment(name + ".jpg", "image/jpeg", small) : new ReportAttachment(name + ".png", "image/png", data);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Warn($"신고 화면 캡처 실패: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>미리 보기용 그림 (그림 첨부만, 실패하면 null).</summary>
    public static BitmapSource? Preview(ReportAttachment a, int decodeWidth)
    {
        if (!a.IsImage) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(a.Data);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static string SizeText(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
