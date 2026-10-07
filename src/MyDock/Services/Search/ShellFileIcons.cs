using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyDock.Native;

namespace MyDock.Services.Search;

/// <summary>
/// 검색 결과 파일·폴더 아이콘 (셸 시스템 이미지 목록 48px, Frozen).
/// 보통은 확장자별 아이콘을 디스크에 접근하지 않고(SHGFI_USEFILEATTRIBUTES) 받아 확장자로 캐시,
/// 파일마다 아이콘이 다른 종류(exe·lnk·ico·url)만 실제 경로로 받아 경로로 캐시.
/// UI 스레드에서 호출 (Spotlight 가 Background 우선순위로 하나씩).
/// </summary>
public static class ShellFileIcons
{
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int SHIL_EXTRALARGE = 0x2; // 48px

    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr" };
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path, bool isFolder)
    {
        string ext = isFolder ? "" : Path.GetExtension(path);
        bool perFile = !isFolder && PerFileExtensions.Contains(ext);
        string key = isFolder ? "<folder>" : perFile ? path : (ext.Length == 0 ? "<noext>" : ext);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? icon = perFile
            ? Load(path, 0, 0)
            : Load(isFolder ? "folder" : "file" + ext, isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, SHGFI_USEFILEATTRIBUTES);
        if (Cache.Count > 300) Cache.Clear();
        Cache[key] = icon;
        return icon;
    }

    private static ImageSource? Load(string path, uint attributes, uint extraFlags)
    {
        IntPtr hIcon = IntPtr.Zero;
        IImageList? list = null;
        try
        {
            var sfi = new SHFILEINFO();
            if (Shell32.SHGetFileInfo(path, attributes, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), Shell32.SHGFI_SYSICONINDEX | extraFlags) == IntPtr.Zero)
                return null;
            var iid = typeof(IImageList).GUID;
            if (Shell32.SHGetImageList(SHIL_EXTRALARGE, ref iid, out list) != 0 || list is null) return null;
            if (list.GetIcon(sfi.iIcon, Shell32.ILD_TRANSPARENT, out hIcon) != 0 || hIcon == IntPtr.Zero) return null;
            var bs = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        catch (Exception ex)
        {
            Log.Warn($"검색 결과 아이콘 실패 ({ex.Message})");
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) User32.DestroyIcon(hIcon);
            if (list is not null) Marshal.ReleaseComObject(list);
        }
    }
}
