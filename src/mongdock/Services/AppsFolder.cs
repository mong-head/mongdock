using System.IO;
using System.Runtime.InteropServices;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// shell:AppsFolder (시작 메뉴의 "모든 앱") 관련 도우미.
/// 패키지 앱 항목의 파싱 이름이 정확한 대소문자의 AUMID(예: Claude_pzs8sxrjxfjjc!Claude)이므로
/// AUMID 대소문자 복원·패키지 패밀리로 AUMID 찾기·표시 이름 조회에 사용한다.
/// </summary>
internal static class AppsFolder
{
    private static readonly object Gate = new();
    private static List<(string ParsingName, string DisplayName)>? _cache;
    private static DateTime _cacheTime;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>AppsFolder 의 모든 항목 (파싱 이름, 표시 이름). 60초 캐시.</summary>
    public static IReadOnlyList<(string ParsingName, string DisplayName)> Enumerate(bool refresh = false)
    {
        lock (Gate)
        {
            if (!refresh && _cache is not null && DateTime.UtcNow - _cacheTime < CacheTtl) return _cache;
            var list = new List<(string, string)>();
            IShellItem? folder = null;
            IntPtr enumPtr = IntPtr.Zero;
            IEnumShellItems? en = null;
            try
            {
                var iid = typeof(IShellItem).GUID;
                int hr = Shell32.SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out object o);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new COMException("SHCreateItemFromParsingName", hr);
                folder = (IShellItem)o;

                var bhid = ShellConst.BHID_EnumItems;
                var eiid = typeof(IEnumShellItems).GUID;
                hr = folder.BindToHandler(IntPtr.Zero, ref bhid, ref eiid, out enumPtr);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new COMException("BindToHandler", hr);
                en = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);

                while (en.Next(1, out IShellItem? item, out uint fetched) == 0 && fetched == 1 && item is not null)
                {
                    try
                    {
                        string? parsing = GetDisplayName(item, ShellConst.SIGDN_PARENTRELATIVEPARSING);
                        string? display = GetDisplayName(item, ShellConst.SIGDN_NORMALDISPLAY);
                        if (!string.IsNullOrEmpty(parsing)) list.Add((parsing, display ?? parsing));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("shell:AppsFolder 열거 실패", ex);
            }
            finally
            {
                if (en is not null) Marshal.ReleaseComObject(en);
                if (enumPtr != IntPtr.Zero) Marshal.Release(enumPtr);
                if (folder is not null) Marshal.ReleaseComObject(folder);
            }

            _cache = list;
            _cacheTime = DateTime.UtcNow;
            return list;
        }
    }

    internal static string? GetDisplayName(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>
    /// 대소문자가 깨진 AUMID 를 실제 AUMID 로 복원. 못 찾으면 null.
    /// 1) shell:AppsFolder 열거 (시작 메뉴에 보이는 앱)
    /// 2) 패키지 저장소 레지스트리 (HKCU\...\AppModel\Repository\Packages\&lt;FullName&gt;\&lt;AppId&gt;) — 시작 메뉴에 안 보이는 앱
    ///    (예: Claude 는 AppsFolder 열거에 나오지 않음)
    /// </summary>
    public static string? RestoreAumidCase(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;
        foreach (var (parsing, _) in Enumerate())
            if (string.Equals(parsing, aumid, StringComparison.OrdinalIgnoreCase)) return parsing;
        return RestoreFromPackageRepository(aumid);
    }

    private const string PackageRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>패키지 저장소 레지스트리에서 "Name_..._PublisherId" 키와 그 아래 앱 ID 키 이름으로 대소문자 복원 (읽기 전용).</summary>
    internal static string? RestoreFromPackageRepository(string aumid)
    {
        try
        {
            int bang = aumid.IndexOf('!');
            if (bang <= 0) return null;
            string family = aumid[..bang], appId = aumid[(bang + 1)..];
            int us = family.IndexOf('_');
            if (us <= 0) return null;
            string name = family[..us], publisher = family[(us + 1)..];

            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PackageRepositoryKey, writable: false);
            if (root is null) return null;
            foreach (string full in root.GetSubKeyNames())
            {
                string[] parts = full.Split('_');
                if (parts.Length < 5 ||
                    !parts[0].Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    !parts[^1].Equals(publisher, StringComparison.OrdinalIgnoreCase))
                    continue;
                using var pkg = root.OpenSubKey(full, writable: false);
                string? realApp = pkg?.GetSubKeyNames().FirstOrDefault(n => n.Equals(appId, StringComparison.OrdinalIgnoreCase));
                if (realApp is not null) return parts[0] + "_" + parts[^1] + "!" + realApp;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"패키지 저장소에서 AUMID 복원 실패: {aumid} ({ex.Message})");
        }
        return null;
    }

