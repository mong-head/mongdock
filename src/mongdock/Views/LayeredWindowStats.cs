using System.Runtime.InteropServices;
using System.Windows;

namespace Mongdock.Views;

/// <summary>
/// 메모리 진단 (#14): 투명 창(AllowsTransparency) 크기가 바뀐 횟수·서로 다른 크기 수를 창 종류별로 셈.
/// 큰 투명 창은 크기가 바뀔 때마다 그래픽 드라이버가 그 크기 면을 새로 잡아(닫거나 줄여도 한동안 남음) 메모리가 30~60MB씩 오르내림 —
/// 어느 창이 원인인지 사용자 PC 로그로 보려고. 창 종류 = 클래스 이름(같은 클래스는 제목으로 나눔).
/// </summary>
public static class LayeredWindowStats
{
    private sealed class Entry
    {
        public int Changes;
        public readonly HashSet<long> Sizes = new();
        public int MaxW, MaxH;
    }

    private static readonly Dictionary<string, Entry> Entries = new();
    private static bool _installed;

    /// <summary>모든 Window 의 크기 바뀜을 받음 (UI 스레드에서 한 번).</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnSizeChanged));
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Window { AllowsTransparency: true } w || !ReferenceEquals(e.OriginalSource, w)) return;
        double scale = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        int pw = (int)Math.Round(e.NewSize.Width * scale), ph = (int)Math.Round(e.NewSize.Height * scale);
        if (pw < 64 || ph < 64) return; // 작은 창(툴팁·끄는 아이콘 등)은 메모리 영향이 작아 뺌
        string key = w.GetType().Name == nameof(Window) ? w.Title : w.GetType().Name;
        lock (Entries)
        {
            if (!Entries.TryGetValue(key, out var en)) Entries[key] = en = new Entry();
            en.Changes++;
            en.Sizes.Add(((long)pw << 32) | (uint)ph);
            en.MaxW = Math.Max(en.MaxW, pw);
            en.MaxH = Math.Max(en.MaxH, ph);
        }
    }

    /// <summary>
    /// 메모리 줄에 붙일 요약 (스레드 풀에서 불려도 됨): 크기가 많이 바뀐 창 4개 "이름 n번/크기 k개/최대 w×h".
    /// </summary>
    public static string Summary()
    {
        List<(string Key, Entry En)> top;
        lock (Entries) top = Entries.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value.Sizes.Count).ThenByDescending(x => x.Value.Changes).Take(4).ToList();
        if (top.Count == 0) return "투명 창 크기 바뀜 없음";
        return "투명 창 크기: " + string.Join(", ", top.Select(x => $"{Short(x.Key)} {x.En.Changes}번/{x.En.Sizes.Count}가지/최대 {x.En.MaxW}×{x.En.MaxH}"));
    }

    private static string Short(string key) => key.StartsWith("mongdock ", StringComparison.Ordinal) ? key[9..] : key;

    /// <summary>
    /// 그래픽 드라이버 몫 MB: 이 프로세스의 쓰기 결합(PAGE_WRITECOMBINE) 전용 메모리 합 — WPF 하드웨어 그리기용 드라이버 면 (#10 조사).
    /// 드라이버마다 다를 수 있음(이 PC NVIDIA 에서 확인).
    /// </summary>
    public static long DriverMemoryBytes()
    {
        long total = 0;
        IntPtr a = IntPtr.Zero;
        int size = Marshal.SizeOf<MemoryInfo>();
        while (VirtualQuery(a, out var m, size) != 0)
        {
            long len = (long)m.RegionSize;
            if (m.State == MemCommit && m.Type == MemPrivate && (m.Protect & PageWriteCombine) != 0) total += len;
            long next = (long)m.BaseAddress + len;
            if (next <= (long)a) break;
            a = (IntPtr)next;
        }
        return total;
    }

    private const uint MemCommit = 0x1000, MemPrivate = 0x20000, PageWriteCombine = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")]
    private static extern int VirtualQuery(IntPtr address, out MemoryInfo info, int length);
}
