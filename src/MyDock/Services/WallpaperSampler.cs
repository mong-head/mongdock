using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 바탕화면 배경(월페이퍼)만의 색을 계산한다 — 화면 캡처가 아니므로 우리 창/다른 창이 섞이지 않음.
/// IDesktopWallpaper 로 주 모니터의 배경 파일·맞춤 방식·배경색을 읽고, 그림을 맞춤 방식대로 모니터 좌표에 매핑해 영역 평균을 낸다.
/// 파일을 못 찾으면(Spotlight 등) %APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper, 그래도 없으면 배경색.
/// </summary>
internal sealed class WallpaperSampler
{
    // DESKTOP_WALLPAPER_POSITION
    private const int DWPOS_CENTER = 0, DWPOS_TILE = 1, DWPOS_STRETCH = 2, DWPOS_FIT = 3, DWPOS_FILL = 4, DWPOS_SPAN = 5;
    private const int MaxDecodeWidth = 1280;

    private static readonly string TranscodedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");

    private readonly object _gate = new();
    private readonly Dictionary<string, Color?> _colorCache = new();
    private string? _imageKey;
    private BitmapSource? _image; // 축소 디코드된 Bgra32
    private int _origW, _origH;

    public sealed record Info(string? Path, int Position, Color Background, RECT Monitor);

    public void Invalidate()
    {
        lock (_gate)
        {
            _colorCache.Clear();
            _imageKey = null;
            _image = null;
        }
    }