    /// <summary>패키지 패밀리명(예: Claude_pzs8sxrjxfjjc)에 속한 첫 AUMID. 없으면 null.</summary>
    public static string? FindAumidByFamily(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;
        string prefix = family + "!";
        foreach (var (parsing, _) in Enumerate())
            if (parsing.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return parsing;
        return null;
    }

    /// <summary>AUMID 의 표시 이름 (시작 메뉴 이름). 실패 시 null.</summary>
    public static string? GetAppDisplayName(string aumid)
    {
        foreach (var (parsing, display) in Enumerate())
            if (string.Equals(parsing, aumid, StringComparison.OrdinalIgnoreCase)) return display;
        return null;
    }

    private static readonly Dictionary<string, bool> PackagedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 설치된 패키지 앱의 AUMID 인지 ("패밀리!앱ID" 형식이고 AppsFolder/패키지 저장소에 있음). 결과 캐시.
    /// Chrome("Chrome")·탐색기("Microsoft.Windows.Explorer") 같은 데스크톱 앱의 명시적 AUMID 는 false.
    /// </summary>
    public static bool IsPackagedAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        int bang = aumid.IndexOf('!');
        if (bang <= 0 || bang == aumid.Length - 1 || aumid.IndexOf('_') <= 0 || aumid.IndexOf('_') > bang) return false;
        lock (PackagedCache)
            if (PackagedCache.TryGetValue(aumid, out bool known)) return known;
        bool result = RestoreAumidCase(aumid) is not null;
        lock (PackagedCache)
        {
            if (PackagedCache.Count > 512) PackagedCache.Clear();
            PackagedCache[aumid] = result;
        }
        return result;
    }

    /// <summary>"Claude_pzs8sxrjxfjjc!Claude" → "Claude_pzs8sxrjxfjjc".</summary>
    public static string FamilyOf(string aumid)
    {
        int i = aumid.IndexOf('!');
        return i > 0 ? aumid[..i] : aumid;
    }

    public static bool IsWindowsAppsPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "C:\Program Files\WindowsApps\Claude_2.26454.0.0_x64__pzs8sxrjxfjjc\app\claude.exe" → "Claude_pzs8sxrjxfjjc".
    /// 패키지 전체 이름 = Name_Version_Arch_ResourceId_PublisherId. 실패 시 null.
    /// </summary>
    public static string? FamilyFromWindowsAppsPath(string? path)
    {
        if (!IsWindowsAppsPath(path)) return null;
        int i = path!.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) + @"\WindowsApps\".Length;
        int j = path.IndexOf('\\', i);
        string full = j > i ? path[i..j] : path[i..];
        string[] parts = full.Split('_');
        if (parts.Length < 5) return null;
        return parts[0] + "_" + parts[^1];
    }

    /// <summary>shell:AppsFolder\AUMID 형태의 파싱 이름.</summary>
    public static string ShellPathOf(string aumid) => @"shell:AppsFolder\" + aumid;

    /// <summary>파일 경로의 실제 대소문자 복원 (존재하지 않으면 원래 문자열).</summary>
    public static string RestorePathCase(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) return path;
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return path;
            string result = root.ToUpperInvariant();
            foreach (string part in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Directory.EnumerateFileSystemEntries(result, part).FirstOrDefault();
                if (match is null) return path;
                result = match;
            }
            return result;
        }
        catch
        {
            return path;
        }
    }
}
