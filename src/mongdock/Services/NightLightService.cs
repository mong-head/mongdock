using System.Diagnostics;
using Microsoft.Win32;

namespace Mongdock.Services;

/// <summary>
/// 야간 모드(야간 조명) 켜짐 여부 읽기 (읽기만 — 레지스트리 쓰기는 윈도우가 화면에 반영하지 않아 설정 앱을 연다). 윈도우에 공식 API 가 없어 설정 앱이 쓰는 CloudStore 레지스트리 값을 직접 다룬다:
///   HKCU\…\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\
///        windows.data.bluelightreduction.bluelightreductionstate  값 "Data" (REG_BINARY)
/// 형식은 Microsoft Bond Compact Binary v1 ("CB" 43 42 01 00) 직렬화:
///   바깥: 43 42 01 00 | 0A 02 01 00 | 2A 06 &lt;유닉스 초 varint&gt; 2A 2B 0E &lt;길이&gt; &lt;안쪽 blob&gt; | 00 00 00
///   안쪽: 43 42 01 00 | [10 00 = 필드0(int32) 있음 → 켜짐] | D0 0A 02 (필드10) | C6 14 &lt;FILETIME varint&gt; (필드20) | 00
/// 한 번도 야간 모드를 쓴 적 없으면 바깥에 안쪽 blob 이 없다(= 꺼짐). 이 형식이 아니면 Supported=false (바꾸지 않음).
/// 어느 스레드에서나 호출 가능 (레지스트리 호출뿐).
/// </summary>
public static class NightLightService
{
    private const string StateKey =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    private static bool _loggedUnsupported;