    /// <summary>현재 주 모니터 배경 정보. 실패 시 null.</summary>
    public static Info? QueryPrimary()
    {
        object? obj = null;
        try
        {
            obj = new DesktopWallpaperClass();
            var wp = (IDesktopWallpaper)obj;
            DesktopApi.TryGetMonitorRects(DesktopApi.PrimaryMonitor, out RECT primary, out _);

            string? id = null;
            RECT mrect = primary;
            uint count = wp.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                string mid = wp.GetMonitorDevicePathAt(i);
                RECT r;
                try { r = wp.GetMonitorRECT(mid); } catch { continue; }
                if (r.Left == primary.Left && r.Top == primary.Top && r.Right == primary.Right && r.Bottom == primary.Bottom)
                {
                    id = mid;
                    mrect = r;
                    break;
                }
                id ??= mid; // 못 맞추면 첫 모니터
            }

            string? path = null;
            try { path = id is null ? null : wp.GetWallpaper(id); } catch { /* 슬라이드쇼 등 */ }
            int pos = DWPOS_FILL;
            try { pos = wp.GetPosition(); } catch { }
            uint cref = 0;
            try { cref = wp.GetBackgroundColor(); } catch { }
            var bg = Color.FromRgb((byte)(cref & 0xFF), (byte)((cref >> 8) & 0xFF), (byte)((cref >> 16) & 0xFF));
            return new Info(string.IsNullOrWhiteSpace(path) ? null : path, pos, bg, mrect);
        }
        catch (Exception ex)
        {
            Log.Warn($"IDesktopWallpaper 조회 실패: {ex.Message}");
            return null;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    /// <summary>변경 감지용 서명: 배경 경로 + 맞춤 + 배경색 + TranscodedWallpaper 시각 + 현재 가상 데스크톱.</summary>
    public static string GetSignature()
    {
        var info = QueryPrimary();
        string vd = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] b) vd = Convert.ToHexString(b);
        }
        catch { }
        long t = 0;
        try { if (File.Exists(TranscodedPath)) t = File.GetLastWriteTimeUtc(TranscodedPath).Ticks; } catch { }
        return $"{info?.Path}|{info?.Position}|{info?.Background}|{t}|{vd}";
    }

    /// <summary>주 모니터 기준 물리 픽셀 영역의 배경 평균 색.</summary>
    public Color? Sample(RECT areaPx)
    {
        var info = QueryPrimary();
        if (info is null) return null;

        string? file = info.Path is not null && File.Exists(info.Path) ? info.Path
                     : File.Exists(TranscodedPath) && info.Path is not null ? TranscodedPath
                     : null;
        long stamp = 0;
        try { if (file is not null) stamp = File.GetLastWriteTimeUtc(file).Ticks; } catch { }
        string key = $"{file}|{stamp}|{info.Position}|{info.Background}|{info.Monitor}|{areaPx}";

        lock (_gate)
        {
            if (_colorCache.TryGetValue(key, out var cached)) return cached;
            Color? c = file is null ? info.Background : SampleImage(file, stamp, info, areaPx);
            if (_colorCache.Count > 64) _colorCache.Clear();
            _colorCache[key] = c;
            return c;
        }
    }

    private Color? SampleImage(string file, long stamp, Info info, RECT areaPx)
    {
        if (!EnsureImage(file, stamp) || _image is null) return info.Background;

        int mw = info.Monitor.Width, mh = info.Monitor.Height;
        if (mw <= 0 || mh <= 0) return null;
        double iw = _origW, ih = _origH;

        // 그림이 그려지는 사각형 (모니터 기준 물리 픽셀)
        double dx, dy, dw, dh;
        switch (info.Position)
        {
            case DWPOS_CENTER: dw = iw; dh = ih; break;
            case DWPOS_TILE: dw = iw; dh = ih; break;
            case DWPOS_STRETCH: dw = mw; dh = mh; break;
            case DWPOS_FIT: { double s = Math.Min(mw / iw, mh / ih); dw = iw * s; dh = ih * s; break; }
            default: { double s = Math.Max(mw / iw, mh / ih); dw = iw * s; dh = ih * s; break; } // FILL, SPAN(근사)
        }
        if (info.Position == DWPOS_TILE) { dx = 0; dy = 0; }
        else { dx = (mw - dw) / 2; dy = (mh - dh) / 2; }

        int pw = _image.PixelWidth, ph = _image.PixelHeight, stride = pw * 4;
        var pixels = new byte[stride * ph];
        _image.CopyPixels(pixels, stride, 0);

        int ax0 = areaPx.Left - info.Monitor.Left, ay0 = areaPx.Top - info.Monitor.Top;
        int aw = Math.Max(1, areaPx.Width), ah = Math.Max(1, areaPx.Height);
        int stepX = Math.Max(1, aw / 400), stepY = Math.Max(1, ah / 20);
        double r = 0, g = 0, b = 0;
        long n = 0;
        for (int y = ay0; y < ay0 + ah; y += stepY)
        for (int x = ax0; x < ax0 + aw; x += stepX)
        {
            double u = (x + 0.5 - dx) / dw, v = (y + 0.5 - dy) / dh;
            if (info.Position == DWPOS_TILE) { u -= Math.Floor(u); v -= Math.Floor(v); }
            if (u < 0 || u >= 1 || v < 0 || v >= 1)
            {
                r += info.Background.R; g += info.Background.G; b += info.Background.B;
            }
            else
            {
                int px = Math.Min(pw - 1, (int)(u * pw)), py = Math.Min(ph - 1, (int)(v * ph));
                int i = py * stride + px * 4;
                b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2];
            }
            n++;
        }
        if (n == 0) return null;
        return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private bool EnsureImage(string file, long stamp)
    {
        string key = file + "|" + stamp;
        if (_imageKey == key && _image is not null) return true;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = dec.Frames[0];
            _origW = frame.PixelWidth;
            _origH = frame.PixelHeight;
            if (_origW <= 0 || _origH <= 0) return false;

            fs.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (_origW > MaxDecodeWidth) bi.DecodePixelWidth = MaxDecodeWidth;
            bi.StreamSource = fs;
            bi.EndInit();
            var conv = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
            conv.Freeze();
            _image = conv;
            _imageKey = key;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"배경 이미지 로드 실패: {file} ({ex.Message})");
            _image = null;
            _imageKey = null;
            return false;
        }
    }
}
