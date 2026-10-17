using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mongdock.Services;

/// <summary>
/// 윈도우 「창을 최소화·최대화할 때 애니메이션」(SPI_SETANIMATION, 시스템 전체) 관리 (#20, 0.8).
/// 몽독 창 최소화 효과를 켜면 윈도우 효과가 동시에 보여 겹치므로 그동안만 끔. 원래 값은
/// %APPDATA%\mongdock\cache\window-animation.json 에 바꾸기 전에 기록 → 효과를 끄거나 몽독을 끌 때, 갑자기 꺼진 뒤 다음 실행,
/// 제거(--restore-taskbar)에서 되돌림. 작업 표시줄 자동 숨김(TaskbarAutoHide)과 같은 방식. 어느 스레드에서나.
/// </summary>
internal static class WindowAnimationSetting
{
    private static readonly object Gate = new();
    public static string StatePath { get; } = Path.Combine(AppInfo.DataDirectory, "cache", "window-animation.json");

    /// <summary>시험용: 실제 시스템 설정 대신 (읽기, 쓰기).</summary>
    internal static (Func<bool?> Get, Action<bool> Set)? SystemForTest { get; set; }

    private sealed class Record
    {
        public bool OriginalMinAnimate { get; set; }
        public DateTime SavedAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static bool HasRecord
    {
        get { try { return File.Exists(StatePath); } catch { return false; } }
    }

    /// <summary>몽독이 윈도우 효과를 꺼 둔 상태인지 (원래 켜져 있었는데 지금 꺼짐 — 설정 화면 결과 줄).</summary>
    public static bool TurnedOffByMongdock
    {
        get { lock (Gate) return HasRecord && ReadRecord() == true && Get() == false; }
    }

    /// <summary>지금 윈도우 최소화·최대화 애니메이션이 켜져 있는지 (모르면 null).</summary>
    public static bool? Get()
    {
        if (SystemForTest is { } t) return t.Get();
        var info = new AnimationInfo { cbSize = (uint)Marshal.SizeOf<AnimationInfo>() };
        return SystemParametersInfo(SPI_GETANIMATION, info.cbSize, ref info, 0) ? info.iMinAnimate != 0 : null;
    }

    private static void Set(bool on)
    {
        if (SystemForTest is { } t) { t.Set(on); return; }
        var info = new AnimationInfo { cbSize = (uint)Marshal.SizeOf<AnimationInfo>(), iMinAnimate = on ? 1 : 0 };
        if (!SystemParametersInfo(SPI_SETANIMATION, info.cbSize, ref info, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            Log.Warn($"윈도우 최소화 애니메이션 바꾸기 실패 (켜기={on}, 오류 {Marshal.GetLastWin32Error()})");
    }

    /// <summary>몽독 효과를 쓰는 동안: 원래 값을 기록(이미 있으면 이어 씀)하고 윈도우 효과가 켜져 있으면 끔. 껐으면 true.</summary>
    public static bool TurnOff()
    {
        lock (Gate)
        {
            if (Get() is not bool on) return false;
            if (!HasRecord) WriteRecord(on);
            if (!on) return false;
            Set(false);
            Log.Info("윈도우 최소화·최대화 애니메이션 끔 (몽독 최소화 효과를 쓰는 동안)");
            return true;
        }
    }

    /// <summary>기록이 있으면 원래 값으로 되돌리고 기록을 지움 (원래 켜져 있었고 지금 꺼져 있을 때만 켬).</summary>
    public static void Restore(string reason)
    {
        lock (Gate)
        {
            if (!HasRecord) return;
            bool? original = ReadRecord();
            if (original == true && Get() == false)
            {
                Set(true);
                Log.Info($"윈도우 최소화·최대화 애니메이션 되돌림 ({reason})");
            }
            try { File.Delete(StatePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WriteRecord(bool original)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            string tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Record { OriginalMinAnimate = original, SavedAt = DateTime.Now }, JsonOptions));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"윈도우 애니메이션 기록 실패: {ex.Message}"); }
    }

    private static bool? ReadRecord()
    {
        try { return JsonSerializer.Deserialize<Record>(File.ReadAllText(StatePath), JsonOptions)?.OriginalMinAnimate; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private const uint SPI_GETANIMATION = 0x0048, SPI_SETANIMATION = 0x0049, SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo { public uint cbSize; public int iMinAnimate; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref AnimationInfo info, uint winIni);
}
