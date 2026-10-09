using System.Runtime.InteropServices;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 기기에 따라 설정 줄을 숨길 때 쓰는 판단 (#21): 배터리 있음, 모니터 2대 이상, 한/영 버튼이 쓸모 있음.
/// 매번 새로 조회 (가벼운 Win32 호출 — 설정 창을 그릴 때만).
/// </summary>
public static class DeviceInfo
{
    /// <summary>시스템 배터리가 있는지 (데스크톱은 false).</summary>
    public static bool HasBattery
    {
        get
        {
            try
            {
                if (!PowerApi.GetSystemPowerStatus(out var st)) return true; // 모르면 보여 줌
                return st.BatteryFlag != PowerApi.BATTERY_FLAG_NO_BATTERY;
            }
            catch { return true; }
        }
    }

    /// <summary>연결된 모니터가 2대 이상인지.</summary>
    public static bool HasMultipleMonitors
    {
        get
        {
            try { return Monitors.GetAll().Count > 1; }
            catch { return true; }
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    /// <summary>
    /// 한/영 버튼이 쓸모 있는지: 입력 언어가 2개 이상이거나, 한국어·일본어·중국어 입력기(같은 언어 안에서 한/영 전환)가 있을 때.
    /// 영어만 쓰는 PC 에선 숨김.
    /// </summary>
    public static bool ImeToggleUseful
    {
        get
        {
            try
            {
                int n = GetKeyboardLayoutList(0, null);
                if (n <= 0) return true;
                var list = new IntPtr[n];
                n = GetKeyboardLayoutList(n, list);
                if (n >= 2) return true;
                for (int i = 0; i < n; i++)
                {
                    int lang = (int)(list[i].ToInt64() & 0x3FF); // 주 언어 ID
                    if (lang is 0x12 or 0x11 or 0x04) return true; // 한국어·일본어·중국어
                }
                return false;
            }
            catch { return true; }
        }
    }
}
