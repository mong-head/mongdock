using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 윈도우 작업 표시줄에 고정된 앱을 독 핀으로 (작업 표시줄 순서대로). 레지스트리·파일은 읽기만 한다.
/// - 순서: HKCU\...\Explorer\Taskband 의 Favorites (바이너리). 형식(실측, Windows 11):
///   항목마다 [0x00][UInt32 크기][ITEMIDLIST(크기 바이트, 끝의 0 종료자 포함)] … 마지막 [0xFF].
///   각 PIDL 을 SHGetNameFromIDList(SIGDN_DESKTOPABSOLUTEPARSING) 로 풀면
///   스토어 앱(AppsFolder 항목)은 AUMID(예: Claude_pzs8sxrjxfjjc!Claude), 데스크톱 앱은
///   %APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\*.lnk 경로가 나온다.
/// - Favorites 를 못 읽으면 User Pinned\TaskBar 의 .lnk 를 이름순으로 (순서는 알 수 없음).
/// - 바로 가기는 <see cref="PinFactory"/> 로 해석 (exe 면 exe 핀, WindowsApps 안이면 AUMID). 확실히 해석 못 하는 항목
///   (대상이 exe 가 아닌 바로 가기 — 핀이 작업 표시줄 폴더의 .lnk 를 가리키게 되면 작업 표시줄 고정을 풀 때 깨지므로)은 건너뛰고 로그.
/// - 파일 탐색기는 독의 Finder 와 겹치므로 뺀다.
/// </summary>
public static class TaskbarPins
{
    private const string TaskbandKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";

    private static string PinnedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>작업 표시줄 고정 앱을 작업 표시줄 순서대로 핀으로 (해석 못 한 항목·파일 탐색기 제외, 중복 제거).</summary>
    public static List<PinItem> Read(ISettingsService settings)
    {
        var result = new List<PinItem>();
        try
        {
            List<(string Parsing, string? Display)>? items = ReadFavorites();
            if (items is null)
            {
                items = Directory.Exists(PinnedFolder)
                    ? Directory.GetFiles(PinnedFolder, "*.lnk").OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                        .Select(f => (f, (string?)null)).ToList()
                    : new List<(string, string?)>();
                Log.Info($"작업 표시줄 고정 순서(Favorites)를 못 읽어 바로 가기 폴더를 이름순으로 ({items.Count}개)");
            }
            foreach (var (parsing, display) in items)
            {
                PinItem? pin = Resolve(parsing, display, settings);
                if (pin is null) continue;
                if (IsFileExplorer(pin))
                {
                    Log.Info("작업 표시줄 고정: 파일 탐색기는 Finder 와 겹쳐 뺌");
                    continue;
                }
                if (result.Any(p => SamePin(p, pin))) continue;
                result.Add(pin);
            }
        }
        catch (Exception ex)
        {
            Log.Error("작업 표시줄 고정 앱 읽기 실패", ex);
        }
        Log.Info($"작업 표시줄 고정 앱 {result.Count}개: '{string.Join(", ", result.Select(p => p.Name))}'");
        return result;
    }

    /// <summary>작업 표시줄 고정 앱 중 독에 없는 것만 독 고정 목록 끝에 작업 표시줄 순서대로 추가. 추가한 개수. 저장은 호출자.</summary>
    public static int AddMissingTo(Settings current, ISettingsService settings)
    {
        int added = AddMissing(current.Pins, Read(settings));
        Log.Info($"작업 표시줄 고정 앱 가져오기: {added}개 추가");
        return added;
    }

    /// <summary>add 중 pins 에 없는 것만 끝에 추가 (같은 앱 = 종류·대상·인자가 같음). 추가한 개수.</summary>
    public static int AddMissing(List<PinItem> pins, IEnumerable<PinItem> add)
    {
        int n = 0;
        foreach (var pin in add)
        {
            if (pins.Any(p => SamePin(p, pin))) continue;
            pins.Add(pin);
            n++;
        }
        return n;
    }

