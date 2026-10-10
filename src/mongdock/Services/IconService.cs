using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mongdock.Models;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 핀/창 아이콘 → Frozen ImageSource. 키별 LRU 캐시(최대 256개).
/// 우선순위: IconPath(png/ico 등) → 패키지 앱(AUMID, IShellItemImageFactory) → exe(IShellItemImageFactory 256px →
/// SHGetImageList(SHIL_JUMBO) → SHGetFileInfo) → 기본 아이콘.
/// </summary>
public sealed class IconService : IIconService
{
    private const int IconPx = 256;
    /// <summary>
    /// 캐시 개수 상한. 256px 아이콘 하나가 픽셀만 256KB(네이티브 메모리)라 256개면 64MB — 독·실행 중 앱·최근 검색에 넉넉한 128 (#15).
    /// </summary>
    private const int CacheLimit = 128;

    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, ImageSource Image)> _lru = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _failed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FailedRetry = TimeSpan.FromSeconds(10);
    private ImageSource? _default;
    private ImageSource? _launchpad;

    public ImageSource GetIcon(PinItem pin, IconStyle style)
    {
        if (pin is null) return Fallback(style);
        bool launchpad = pin.Kind == PinKind.Special && pin.Target.Equals("launchpad", StringComparison.OrdinalIgnoreCase)
                         && string.IsNullOrWhiteSpace(pin.IconPath);
        if (launchpad) return style == IconStyle.Mac ? MacLaunchpadIcon : LaunchpadIcon;

        string rawKey = $"pin|{pin.Kind}|{pin.Target}|{pin.IconPath}";
        // 윈도우(마이크로소프트) 패키지 앱이고 커스텀 아이콘이 없으면 맥 스타일은 흰 판 + unplated 로고
        string? windowsAumid = pin.Kind == PinKind.Aumid && string.IsNullOrWhiteSpace(pin.IconPath)
                               && PackageLogo.IsWindowsPackage(pin.Target) ? pin.Target.Trim() : null;
        return Styled(rawKey, style, windowsAumid, () =>
        {
            if (!string.IsNullOrWhiteSpace(pin.IconPath))
            {
                var img = LoadImageFile(pin.IconPath);
                if (img is not null) return img;
            }
            return pin.Kind switch
            {
                PinKind.Aumid => FromAumid(pin.Target.Trim()),
                PinKind.Exe => FromFile(Environment.ExpandEnvironmentVariables(pin.Target.Trim().Trim('"'))),
                _ => null,
            };
        });
    }

    public ImageSource GetIcon(AppWindowInfo window, IconStyle style)
    {
        if (window is null) return Fallback(style);
        // 패키지 앱 판정은 경로가 아니라 AUMID 형식("패밀리!앱ID")으로 — 설정·계산기 같은 시스템 앱은
        // WindowsApps 가 아닌 C:\Windows\SystemApps, ImmersiveControlPanel 등에 있고,
        // 최소화된 UWP 는 CoreWindow 가 프레임에서 떨어져 경로가 ApplicationFrameHost.exe 로만 보인다.
        bool packaged = AppsFolder.IsPackagedAumid(window.Aumid);
        bool frameHost = window.ProcessPath.EndsWith(@"\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        if (!packaged && (window.ProcessPath.Length == 0 || frameHost))
        {
            // 실제 앱을 아직 모름 (창 생성 직후 등) → 창 자체 아이콘, 캐시하지 않음 (다음 갱신 때 다시 시도)
            var raw = FromWindowHandle(window.Hwnd);
            return raw is null ? Fallback(style) : style == IconStyle.Mac ? (MacIconRenderer.Normalize(raw) ?? Fallback(style)) : raw;
        }
        string rawKey = packaged ? "aumid|" + window.Aumid!.ToLowerInvariant() : "exe|" + window.ProcessPath;
        string? windowsAumid = packaged && PackageLogo.IsWindowsPackage(window.Aumid!) ? window.Aumid : null;
        return Styled(rawKey, style, windowsAumid, () =>
            (packaged ? FromAumid(window.Aumid!) : null)
            ?? (frameHost ? null : FromFile(window.ProcessPath))
            ?? FromWindowHandle(window.Hwnd));
    }

    private readonly Dictionary<IntPtr, (ImageSource? Image, DateTime At)> _windowIcons = new();
    private static readonly TimeSpan WindowIconTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 창 자체 아이콘 (WM_GETICON ICON_BIG → ICON_SMALL2 → GCLP_HICON, SendMessageTimeout 100ms).
    /// 빌린 핸들이라 해제하지 않고 BitmapSource 로 복사해 Freeze. 창마다 5초 캐시 (크롬 프로필 아이콘처럼 창별로 다른 경우용).
    /// </summary>
    public ImageSource? GetWindowIcon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        lock (_gate)
        {
            if (_windowIcons.TryGetValue(hwnd, out var c) && DateTime.UtcNow - c.At < WindowIconTtl) return c.Image;
        }
        ImageSource? img = null;
        try { if (User32.IsWindow(hwnd)) img = FromWindowHandle(hwnd); }
        catch (Exception ex) { Log.Warn($"창 아이콘 조회 실패: {ex.Message}"); }
        lock (_gate)
        {
            if (_windowIcons.Count > 128)
                foreach (var k in _windowIcons.Where(kv => DateTime.UtcNow - kv.Value.At >= WindowIconTtl).Select(kv => kv.Key).ToList())
                    _windowIcons.Remove(k);
            _windowIcons[hwnd] = (img, DateTime.UtcNow);
        }
        return img;
    }

    /// <summary>패키지 앱 아이콘: shell:AppsFolder\AUMID 의 IShellItemImageFactory → 패키지 로고(Assets) 파일.</summary>
    private static ImageSource? FromAumid(string aumid)
    {
        string real = AppsFolder.RestoreAumidCase(aumid) ?? aumid;
        return FromShellItem(AppsFolder.ShellPathOf(real)) ?? PackageLogo.Load(AppsFolder.FamilyOf(real));
    }

    /// <summary>맥 스타일 렌더링 규칙이 바뀌면 올려서 이전 캐시 결과(예: 파란 타일 설정 아이콘)가 남지 않게.</summary>
    private const string MacStyleVersion = "mac2";

    /// <summary>
    /// 원본은 rawKey 로, 맥 스타일은 "mac2|rawKey" 로 따로 캐시.
    /// windowsAumid 가 있으면(윈도우 패키지 앱) 맥 스타일은 항상 흰 판 위: unplated 로고 → 없으면 타일 배경색을 지운 원본.
    /// </summary>
    private ImageSource Styled(string rawKey, IconStyle style, string? windowsAumid, Func<ImageSource?> loadRaw)
    {
        if (style != IconStyle.Mac) return GetOrAdd("orig|" + rawKey, loadRaw, DefaultIcon);
        // 맥 스타일: 원본은 맥 아이콘을 만들 때만 쓰고 캐시하지 않음 — 앱마다 256px 두 장을 들고 있지 않게 (#15).
        // (원본 스타일로 이미 캐시돼 있으면 그것을 씀)
        string macKey = MacStyleVersion + "|" + rawKey;
        if (TryGetCached(macKey) is { } hit) return hit;
        ImageSource raw = TryGetCached("orig|" + rawKey) ?? LoadUncached("orig|" + rawKey, loadRaw) ?? DefaultIcon;
        return GetOrAdd(macKey, () =>
        {
            if (windowsAumid is not null)
            {
                string real = AppsFolder.RestoreAumidCase(windowsAumid) ?? windowsAumid;
                var unplated = PackageLogo.LoadUnplated(real);
                if (unplated is not null) return MacIconRenderer.OnPlate(unplated, removeBackground: false);
                return raw is BitmapSource rb && !ReferenceEquals(raw, _default) ? MacIconRenderer.OnPlate(rb, removeBackground: true) : null;
            }
            return raw is BitmapSource bs && !ReferenceEquals(raw, _default) ? MacIconRenderer.Normalize(bs) : null;
        }, MacDefaultIcon);
    }

    private ImageSource Fallback(IconStyle style) => style == IconStyle.Mac ? MacDefaultIcon : DefaultIcon;

    // ───────────────────────── 캐시 ─────────────────────────

    private ImageSource? TryGetCached(string key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>캐시에 넣지 않고 한 번 불러옴 (실패 표시는 GetOrAdd 와 같이 — 10초 동안 다시 시도 안 함).</summary>
    private ImageSource? LoadUncached(string key, Func<ImageSource?> factory)
    {
        lock (_gate)
        {
            if (_failed.TryGetValue(key, out var until) && DateTime.UtcNow < until) return null;
        }
        ImageSource? img = null;
        try { img = factory(); }
        catch (Exception ex) { Log.Error($"아이콘 로드 실패: {key}", ex); }
        if (img is null)
        {
            lock (_gate)
            {
                if (_failed.Count > 256) _failed.Clear();
                _failed[key] = DateTime.UtcNow + FailedRetry;
            }
            return null;
        }
        if (img.CanFreeze && !img.IsFrozen) img.Freeze();
        return img;
    }

    /// <summary>메모리 진단용: 캐시 개수·픽셀 크기 합 (MemoryReport).</summary>
    public string CacheStats()
    {
        lock (_gate)
        {
            long bytes = 0;
            foreach (var (_, img) in _lru)
                if (img is BitmapSource b) bytes += (long)b.PixelWidth * b.PixelHeight * 4;
            return $"아이콘 캐시 {_lru.Count}개 ~{bytes / (1024 * 1024)}MB, 창 아이콘 {_windowIcons.Count}개";
        }
    }

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

        lock (_gate)
        {
            // 실패한 키는 잠시(10초) 기본 아이콘만 돌려주고, 그 뒤 다시 시도 (실패 결과를 영구 캐시하지 않음)
            if (_failed.TryGetValue(key, out var until) && DateTime.UtcNow < until) return fallback;
        }

        ImageSource? img = null;
        try { img = factory(); }
        catch (Exception ex) { Log.Error($"아이콘 로드 실패: {key}", ex); }
        if (img is null)
        {
            lock (_gate)
            {
                if (_failed.Count > 256) _failed.Clear();
                _failed[key] = DateTime.UtcNow + FailedRetry;
            }
            return fallback;
        }
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
    internal static BitmapSource? HBitmapToBitmapSource(IntPtr hbmp)
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

    private ImageSource LaunchpadIcon => _launchpad ??= CreateAllAppsIcon();

    private ImageSource? _macDefault, _macLaunchpad;

    /// <summary>맥 스타일 기본 아이콘: 밝은 판 위에 시스템 기본 앱 아이콘.</summary>
    private ImageSource MacDefaultIcon => _macDefault ??=
        (DefaultIcon is BitmapSource b ? MacIconRenderer.Plate(b) : MacIconRenderer.Plate(null));

    private ImageSource MacLaunchpadIcon => _macLaunchpad ??= MacIconRenderer.AllApps();

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

    /// <summary>독 "앱 모음" (원본 스타일, #d26): 몽독 하늘색~연보라 둥근 판 + 흰 2x2 둥근 칸 (MacIconRenderer.AllApps 와 같은 그림).</summary>
    private static ImageSource CreateAllAppsIcon()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var plate = new Rect(0, 0, 64, 64);
            dc.DrawRoundedRectangle(new LinearGradientBrush(MacIconRenderer.SkyTop, MacIconRenderer.SkyBottom, 75), null, plate, 14, 14);
            MacIconRenderer.DrawAllAppsTiles(dc, plate);
        }
        var di = new DrawingImage(drawing);
        di.Freeze();
        return di;
    }
}
