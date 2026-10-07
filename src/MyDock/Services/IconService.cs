using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyDock.Models;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 핀/창 아이콘 → Frozen ImageSource. 키별 LRU 캐시(최대 256개).
/// 우선순위: IconPath(png/ico 등) → 패키지 앱(AUMID, IShellItemImageFactory) → exe(IShellItemImageFactory 256px →
/// SHGetImageList(SHIL_JUMBO) → SHGetFileInfo) → 기본 아이콘.
/// </summary>
public sealed class IconService : IIconService
{
    private const int IconPx = 256;
    private const int CacheLimit = 256;

    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, ImageSource Image)> _lru = new();
    private readonly object _gate = new();
    private ImageSource? _default;
    private ImageSource? _launchpad;

    public ImageSource GetIcon(PinItem pin, IconStyle style)
    {
        if (pin is null) return Fallback(style);
        bool launchpad = pin.Kind == PinKind.Special && pin.Target.Equals("launchpad", StringComparison.OrdinalIgnoreCase)
                         && string.IsNullOrWhiteSpace(pin.IconPath);
        if (launchpad) return style == IconStyle.Mac ? MacLaunchpadIcon : LaunchpadIcon;

        string rawKey = $"pin|{pin.Kind}|{pin.Target}|{pin.IconPath}";
        return Styled(rawKey, style, () =>
        {
            if (!string.IsNullOrWhiteSpace(pin.IconPath))
            {
                var img = LoadImageFile(pin.IconPath);
                if (img is not null) return img;
            }
            return pin.Kind switch
            {
                PinKind.Aumid => FromShellItem(AppsFolder.ShellPathOf(pin.Target.Trim())),
                PinKind.Exe => FromFile(Environment.ExpandEnvironmentVariables(pin.Target.Trim().Trim('"'))),
                _ => null,
            };
        });
    }

    public ImageSource GetIcon(AppWindowInfo window, IconStyle style)
    {
        if (window is null) return Fallback(style);
        bool packaged = !string.IsNullOrEmpty(window.Aumid) && AppsFolder.IsWindowsAppsPath(window.ProcessPath);
        if (!packaged && window.ProcessPath.Length == 0)
        {
            // hwnd 기반은 창이 사라져도 캐시에 남으니 캐시하지 않음
            var raw = FromWindowHandle(window.Hwnd);
            return raw is null ? Fallback(style) : style == IconStyle.Mac ? (MacIconRenderer.Normalize(raw) ?? Fallback(style)) : raw;
        }
        string rawKey = packaged ? "aumid|" + window.Aumid : "exe|" + window.ProcessPath;
        return Styled(rawKey, style, () =>
            (packaged ? FromShellItem(AppsFolder.ShellPathOf(window.Aumid!)) : null)
            ?? FromFile(window.ProcessPath)
            ?? FromWindowHandle(window.Hwnd));
    }

    /// <summary>원본은 rawKey 로, 맥 스타일은 "mac|rawKey" 로 따로 캐시.</summary>
    private ImageSource Styled(string rawKey, IconStyle style, Func<ImageSource?> loadRaw)
    {
        ImageSource raw = GetOrAdd("orig|" + rawKey, loadRaw, DefaultIcon);
        if (style != IconStyle.Mac) return raw;
        return GetOrAdd("mac|" + rawKey, () =>
            raw is BitmapSource bs && !ReferenceEquals(raw, _default) ? MacIconRenderer.Normalize(bs) : null,
            MacDefaultIcon);
    }

    private ImageSource Fallback(IconStyle style) => style == IconStyle.Mac ? MacDefaultIcon : DefaultIcon;

    // ───────────────────────── 캐시 ─────────────────────────

    private ImageSource GetOrAdd(string key, Func<ImageSource?> factory, ImageSource fallback)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }
        }

        ImageSource? img = null;
        try { img = factory(); }
        catch (Exception ex) { Log.Error($"아이콘 로드 실패: {key}", ex); }
        img ??= fallback;
        if (img.CanFreeze && !img.IsFrozen) img.Freeze();

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing)) return existing.Value.Image;
            var node = _lru.AddFirst((key, img));
            _map[key] = node;
            while (_lru.Count > CacheLimit)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
        return img;
    }

    // ───────────────────────── 이미지 파일 ─────────────────────────

    private static ImageSource? LoadImageFile(string path)
    {
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                // .ico 는 가장 큰 프레임 선택
                var dec = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame? best = dec.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight)
                                              .ThenByDescending(f => f.Format.BitsPerPixel).FirstOrDefault();
                if (best is null) return null;
                best.Freeze();
                return best;
            }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad; // 파일 잠금 방지
            bi.StreamSource = fs;
            bi.EndInit();
            if (bi.PixelWidth > 512) // 너무 큰 이미지는 축소해서 다시 디코드
            {
                fs.Position = 0;
                bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = IconPx;
                bi.StreamSource = fs;
                bi.EndInit();
            }
            bi.Freeze();
            return bi;
        }
        catch (Exception ex)
        {
            Log.Error($"아이콘 이미지 로드 실패: {path}", ex);
            return null;
        }
    }

    // ───────────────────────── 셸 아이콘 ─────────────────────────

    private static ImageSource? FromFile(string path)
    {
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) return null;
        return FromShellItem(path) ?? FromJumboImageList(path) ?? FromSHGetFileInfo(path);
    }

    /// <summary>IShellItemImageFactory.GetImage 로 256px 아이콘 (패키지 앱/파일 모두 가능).</summary>
    private static BitmapSource? FromShellItem(string parsingName)
    {
        object? obj = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (Shell32.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out obj) != 0 || obj is null) return null;
            var factory = (IShellItemImageFactory)obj;
            int hr = factory.GetImage(new SIZE(IconPx, IconPx),
                ShellConst.SIIGBF_ICONONLY | ShellConst.SIIGBF_BIGGERSIZEOK, out hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;
            return HBitmapToBitmapSource(hbmp);
        }
        catch (Exception ex)
        {
            Log.Warn($"IShellItemImageFactory 실패: {parsingName} ({ex.Message})");
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) Gdi32.DeleteObject(hbmp);
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    private static BitmapSource? FromJumboImageList(string path)
    {
        IntPtr hIcon = IntPtr.Zero;
        IImageList? list = null;
        try
        {
            var sfi = new SHFILEINFO();
            if (Shell32.SHGetFileInfo(path, 0, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), Shell32.SHGFI_SYSICONINDEX) == IntPtr.Zero)
                return null;
            var iid = typeof(IImageList).GUID;
            if (Shell32.SHGetImageList(Shell32.SHIL_JUMBO, ref iid, out list) != 0 || list is null) return null;
            if (list.GetIcon(sfi.iIcon, Shell32.ILD_TRANSPARENT, out hIcon) != 0 || hIcon == IntPtr.Zero) return null;
            return HIconToBitmapSource(hIcon);
        }
        catch (Exception ex)
        {
            Log.Warn($"SHGetImageList(JUMBO) 실패: {path} ({ex.Message})");
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) User32.DestroyIcon(hIcon);
            if (list is not null) Marshal.ReleaseComObject(list);
        }
    }

    private static BitmapSource? FromSHGetFileInfo(string path)
    {
        var sfi = new SHFILEINFO();
        try
        {
            if (Shell32.SHGetFileInfo(path, 0, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
                    Shell32.SHGFI_ICON | Shell32.SHGFI_LARGEICON) == IntPtr.Zero || sfi.hIcon == IntPtr.Zero)
                return null;
            return HIconToBitmapSource(sfi.hIcon);
        }
        catch (Exception ex)
        {
            Log.Warn($"SHGetFileInfo 실패: {path} ({ex.Message})");
            return null;
        }
        finally
        {
            if (sfi.hIcon != IntPtr.Zero) User32.DestroyIcon(sfi.hIcon);
        }
    }

    /// <summary>창 자신의 아이콘 (프로세스 경로를 모를 때). 빌린 핸들이므로 DestroyIcon 하지 않음.</summary>
    private static BitmapSource? FromWindowHandle(IntPtr hwnd)
    {
        try
        {
            IntPtr h = IntPtr.Zero;
            foreach (int type in new[] { 1 /* ICON_BIG */, 2 /* ICON_SMALL2 */ })
            {
                if (User32.SendMessageTimeout(hwnd, User32.WM_GETICON, new IntPtr(type), IntPtr.Zero,
                        User32.SMTO_ABORTIFHUNG, 100, out h) != IntPtr.Zero && h != IntPtr.Zero) break;
                h = IntPtr.Zero;
            }
            if (h == IntPtr.Zero) h = User32.GetClassLongPtr(hwnd, User32.GCLP_HICON);
            return h == IntPtr.Zero ? null : HIconToBitmapSource(h);
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────── 변환 ─────────────────────────

    private static BitmapSource HIconToBitmapSource(IntPtr hIcon)
    {
        var bs = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        bs.Freeze();
        return bs;
    }

    /// <summary>32bpp HBITMAP → BitmapSource (알파 유지). 프리멀티플라이 여부를 픽셀로 판별.</summary>
    private static BitmapSource? HBitmapToBitmapSource(IntPtr hbmp)
    {
        if (Gdi32.GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Gdi32.BI_RGB,
            },
            bmiColors = new uint[256],
        };
        int stride = w * 4;
        var pixels = new byte[stride * h];
        IntPtr hdc = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (Gdi32.GetDIBits(hdc, hbmp, 0, (uint)h, pixels, ref bmi, Gdi32.DIB_RGB_COLORS) == 0) return null;
        }
        finally
        {
            Gdi32.DeleteDC(hdc);
        }

        bool anyAlpha = false, straight = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a != 0) anyAlpha = true;
            if (pixels[i] > a || pixels[i + 1] > a || pixels[i + 2] > a) straight = true;
            if (anyAlpha && straight) break;
        }
        PixelFormat fmt;
        if (!anyAlpha)
            fmt = PixelFormats.Bgr32; // 알파 채널이 없는 비트맵
        else
            fmt = straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;

        var bs = BitmapSource.Create(w, h, 96, 96, fmt, null, pixels, stride);
        bs.Freeze();
        return bs;
    }

    // ───────────────────────── 기본 아이콘 ─────────────────────────

    private ImageSource DefaultIcon => _default ??= CreateDefaultIcon();

    private ImageSource LaunchpadIcon => _launchpad ??= CreateLaunchpadIcon();

    private ImageSource? _macDefault, _macLaunchpad;

    /// <summary>맥 스타일 기본 아이콘: 밝은 판 위에 시스템 기본 앱 아이콘.</summary>
    private ImageSource MacDefaultIcon => _macDefault ??=
        (DefaultIcon is BitmapSource b ? MacIconRenderer.Plate(b) : MacIconRenderer.Plate(null));

    private ImageSource MacLaunchpadIcon => _macLaunchpad ??= MacIconRenderer.Launchpad();

    private static ImageSource CreateDefaultIcon()
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        try
        {
            if (Shell32.SHGetStockIconInfo(Shell32.SIID_APPLICATION, Shell32.SHGSI_ICON | Shell32.SHGSI_LARGEICON, ref info) == 0
                && info.hIcon != IntPtr.Zero)
                return HIconToBitmapSource(info.hIcon);
        }
        catch (Exception ex)
        {
            Log.Warn($"기본 아이콘(SHGetStockIconInfo) 실패: {ex.Message}");
        }
        finally
        {
            if (info.hIcon != IntPtr.Zero) User32.DestroyIcon(info.hIcon);
        }

        var g = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), null,
            new RectangleGeometry(new Rect(0, 0, 64, 64), 14, 14));
        var di = new DrawingImage(g);
        di.Freeze();
        return di;
    }

    /// <summary>런치패드: 3x3 둥근 사각형 격자.</summary>
    private static ImageSource CreateLaunchpadIcon()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new LinearGradientBrush(Color.FromRgb(0x5E, 0x5C, 0xE6), Color.FromRgb(0xBF, 0x5A, 0xF2), 90),
            null, new RectangleGeometry(new Rect(0, 0, 64, 64), 14, 14)));
        var white = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            group.Children.Add(new GeometryDrawing(white, null,
                new RectangleGeometry(new Rect(12 + c * 15, 12 + r * 15, 10, 10), 3, 3)));
        var di = new DrawingImage(group);
        di.Freeze();
        return di;
    }
}