    /// <summary>야간 모드 켜짐 여부. 키가 없거나 형식을 모르면 null (UI 는 토글 대신 설정 링크만).</summary>
    public static bool? IsOn
    {
        get
        {
            try
            {
                var data = ReadData();
                if (data is null) return Unsupported("상태 키 없음");
                var on = ParseState(data);
                return on ?? Unsupported("상태 형식 모름: " + Convert.ToHexString(data));
            }
            catch (Exception ex)
            {
                return Unsupported(ex.Message);
            }
        }
    }

    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:nightlight") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error("야간 모드 설정 열기 실패", ex); }
    }

    private static byte[]? ReadData()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StateKey, writable: false);
        return key?.GetValue("Data") as byte[];
    }

    private static bool? Unsupported(string why)
    {
        if (!_loggedUnsupported)
        {
            _loggedUnsupported = true;
            Log.Info($"야간 모드 읽기 불가: {why}");
        }
        return null;
    }

    // ───────────────────────── 형식 ─────────────────────────

    /// <summary>바깥 blob 을 풀어 (유닉스 초, 안쪽 blob) — 안쪽이 없으면 null. 형식이 다르면 false.</summary>
    internal static bool TryParseOuter(byte[] data, out ulong timestamp, out byte[]? inner)
    {
        timestamp = 0;
        inner = null;
        var r = new Reader(data);
        if (!r.Magic()) return false;
        // 필드0: struct { bool 필드0 } — 내용은 보지 않고 건너뜀
        if (!r.FieldHeader(out int id, out int type) || id != 0 || type != BtStruct || !r.SkipStruct()) return false;
        // 필드1: struct { uint64 필드0 = 시각, struct 필드1 { list<int8> 필드1 = 안쪽 } }
        if (!r.FieldHeader(out id, out type) || id != 1 || type != BtStruct) return false;
        while (true)
        {
            if (!r.FieldHeader(out id, out type)) return false;
            if (type == BtStop) break;
            if (id == 0 && type == BtUInt64)
            {
                if (!r.VarUInt(out timestamp)) return false;
            }
            else if (id == 1 && type == BtStruct)
            {
                while (true)
                {
                    if (!r.FieldHeader(out int id2, out int type2)) return false;
                    if (type2 == BtStop) break;
                    if (id2 == 1 && type2 == BtList)
                    {
                        if (!r.Byte(out byte elem) || (elem & 0x1F) != BtInt8 || !r.VarUInt(out ulong n) || n > int.MaxValue) return false;
                        if (!r.Bytes((int)n, out var blob)) return false;
                        inner = blob;
                    }
                    else if (!r.SkipValue(type2)) return false;
                }
            }
            else if (!r.SkipValue(type)) return false;
        }
        return true;
    }

    /// <summary>켜짐 여부 (안쪽 blob 의 필드0 존재). 형식을 모르면 null.</summary>
    internal static bool? ParseState(byte[] data)
    {
        if (!TryParseOuter(data, out _, out var inner)) return null;
        if (inner is null) return false; // 한 번도 쓰지 않음
        if (!TryParseInner(inner, out var fields)) return null;
        return fields.Any(f => f.Id == 0);
    }

    /// <summary>안쪽 struct 의 필드 목록 (id, 헤더 포함 원본 바이트).</summary>
    internal static bool TryParseInner(byte[] inner, out List<(int Id, byte[] Raw)> fields)
    {
        fields = new();
        var r = new Reader(inner);
        if (!r.Magic()) return false;
        while (true)
        {
            int start = r.Pos;
            if (!r.FieldHeader(out int id, out int type)) return false;
            if (type == BtStop) break;
            if (!r.SkipValue(type)) return false;
            fields.Add((id, inner[start..r.Pos]));
        }
        return r.Pos == inner.Length;
    }

    // Bond 타입 코드
    private const int BtStop = 0, BtStopBase = 1, BtBool = 2, BtUInt8 = 3, BtUInt16 = 4, BtUInt32 = 5, BtUInt64 = 6,
        BtFloat = 7, BtDouble = 8, BtString = 9, BtStruct = 10, BtList = 11, BtSet = 12, BtMap = 13,
        BtInt8 = 14, BtInt16 = 15, BtInt32 = 16, BtInt64 = 17, BtWString = 18;

    /// <summary>Bond Compact Binary v1 최소 읽기 (건너뛰기 위주, 범위 넘으면 false).</summary>
    private struct Reader
    {
        private readonly byte[] _d;
        public int Pos;
        private int _depth;

        public Reader(byte[] d)
        {
            _d = d;
            Pos = 0;
            _depth = 0;
        }

        public bool Byte(out byte b)
        {
            b = 0;
            if (Pos >= _d.Length) return false;
            b = _d[Pos++];
            return true;
        }

        public bool Bytes(int n, out byte[] b)
        {
            b = Array.Empty<byte>();
            if (n < 0 || Pos + n > _d.Length) return false;
            b = _d[Pos..(Pos + n)];
            Pos += n;
            return true;
        }

        public bool Magic()
        {
            if (_d.Length < 4 || _d[0] != 0x43 || _d[1] != 0x42 || _d[2] != 0x01 || _d[3] != 0x00) return false;
            Pos = 4;
            return true;
        }

        public bool VarUInt(out ulong v)
        {
            v = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (!Byte(out byte b)) return false;
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        /// <summary>필드 헤더: 하위 5비트 = 타입, 상위 3비트 = id (6 이면 다음 1바이트, 7 이면 다음 2바이트가 id).</summary>
        public bool FieldHeader(out int id, out int type)
        {
            id = type = 0;
            if (!Byte(out byte b)) return false;
            type = b & 0x1F;
            id = b >> 5;
            if (id == 6)
            {
                if (!Byte(out byte x)) return false;
                id = x;
            }
            else if (id == 7)
            {
                if (!Byte(out byte lo) || !Byte(out byte hi)) return false;
                id = lo | (hi << 8);
            }
            return true;
        }

        public bool SkipStruct()
        {
            if (++_depth > 16) return false;
            while (true)
            {
                if (!FieldHeader(out _, out int type)) return false;
                if (type == BtStop) break;
                if (type == BtStopBase) continue;
                if (!SkipValue(type)) return false;
            }
            _depth--;
            return true;
        }

        public bool SkipValue(int type)
        {
            switch (type)
            {
                case BtBool or BtUInt8 or BtInt8: return Byte(out _);
                case BtUInt16 or BtUInt32 or BtUInt64 or BtInt16 or BtInt32 or BtInt64: return VarUInt(out _);
                case BtFloat: return Bytes(4, out _);
                case BtDouble: return Bytes(8, out _);
                case BtString:
                    return VarUInt(out ulong sl) && sl <= int.MaxValue && Bytes((int)sl, out _);
                case BtWString:
                    return VarUInt(out ulong wl) && wl <= int.MaxValue / 2 && Bytes((int)wl * 2, out _);
                case BtStruct: return SkipStruct();
                case BtList or BtSet:
                {
                    if (!Byte(out byte et) || !VarUInt(out ulong n) || n > (ulong)_d.Length) return false;
                    for (ulong i = 0; i < n; i++)
                        if (!SkipValue(et & 0x1F)) return false;
                    return true;
                }
                case BtMap:
                {
                    if (!Byte(out byte kt) || !Byte(out byte vt) || !VarUInt(out ulong n) || n > (ulong)_d.Length) return false;
                    for (ulong i = 0; i < n; i++)
                        if (!SkipValue(kt & 0x1F) || !SkipValue(vt & 0x1F)) return false;
                    return true;
                }
                default: return false;
            }
        }
    }
}