    private static bool SamePin(PinItem a, PinItem b)
    {
        if (a.Kind == PinKind.Separator || b.Kind == PinKind.Separator) return false;
        if (a.Kind != b.Kind) return false;
        if (!string.Equals(NormalizeTarget(a.Target), NormalizeTarget(b.Target), StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals((a.Arguments ?? "").Trim(), (b.Arguments ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTarget(string target)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(target)); }
        catch { return target; }
    }

    private static bool IsFileExplorer(PinItem pin)
    {
        if (pin.Kind != PinKind.Exe || !string.IsNullOrWhiteSpace(pin.Arguments)) return false;
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        return string.Equals(NormalizeTarget(pin.Target), explorer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>파싱 이름(AUMID 또는 .lnk 경로) → 핀. 확실하지 않으면 null + 로그. display = 작업 표시줄에 보이는 이름.</summary>
    private static PinItem? Resolve(string name, string? display, ISettingsService settings)
    {
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(name))
        {
            string file = Path.GetFileName(name);
            if (!File.Exists(name))
            {
                Log.Info($"작업 표시줄 고정 건너뜀 (바로 가기 없음): '{file}'");
                return null;
            }
            PinItem? pin = PinFactory.CreatePin(name, settings);
            // 바로 가기 자체를 실행하는 핀 = 대상을 못 풀었음 → 작업 표시줄 고정을 풀면 깨지므로 건너뜀
            if (pin is null || string.Equals(NormalizeTarget(pin.Target), NormalizeTarget(name), StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"작업 표시줄 고정 건너뜀 (바로 가기 대상을 해석 못 함): '{file}'");
                return null;
            }
            return pin;
        }
        // AppsFolder 항목: 파싱 이름이 패키지 앱 AUMID (패키지 패밀리!앱 ID). 설치 확인(대소문자 복원)된 것만.
        // "Chrome"·"Microsoft.Windows.Explorer" 같은 데스크톱 앱의 명시적 AUMID·알려진 폴더 경로는 확실하지 않아 건너뜀.
        if (name.Contains('!') && !name.Contains('\\') && !name.StartsWith("::", StringComparison.Ordinal))
        {
            string? aumid = AppsFolder.RestoreAumidCase(name);
            if (aumid is null)
            {
                Log.Info($"작업 표시줄 고정 건너뜀 (설치된 패키지 앱에 없는 AUMID): {name}");
                return null;
            }
            string label = AppsFolder.GetAppDisplayName(aumid) ?? display ?? aumid;
            return new PinItem { Name = label, Kind = PinKind.Aumid, Target = aumid };
        }
        Log.Info($"작업 표시줄 고정 건너뜀 (알 수 없는 항목): '{(name.Length > 80 ? name[..80] + "…" : name)}'");
        return null;
    }

    // ───────────────────────── Taskband Favorites ─────────────────────────

    /// <summary>Favorites 를 항목별 (파싱 이름, 표시 이름) 목록으로 (작업 표시줄 순서). 값이 없거나 형식이 다르면 null.</summary>
    private static List<(string Parsing, string? Display)>? ReadFavorites()
    {
        byte[]? data;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TaskbandKey);
            data = key?.GetValue("Favorites") as byte[];
        }
        catch (Exception ex)
        {
            Log.Warn("Taskband Favorites 읽기 실패", ex);
            return null;
        }
        if (data is null || data.Length == 0) return null;

        var names = new List<(string Parsing, string? Display)>();
        int p = 0;
        while (p < data.Length)
        {
            byte tag = data[p];
            if (tag == 0xFF) return names; // 끝
            if (tag != 0x00 || p + 5 > data.Length)
            {
                Log.Warn($"Taskband Favorites 형식이 예상과 다름 (오프셋 {p}, 0x{tag:X2})");
                return names.Count > 0 ? names : null;
            }
            int size = BitConverter.ToInt32(data, p + 1);
            if (size < 2 || p + 5 + size > data.Length)
            {
                Log.Warn($"Taskband Favorites 항목 크기 이상 (오프셋 {p}, {size})");
                return names.Count > 0 ? names : null;
            }
            string? name = PidlName(data, p + 5, size, SIGDN_DESKTOPABSOLUTEPARSING);
            if (name is not null) names.Add((name, PidlName(data, p + 5, size, SIGDN_NORMALDISPLAY)));
            else Log.Info($"작업 표시줄 고정 항목 {names.Count} 이름 없음 → 건너뜀");
            p += 5 + size;
        }
        return names;
    }

    private const uint SIGDN_NORMALDISPLAY = 0x00000000;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr ppszName);

    /// <summary>바이트 배열 속 ITEMIDLIST 를 (검증 후) 파싱 이름으로. 구조가 이상하면 null.</summary>
    private static string? PidlName(byte[] data, int offset, int size, uint sigdn)
    {
        // ITEMIDLIST 구조 확인: SHITEMID(cb UInt16 + 내용) 의 연속, cb=0 으로 끝나야 함 — 범위를 넘으면 쓰지 않음
        int q = offset, end = offset + size;
        while (true)
        {
            if (q + 2 > end) return null;
            int cb = BitConverter.ToUInt16(data, q);
            if (cb == 0) break;
            if (cb < 2) return null;
            q += cb;
        }
        IntPtr mem = Marshal.AllocCoTaskMem(size + 2);
        try
        {
            Marshal.Copy(data, offset, mem, size);
            Marshal.WriteInt16(mem, size, 0); // 여분 종료자
            if (SHGetNameFromIDList(mem, sigdn, out IntPtr psz) != 0 || psz == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(psz); }
            finally { Marshal.FreeCoTaskMem(psz); }
        }
        catch (Exception ex)
        {
            Log.Warn("작업 표시줄 고정 항목 이름 읽기 실패", ex);
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(mem);
        }
    }
}
