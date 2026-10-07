using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using MyDock.Models;
using MyDock.Services;

namespace MyDock.Views;

/// <summary>
/// 탐색기/바탕화면에서 독으로 끌어 놓은 파일·폴더를 핀으로 바꾼다.
/// - .exe : 그 exe (이름 = 파일 설명, 없으면 파일 이름)
/// - .lnk : IShellLink 로 대상·인자를 읽어 대상이 exe 면 exe 핀(창 매칭이 됨), 아니면 바로 가기 파일 자체를 실행하는 핀.
///          바로 가기에 .ico 아이콘이 지정돼 있으면 그 아이콘을 가져옴
/// - 폴더 : 그 폴더를 여는 핀 (Target = 폴더 경로, ShellExecute 가 탐색기로 연다.
///          explorer.exe + 인자로 만들면 모든 탐색기 창이 이 핀에 묶이므로 쓰지 않음)
/// - .url 등 그 밖의 파일 : 파일 자체를 기본 프로그램으로 여는 핀
/// - C:\Program Files\WindowsApps\ 안의 exe (직접 또는 바로 가기 대상) : 버전 포함 경로는 업데이트 때 깨지므로
///   패키지 패밀리로 AUMID 를 찾아 Aumid 핀으로. 못 찾으면 거절(null, 로그).
/// </summary>
internal static class DockDropFiles
{
    public static PinItem? CreatePin(string path, ISettingsService settings)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                string name = Path.GetFileName(path.TrimEnd('\\'));
                return new PinItem { Name = name.Length > 0 ? name : path, Kind = PinKind.Exe, Target = path };
            }
            if (!File.Exists(path)) return null;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            string fileName = Path.GetFileNameWithoutExtension(path);
            switch (ext)
            {
                case ".exe":
                    if (AppsFolder.IsWindowsAppsPath(path)) return FromPackagedExe(path);
                    return new PinItem { Name = ExeName(path), Kind = PinKind.Exe, Target = path };
                case ".lnk":
                    return FromShortcut(path, fileName, settings);
                default:
                    return new PinItem { Name = fileName, Kind = PinKind.Exe, Target = path };
            }
        }
        catch (Exception ex)
        {
            Log.Error($"끌어 놓은 파일로 핀 만들기 실패: {path}", ex);
            return null;
        }
    }

    private static string ExeName(string exe)
    {
        try
        {
            string? desc = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) return desc;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }

    /// <summary>WindowsApps 안의 exe → 패키지 패밀리의 AUMID 핀. 못 찾으면 null (버전 경로를 저장하지 않으려고 거절).</summary>
    private static PinItem? FromPackagedExe(string exe)
    {
        string? family = AppsFolder.FamilyFromWindowsAppsPath(exe);
        string? aumid = family is null ? null : AppsFolder.FindAumidByFamily(family);
        if (string.IsNullOrEmpty(aumid))
        {
            Log.Warn($"끌어 놓은 패키지 앱 exe 의 AUMID 를 찾지 못해 핀으로 만들지 않음 (버전 경로 저장 금지): {exe}");
            return null;
        }
        aumid = AppsFolder.RestoreAumidCase(aumid) ?? aumid;
        Log.Info($"끌어 놓은 패키지 앱 exe → AUMID 핀: {aumid}");
        return new PinItem { Name = AppsFolder.GetAppDisplayName(aumid) ?? ExeName(exe), Kind = PinKind.Aumid, Target = aumid };
    }

    private static PinItem? FromShortcut(string lnk, string name, ISettingsService settings)
    {
        var fallback = new PinItem { Name = name, Kind = PinKind.Exe, Target = lnk };
        if (!TryReadShortcut(lnk, out string target, out string args, out string iconPath))
            return fallback;

        string expanded = Environment.ExpandEnvironmentVariables(target);
        // 대상이 실제 exe 일 때만 풀어서 저장 (스토어 앱·MSI 광고 바로 가기 등은 경로가 없거나 엉뚱함 → 바로 가기 그대로)
        if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(expanded))
            return fallback;
        // 패키지 앱 exe 를 가리키는 바로 가기 → AUMID 핀 (못 찾으면 바로 가기 파일 자체를 실행하는 핀 — 버전 경로는 저장 안 함)
        if (AppsFolder.IsWindowsAppsPath(expanded))
        {
            var packaged = FromPackagedExe(expanded);
            if (packaged is not null) packaged.Name = name;
            return packaged ?? fallback;
        }

        var pin = new PinItem
        {
            Name = name,
            Kind = PinKind.Exe,
            Target = target,
            Arguments = string.IsNullOrWhiteSpace(args) ? null : args,
        };
        string icon = Environment.ExpandEnvironmentVariables(iconPath);
        if (icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(icon))
        {
            try { pin.IconPath = settings.ImportIcon(icon); }
            catch (Exception ex) { Log.Warn($"바로 가기 아이콘 가져오기 실패: {ex.Message}"); }
        }
        return pin;
    }

    /// <summary>IShellLinkW + IPersistFile 로 바로 가기 읽기 (UI 스레드 = STA).</summary>
    private static bool TryReadShortcut(string lnk, out string target, out string args, out string iconPath)
    {
        target = args = iconPath = "";
        object? obj = null;
        try
        {
            obj = new ShellLinkCoClass();
            ((IPersistFile)obj).Load(lnk, 0 /* STGM_READ */);
            var link = (IShellLinkW)obj;
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            target = sb.ToString();
            sb.Clear();
            link.GetArguments(sb, sb.Capacity);
            args = sb.ToString();
            sb.Clear();
            link.GetIconLocation(sb, sb.Capacity, out _);
            iconPath = sb.ToString();
            return target.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"바로 가기 읽기 실패 {lnk}: {ex.Message}");
            return false;
        }
        finally
        {
            if (obj != null) Marshal.ReleaseComObject(obj);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
