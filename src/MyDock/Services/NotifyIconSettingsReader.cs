using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using T = MyDock.Native.TrayApi;

namespace MyDock.Services;

/// <summary>
/// 윈도우 11 의 트레이 아이콘 설정 (HKCU\Control Panel\NotifyIconSettings) 한 항목.
/// 하위 키 이름은 64비트 숫자 ID, 값: ExecutablePath(앞이 {KNOWNFOLDERID} 일 수 있음), UID(DWORD) 또는 IconGuid(문자열),
/// IsPromoted(DWORD, 1 = "작업 표시줄에 항상 표시" — 값이 없으면 한 번도 켠 적 없음 = 숨김), InitialTooltip, Publisher, IconSnapshot.
/// 상위 키의 UIOrderList(REG_BINARY) 는 하위 키 ID(UInt64 리틀 엔디언)를 설정 화면 순서대로 나열한 것.
/// </summary>
public sealed class NotifyIconSetting
{
    public required ulong Id { get; init; }
    /// <summary>KNOWNFOLDER 접두를 풀어낸 전체 경로 (모르면 원문).</summary>
    public required string ExecutablePath { get; init; }
    public uint? Uid { get; init; }
    public Guid IconGuid { get; init; }
    public bool IsPromoted { get; init; }
    /// <summary>IsPromoted 값이 있음 (사용자가 윈도우 설정에서 한 번이라도 켜거나 끔).</summary>
    public bool IsPromotedSet { get; init; }
    public string Tooltip { get; init; } = "";
    /// <summary>UIOrderList 안의 위치 (없으면 int.MaxValue).</summary>
    public int Order { get; init; } = int.MaxValue;
}

/// <summary>
/// NotifyIconSettings 를 <b>읽기만</b> 하는 공유 리더. 키가 바뀌면(윈도우 설정에서 아이콘 켜기/끄기) 다시 읽고
/// 승격·순서가 달라졌으면 <see cref="Changed"/> (UI 스레드). 윈도우 10 처럼 키가 없으면 <see cref="IsAvailable"/> = false.
/// 스레드 안전 (스냅숏 교체).
/// </summary>
public sealed class NotifyIconSettingsReader
{
    private const string KeyPath = @"Control Panel\NotifyIconSettings";

    private static readonly Lazy<NotifyIconSettingsReader> _shared = new(() =>
    {
        var r = new NotifyIconSettingsReader();
        r.Reload(raise: false);
        r.StartWatch();
        return r;
    });

    public static NotifyIconSettingsReader Shared => _shared.Value;

    private readonly System.Windows.Threading.Dispatcher? _dispatcher = System.Windows.Application.Current?.Dispatcher;
    private volatile Snapshot _snap = Snapshot.Empty;

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(false, Array.Empty<NotifyIconSetting>());
        public readonly bool Available;
        public readonly IReadOnlyList<NotifyIconSetting> Entries;
        public readonly Dictionary<Guid, NotifyIconSetting> ByGuid = new();
        public readonly Dictionary<string, List<NotifyIconSetting>> ByPath = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> FileNames = new(StringComparer.OrdinalIgnoreCase);

        public Snapshot(bool available, IReadOnlyList<NotifyIconSetting> entries)
        {
            Available = available;
            Entries = entries;
            foreach (var e in entries)
            {
                if (e.IconGuid != Guid.Empty) ByGuid[e.IconGuid] = e;
                if (e.ExecutablePath.Length == 0) continue;
                if (!ByPath.TryGetValue(e.ExecutablePath, out var l)) ByPath[e.ExecutablePath] = l = new();
                l.Add(e);
                FileNames.Add(Path.GetFileName(e.ExecutablePath));
            }
        }

