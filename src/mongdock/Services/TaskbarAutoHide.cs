using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// "윈도우 작업 표시줄 숨기기" 용 작업 표시줄 자동 숨김(ABS_AUTOHIDE) 관리.
/// 작업 표시줄 창만 숨기면 자동 숨김을 안 쓰는 PC 에선 작업 표시줄의 작업 영역 예약이 남아 아래에 빈 띠가 생긴다
/// → 숨기는 동안만 자동 숨김을 켜고, 끝나면 원래대로(원래 꺼져 있었고 지금도 켜져 있을 때만) 끈다.
/// 원래 상태는 %APPDATA%\mongdock\cache\taskbar-state.json 에 원자적으로 기록 (강제 종료 뒤 다음 실행·제거 시 복구용).
/// 자동 숨김은 전역 설정이라 보조 모니터 작업 표시줄도 같이 따른다.
/// SHAppBarMessage(ABM_GETSTATE/SETSTATE) 는 결과용 공유 메모리가 없는 메시지라 트레이 가로채기 창(TrayIconService)을 거쳐도
/// 그대로 explorer 로 전달되고 반환값도 돌아온다(실측).
/// 어느 스레드에서나 호출 가능 (ProcessExit 포함).
/// </summary>
internal static class TaskbarAutoHide
{
    private static readonly object Gate = new();
    /// <summary>이번 실행에서 관리 중인 원래 상태 (null = 관리 안 함).</summary>
    private static bool? _originalAutoHide;

    public static string StatePath { get; } = Path.Combine(AppInfo.DataDirectory, "cache", "taskbar-state.json");

    /// <summary>복구 기록이 남아 있는지 (이전 실행이 숨긴 채 강제 종료됨, 또는 지금 관리 중).</summary>
    public static bool HasRecord
    {
        get
        {
            try { return File.Exists(StatePath); }
            catch { return false; }
        }
    }

    private sealed class Record
    {
        public bool OriginalAutoHide { get; set; }
        public DateTime SavedAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// 작업 표시줄을 숨기기 직전에: 원래 상태를 기록하고(이미 기록이 있으면 그 기록을 이어 씀) 자동 숨김이 꺼져 있으면 켠다.
    /// 자동 숨김을 켰으면 true — explorer 가 작업 표시줄 창을 다시 보이게 하므로 호출자는 조금 뒤 다시 숨겨야 한다.
    /// </summary>
    public static bool Ensure()
    {
        lock (Gate)
        {
            if (GetState() is not uint state)
            {
                Log.Warn("작업 표시줄 자동 숨김: 탐색기 작업 표시줄을 찾지 못해 건너뜀");
                return false;
            }
            if (_originalAutoHide is null)
            {
                bool? fromFile = ReadRecord();
                if (fromFile is bool f)
                {
                    _originalAutoHide = f;
                    Log.Info($"작업 표시줄 자동 숨김: 이전 실행 기록 이어 씀 (원래 {(f ? "켜짐" : "꺼짐")})");
                }
                else
                {
                    _originalAutoHide = (state & Shell32.ABS_AUTOHIDE) != 0;
                    // 상태를 바꾸기 전에 기록 (바꾼 직후 강제 종료돼도 되돌릴 수 있게)
                    WriteRecord(_originalAutoHide.Value);
                }
            }
            if ((state & Shell32.ABS_AUTOHIDE) != 0) return false;
            SetState(state | Shell32.ABS_AUTOHIDE);
            Log.Info("작업 표시줄 자동 숨김 켬 (빈 띠 방지, 몽독이 숨기는 동안만)");
            return true;
        }
    }

    /// <summary>
    /// 작업 표시줄을 다시 보인 뒤에: 기록이 "원래 꺼짐" 이고 지금 자동 숨김이 켜져 있으면 끈다. 기록 삭제.
    /// 관리 중이 아니어도 복구 파일이 남아 있으면(이전 실행 강제 종료) 그 기록대로 되돌린다.
    /// </summary>
    public static void Restore(string reason)
    {
        lock (Gate)
        {
            bool? original = _originalAutoHide ?? ReadRecord();
            if (original is null && !HasRecord) return;
            if (original == false)
            {
                if (GetState() is not uint state)
                {
                    // 탐색기가 없으면 되돌릴 수 없음 → 기록을 남겨 다음 실행·제거 때 다시 시도
                    Log.Warn($"작업 표시줄 자동 숨김 되돌리기 보류 — 탐색기 작업 표시줄 없음 ({reason})");
                    _originalAutoHide = null;
                    return;
                }
                if ((state & Shell32.ABS_AUTOHIDE) != 0)
                {
                    SetState(state & ~Shell32.ABS_AUTOHIDE);
                    Log.Info($"작업 표시줄 자동 숨김 원래대로 끔 ({reason})");
                }
                else
                {
                    Log.Info($"작업 표시줄 자동 숨김이 이미 꺼져 있음 — 그대로 둠 ({reason})");
                }
            }
            _originalAutoHide = null;
            DeleteRecord();
        }
    }

    // ───────────────────────── SHAppBarMessage ─────────────────────────

    /// <summary>윈도우 작업 표시줄 자동 숨김이 켜져 있는지 (작업 표시줄이 없으면 null).</summary>
    public static bool? WindowsAutoHideOn => GetState() is uint s ? (s & 0x1 /* ABS_AUTOHIDE */) != 0 : null;

    /// <summary>ABM_GETSTATE. 탐색기 작업 표시줄이 없으면 null (그때 0 은 "꺼짐" 과 구분이 안 됨).</summary>
    private static uint? GetState()
    {
        if (TrayApi.FindExplorerTray() == IntPtr.Zero) return null;
        var abd = NewData();
        return (uint)Shell32.SHAppBarMessage(Shell32.ABM_GETSTATE, ref abd);
    }

    private static void SetState(uint state)
    {
        var abd = NewData();
        abd.lParam = (IntPtr)(int)state;
        Shell32.SHAppBarMessage(Shell32.ABM_SETSTATE, ref abd);
    }

    private static APPBARDATA NewData() => new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };

    // ───────────────────────── 복구 파일 ─────────────────────────

    private static bool? ReadRecord()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var r = JsonSerializer.Deserialize<Record>(File.ReadAllText(StatePath), JsonOptions);
            return r?.OriginalAutoHide;
        }
        catch (Exception ex)
        {
            Log.Warn("작업 표시줄 상태 기록 읽기 실패 (무시)", ex);
            return null;
        }
    }

    private static void WriteRecord(bool originalAutoHide)
    {
        try
        {
            var r = new Record { OriginalAutoHide = originalAutoHide, SavedAt = DateTime.Now };
            AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(r, JsonOptions));
            Log.Info($"작업 표시줄 원래 상태 기록: 자동 숨김 {(originalAutoHide ? "켜짐" : "꺼짐")}");
        }
        catch (Exception ex)
        {
            // 기록을 못 해도 이번 실행 안에서는 메모리 값으로 되돌린다 (강제 종료 시에만 못 되돌림)
            Log.Error("작업 표시줄 상태 기록 저장 실패", ex);
        }
    }

    private static void DeleteRecord()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); }
        catch (Exception ex) { Log.Warn("작업 표시줄 상태 기록 삭제 실패", ex); }
    }
}
