using System.Runtime.InteropServices;

namespace LiveTest;

/// <summary>
/// 화면 깜빡임 점검용 Desktop Duplication (DXGI) — DWM 이 합성한 화면을 프레임마다 받음 (바뀐 프레임만 옴).
/// 외부 NuGet 없이 COM vtable 을 직접 부름. 한 스레드에서 만들고 쓰고 버림.
/// 주 모니터(데스크톱 좌표 0,0 을 담은 출력)만. 좌표는 물리 픽셀.
/// </summary>
internal sealed unsafe class DesktopDup : IDisposable
{
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out int level, out IntPtr context);

    private const int WaitTimeout = unchecked((int)0x887A0027);
    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private readonly IntPtr _device, _context, _dup;
    private IntPtr _staging;
    public int OriginX { get; }
    public int OriginY { get; }

    private static void* Fn(IntPtr obj, int i) => (*(void***)obj)[i];
    private static void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(p, 2))(p); }
    private static void Check(int hr, string what) { if (hr < 0) throw new COMException($"DXGI {what} 0x{hr:X8}", hr); }

    private static IntPtr Query(IntPtr p, Guid iid)
    {
        IntPtr r;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Fn(p, 0))(p, &iid, &r), "QueryInterface");
        return r;
    }

    public DesktopDup()
    {
        Check(D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0x20 /* BGRA */, IntPtr.Zero, 0, 7, out _device, out _, out _context), "D3D11CreateDevice");
        var dxgi = Query(_device, IidDxgiDevice);
        IntPtr adapter;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Fn(dxgi, 7))(dxgi, &adapter), "GetAdapter");
        Release(dxgi);
        IntPtr output = IntPtr.Zero;
        for (uint i = 0; ; i++)
        {
            IntPtr o;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(adapter, 7))(adapter, i, &o) < 0) break;
            // DXGI_OUTPUT_DESC: WCHAR[32] DeviceName, RECT DesktopCoordinates, ...
            byte* desc = stackalloc byte[128];
            ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Fn(o, 7))(o, desc);
            int left = *(int*)(desc + 64), top = *(int*)(desc + 68), right = *(int*)(desc + 72), bottom = *(int*)(desc + 76);
            if (output == IntPtr.Zero && left <= 0 && top <= 0 && right > 0 && bottom > 0) { output = o; OriginX = left; OriginY = top; }
            else Release(o);
        }
        Release(adapter);
        if (output == IntPtr.Zero) throw new COMException("DXGI 주 모니터 출력을 못 찾음");
        var output1 = Query(output, IidOutput1);
        Release(output);
        IntPtr dup;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Fn(output1, 22))(output1, _device, &dup), "DuplicateOutput");
        Release(output1);
        _dup = dup;
    }

    /// <summary>
    /// 다음 화면 프레임에서 상자마다 평균 밝기(0~255). 그 사이 화면이 안 바뀌었으면 null.
    /// 상자 = 화면 물리 픽셀 (X, Y, W, H).
    /// </summary>
    public double[]? Next(int timeoutMs, (int X, int Y, int W, int H)[] boxes)
    {
        byte* info = stackalloc byte[64];
        IntPtr resource;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, IntPtr*, int>)Fn(_dup, 8))(_dup, (uint)timeoutMs, info, &resource);
        if (hr == WaitTimeout) return null;
        Check(hr, "AcquireNextFrame");
        try
        {
            if (*(long*)info == 0) return null; // 마우스만 움직임 (화면 그대로)
            var tex = Query(resource, IidTexture2D);
            try
            {
                if (_staging == IntPtr.Zero)
                {
                    // D3D11_TEXTURE2D_DESC (11 x uint): 원본 그대로 + STAGING·CPU 읽기
                    uint* d = stackalloc uint[11];
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint*, void>)Fn(tex, 10))(tex, d);
                    d[2] = 1; d[3] = 1; d[5] = 1; d[6] = 0; d[7] = 3; d[8] = 0; d[9] = 0x20000; d[10] = 0;
                    IntPtr st;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr, IntPtr*, int>)Fn(_device, 5))(_device, d, IntPtr.Zero, &st), "CreateTexture2D");
                    _staging = st;
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Fn(_context, 47))(_context, _staging, tex); // CopyResource
            }
            finally { Release(tex); }
            // D3D11_MAPPED_SUBRESOURCE { void* pData; uint RowPitch; uint DepthPitch; }
            byte* mapped = stackalloc byte[16];
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, byte*, int>)Fn(_context, 14))(_context, _staging, 0, 1 /* READ */, 0, mapped), "Map");
            try
            {
                byte* data = *(byte**)mapped;
                uint pitch = *(uint*)(mapped + sizeof(IntPtr));
                var result = new double[boxes.Length];
                for (int b = 0; b < boxes.Length; b++)
                {
                    var (x, y, w, h) = boxes[b];
                    double sum = 0;
                    for (int yy = 0; yy < h; yy++)
                    {
                        byte* row = data + (y - OriginY + yy) * pitch + (x - OriginX) * 4;
                        for (int xx = 0; xx < w; xx++, row += 4) sum += row[0] * 0.11 + row[1] * 0.59 + row[2] * 0.3;
                    }
                    result[b] = sum / (w * h);
                }
                return result;
            }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Fn(_context, 15))(_context, _staging, 0); }
        }
        finally
        {
            Release(resource);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(_dup, 14))(_dup); // ReleaseFrame
        }
    }

    public void Dispose()
    {
        Release(_staging);
        Release(_dup);
        Release(_context);
        Release(_device);
    }
}