        /// <summary>바/⌃ 결과에 영향을 주는 부분만 비교 (IconSnapshot 갱신 등으로 키가 자주 바뀌어도 UI 를 흔들지 않게).</summary>
        public string Signature() => string.Join(";", Entries.Select(e => $"{e.Id}:{(e.IsPromoted ? 1 : 0)}:{e.Order}"));
    }

    private NotifyIconSettingsReader() { }

    /// <summary>윈도우 11 의 설정 키가 있는지 (없으면 승격 규칙 생략).</summary>
    public bool IsAvailable => _snap.Available;
    public IReadOnlyList<NotifyIconSetting> Entries => _snap.Entries;

    /// <summary>승격 여부·순서가 바뀜 (UI 스레드).</summary>
    public event EventHandler? Changed;

    /// <summary>트레이 아이콘을 가진 적 있는 exe 파일 이름 (소문자 무시) — 빠진 앱 재등록 요청용.</summary>
    public bool IsKnownFileName(string fileName) => _snap.FileNames.Contains(fileName);

    /// <summary>트레이 아이콘을 가진 적 있는 exe 전체 경로인지.</summary>
    public bool IsKnownPath(string path) => _snap.ByPath.ContainsKey(path);

    /// <summary>
    /// 아이콘 ↔ 설정 매칭: GUID → (exe 경로 + uID) → 같은 경로 항목이 하나뿐이면 그것. 없으면 null.
    /// </summary>
    public NotifyIconSetting? Match(TrayIconInfo icon)
    {
        var s = _snap;
        if (!s.Available) return null;
        if (icon.Guid != Guid.Empty && s.ByGuid.TryGetValue(icon.Guid, out var g)) return g;
        if (icon.ProcessPath.Length == 0 || !s.ByPath.TryGetValue(icon.ProcessPath, out var list)) return null;
        // 대소문자만 다른 경로가 따로 저장되는 경우가 있음 (실측: 카카오톡 "KakaoTalk.exe" IsPromoted=1 과
        // "kakaotalk.exe" 값 없음이 같이 있고, 실행 경로는 "c:\program files (x86)\kakao\...\kakaotalk.exe" 로 둘 다와 다름).
        // 순서: 대소문자까지 같은 경로 → 사용자가 IsPromoted 를 직접 정한 항목(켬 우선) → 아무거나.
        NotifyIconSetting? chosen = null, loose = null;
        foreach (var e in list)
        {
            if (e.Uid != icon.Uid || e.IconGuid != Guid.Empty) continue;
            if (string.Equals(e.ExecutablePath, icon.ProcessPath, StringComparison.Ordinal)) return e;
            if (e.IsPromotedSet && (chosen is null || e.IsPromoted && !chosen.IsPromoted)) chosen = e;
            loose ??= e;
        }
        if (chosen is not null) return chosen;
        if (loose is not null) return loose;
        if (icon.Guid == Guid.Empty && list.Count == 1) return list[0];
        return null;
    }

    // ───────────────────────── 읽기 ─────────────────────────

    private void Reload(bool raise)
    {
        Snapshot next;
        try { next = Read(); }
        catch (Exception ex)
        {
            Log.Warn($"NotifyIconSettings 읽기 실패: {ex.Message}");
            return;
        }
        var old = _snap;
        _snap = next;
        if (!raise || old.Signature() == next.Signature()) return;
        Log.Info($"윈도우 트레이 아이콘 설정 바뀜 (항목 {next.Entries.Count}, 항상 표시 {next.Entries.Count(e => e.IsPromoted)})");
        var d = _dispatcher;
        if (d is null) return;
        d.BeginInvoke(() =>
        {
            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("NotifyIconSettings Changed 처리 예외", ex); }
        });
    }

    private static Snapshot Read()
    {
        using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        if (root is null) return Snapshot.Empty;

        var order = new Dictionary<ulong, int>();
        if (root.GetValue("UIOrderList") is byte[] bytes)
            for (int i = 0; i + 8 <= bytes.Length; i += 8)
                order.TryAdd(BitConverter.ToUInt64(bytes, i), i / 8);

        var list = new List<NotifyIconSetting>();
        foreach (var name in root.GetSubKeyNames())
        {
            if (!ulong.TryParse(name, out ulong id)) continue;
            using var k = root.OpenSubKey(name, writable: false);
            if (k is null) continue;
            string raw = k.GetValue("ExecutablePath") as string ?? "";
            Guid guid = Guid.Empty;
            if (k.GetValue("IconGuid") is string gs) Guid.TryParse(gs, out guid);
            list.Add(new NotifyIconSetting
            {
                Id = id,
                ExecutablePath = ExpandPath(raw),
                Uid = k.GetValue("UID") is int uid ? unchecked((uint)uid) : null,
                IconGuid = guid,
                IsPromoted = k.GetValue("IsPromoted") is int p && p != 0,
                IsPromotedSet = k.GetValue("IsPromoted") is int,
                Tooltip = k.GetValue("InitialTooltip") as string ?? "",
                Order = order.TryGetValue(id, out int o) ? o : int.MaxValue,
            });
        }
        return new Snapshot(true, list);
    }

    private static readonly Dictionary<Guid, string?> KnownFolders = new();

    /// <summary>"{6D809377-...}\Kakao\x.exe" → "C:\Program Files\Kakao\x.exe". 환경 변수도 풂.</summary>
    internal static string ExpandPath(string raw)
    {
        if (raw.Length == 0) return raw;
        if (raw[0] == '{')
        {
            int close = raw.IndexOf('}');
            if (close > 0 && Guid.TryParse(raw.AsSpan(0, close + 1), out var kf))
            {
                string? folder;
                lock (KnownFolders)
                {
                    if (!KnownFolders.TryGetValue(kf, out folder)) KnownFolders[kf] = folder = KnownFolderPath(kf);
                }
                if (folder is not null) raw = folder.TrimEnd('\\') + raw[(close + 1)..];
            }
        }
        else if (raw.Contains('%'))
        {
            raw = Environment.ExpandEnvironmentVariables(raw);
        }
        return raw;
    }

    private static string? KnownFolderPath(Guid id)
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            if (T.SHGetKnownFolderPath(ref id, T.KF_FLAG_DONT_VERIFY, IntPtr.Zero, out p) != 0) return null;
            return Marshal.PtrToStringUni(p);
        }
        finally
        {
            if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    // ───────────────────────── 변경 감시 ─────────────────────────

    private void StartWatch()
    {
        var t = new Thread(WatchLoop) { IsBackground = true, Name = "mongdock NotifyIconSettings" };
        t.Start();
    }

    private void WatchLoop()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (root is null) return; // 윈도우 10 등: 키 없음 → 감시 안 함
            using var evt = new AutoResetEvent(false);
            while (true)
            {
                int rc = T.RegNotifyChangeKeyValue(root.Handle, true,
                    T.REG_NOTIFY_CHANGE_NAME | T.REG_NOTIFY_CHANGE_LAST_SET, evt.SafeWaitHandle, true);
                if (rc != 0)
                {
                    // 알림 등록 실패 → 5초마다 다시 읽기로 대체
                    Log.Warn($"NotifyIconSettings 변경 감시 등록 실패 (오류 {rc}) → 5초 주기 확인");
                    while (true)
                    {
                        Thread.Sleep(5000);
                        Reload(raise: true);
                    }
                }
                evt.WaitOne();
                Thread.Sleep(400); // explorer 가 여러 값을 연달아 씀 → 모아서 한 번
                Reload(raise: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"NotifyIconSettings 변경 감시 중단: {ex.Message}");
        }
    }
}
