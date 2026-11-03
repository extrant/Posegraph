using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Posegraph.Backend;

/// <summary>
/// 显存 / 内存检测，判断能否加载识别模型。优先用 nvidia-smi，没有时走 Windows DXGI + WDDM（支持 AMD / Intel / NVIDIA），后台每几秒刷新一次。
/// </summary>
public sealed class SystemProbe
{
    /// <summary>GPU 模式所需可用显存：模型峰值约 2GB（MHR 放 CPU）+ 运行时上下文与余量。低于它时"自动"档改用 CPU。</summary>
    public const int RequiredVramMB = 3000;

    /// <summary>加载权重时的内存峰值约 4GB。</summary>
    public const int RequiredRamMB = 4096;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly Guid IidIdxgiFactory1 = new(0x770AAE78, 0xF26F, 0x4DBA, 0xA8, 0x29, 0x25, 0x3C, 0x83, 0xD1, 0xB3, 0x87);
    private static readonly Guid IidIdxgiAdapter3 = new(0x645967A4, 0x1392, 0x4310, 0xA7, 0x98, 0x80, 0x53, 0xCE, 0x3E, 0x93, 0xFD);

    private DateTime _last = DateTime.MinValue;
    private Task? _running;
    private bool _noNvidiaSmi;
    private static int? _d3dkmtSegOffset;

    public string? GpuName { get; private set; }
    public int? VramFreeMB { get; private set; }
    public int? VramTotalMB { get; private set; }
    public string? GpuError { get; private set; }
    public int RamAvailMB { get; private set; }
    public int RamTotalMB { get; private set; }

    /// <summary>界面每帧调用；到时间才在后台刷新。</summary>
    public void Tick()
    {
        if (_running is { IsCompleted: false } || DateTime.UtcNow - _last < Interval) return;
        _last = DateTime.UtcNow;
        _running = Task.Run(Refresh);
    }

    private void Refresh()
    {
        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            RamAvailMB = (int)(mem.AvailPhys / 1048576);
            RamTotalMB = (int)(mem.TotalPhys / 1048576);
        }
        try
        {
            if (!_noNvidiaSmi && TryQueryNvidiaSmi(out var nvName, out var nvTotal, out var nvFree))
            {
                GpuName = nvName;
                VramTotalMB = nvTotal;
                VramFreeMB = nvFree;
                GpuError = null;
                return;
            }
            if (OperatingSystem.IsWindows() && TryQueryWindowsDxgi(out var dxName, out var dxTotal, out var dxFree))
            {
                GpuName = dxName;
                VramTotalMB = dxTotal;
                VramFreeMB = dxFree;
                GpuError = null;
                return;
            }
            throw new InvalidOperationException(Loc.T("未检测到可用独立显卡"));
        }
        catch (Exception ex)
        {
            GpuError = Loc.F("无法读取显存：{0}", ex.Message);
            VramFreeMB = null;
        }
    }

    private bool TryQueryNvidiaSmi(out string name, out int totalMB, out int freeMB)
    {
        name = "";
        totalMB = freeMB = 0;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            })!;
            var line = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            var parts = line?.Split(',').Select(s => s.Trim()).ToArray();
            if (parts is not { Length: 3 }) return false;
            name = parts[0];
            totalMB = int.Parse(parts[1]);
            freeMB = int.Parse(parts[2]);
            return true;
        }
        catch (Win32Exception)
        {
            _noNvidiaSmi = true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static unsafe bool TryQueryWindowsDxgi(out string name, out int totalMB, out int freeMB)
    {
        name = "";
        totalMB = freeMB = 0;
        if (nint.Size != 8) return false;
        var iidFactory = IidIdxgiFactory1;
        if (CreateDXGIFactory1(&iidFactory, out var factory) != 0 || factory == 0) return false;
        try
        {
            var factoryVtbl = *(nint**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)factoryVtbl[12];
            string? bestName = null;
            nuint bestDedicated = 0;
            Luid bestLuid = default;
            (ulong Budget, ulong Usage)? bestBudget = null;

            for (uint i = 0; ; i++)
            {
                nint adapter = 0;
                if (enumAdapters1(factory, i, &adapter) != 0 || adapter == 0) break;
                try
                {
                    var adapterVtbl = *(nint**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, DxgiAdapterDesc1*, int>)adapterVtbl[10];
                    DxgiAdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) != 0) continue;
                    if ((desc.Flags & 2) != 0 || desc.DedicatedVideoMemory == 0) continue;
                    if (desc.DedicatedVideoMemory <= bestDedicated) continue;

                    bestDedicated = desc.DedicatedVideoMemory;
                    bestLuid = desc.AdapterLuid;
                    bestName = new string(desc.Description).TrimEnd('\0').Trim();
                    bestBudget = null;

                    var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)adapterVtbl[0];
                    var iidAdapter3 = IidIdxgiAdapter3;
                    nint adapter3 = 0;
                    if (queryInterface(adapter, &iidAdapter3, &adapter3) == 0 && adapter3 != 0)
                    {
                        try
                        {
                            var adapter3Vtbl = *(nint**)adapter3;
                            var queryMem = (delegate* unmanaged[Stdcall]<nint, uint, int, DxgiQueryVideoMemoryInfo*, int>)adapter3Vtbl[14];
                            DxgiQueryVideoMemoryInfo info;
                            if (queryMem(adapter3, 0, 0, &info) == 0)
                                bestBudget = (info.Budget, info.CurrentUsage);
                        }
                        finally
                        {
                            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)adapter3)[2])(adapter3);
                        }
                    }
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)adapter)[2])(adapter);
                }
            }

            if (bestName is null || bestDedicated == 0) return false;
            name = bestName;
            totalMB = (int)((ulong)bestDedicated / 1048576UL);

            if (TryQueryD3dkmtResidentBytes(bestLuid, (ulong)bestDedicated, out var residentBytes))
            {
                var usedMB = (int)Math.Min((ulong)totalMB, residentBytes / 1048576UL);
                freeMB = Math.Max(0, totalMB - usedMB);
                return true;
            }
            if (bestBudget is var (budget, usage))
            {
                var freeBytes = budget > usage ? budget - usage : 0UL;
                freeMB = (int)Math.Min((ulong)totalMB, freeBytes / 1048576UL);
                return true;
            }
            return false;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)factory)[2])(factory);
        }
    }

    private static unsafe bool TryQueryD3dkmtResidentBytes(Luid luid, ulong expectedDedicated, out ulong residentBytes)
    {
        residentBytes = 0;
        const int BufSize = 2048;
        Span<byte> buf = stackalloc byte[BufSize];
        Span<byte> seg0 = stackalloc byte[BufSize];

        WriteHeader(buf, 0, luid); // D3DKMT_QUERYSTATISTICS_ADAPTER = 0
        fixed (byte* pBuf = buf)
            if (D3DKMTQueryStatistics(pBuf) != 0) return false;
        var nbSegments = BinaryPrimitives.ReadUInt32LittleEndian(buf[24..]);
        if (nbSegments is 0 or > 32) return false;

        WriteHeader(seg0, 3, luid); // D3DKMT_QUERYSTATISTICS_SEGMENT = 3, SegmentId = 0
        fixed (byte* pSeg0 = seg0)
            if (D3DKMTQueryStatistics(pSeg0) != 0) return false;

        if (nbSegments > 1 && _d3dkmtSegOffset is null)
        {
            Span<byte> probe = stackalloc byte[BufSize];
            for (var off = 512; off < 1536; off += 4)
            {
                probe.Clear();
                WriteHeader(probe, 3, luid);
                BinaryPrimitives.WriteUInt32LittleEndian(probe[off..], 1);
                int rc;
                fixed (byte* pProbe = probe) rc = D3DKMTQueryStatistics(pProbe);
                if (rc == 0 && !probe.Slice(24, 24).SequenceEqual(seg0.Slice(24, 24)))
                {
                    _d3dkmtSegOffset = off;
                    break;
                }
            }
        }

        ulong totalLimit = 0, totalResident = 0;
        AccumulateSegment(seg0, ref totalLimit, ref totalResident);
        if (nbSegments > 1 && _d3dkmtSegOffset is { } segOff)
        {
            Span<byte> seg = stackalloc byte[BufSize];
            for (uint s = 1; s < nbSegments; s++)
            {
                seg.Clear();
                WriteHeader(seg, 3, luid);
                BinaryPrimitives.WriteUInt32LittleEndian(seg[segOff..], s);
                int rc;
                fixed (byte* pSeg = seg) rc = D3DKMTQueryStatistics(pSeg);
                if (rc == 0) AccumulateSegment(seg, ref totalLimit, ref totalResident);
            }
        }

        if (totalLimit == 0 || totalLimit < expectedDedicated / 2 || totalLimit > expectedDedicated * 3 / 2)
            return false;
        residentBytes = totalResident;
        return true;

        static void WriteHeader(Span<byte> b, uint type, Luid id)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, type);
            BinaryPrimitives.WriteUInt32LittleEndian(b[4..], id.LowPart);
            BinaryPrimitives.WriteInt32LittleEndian(b[8..], id.HighPart);
        }

        static void AccumulateSegment(ReadOnlySpan<byte> b, ref ulong limitSum, ref ulong residentSum)
        {
            var limit = BinaryPrimitives.ReadUInt64LittleEndian(b[24..]);
            var resident = BinaryPrimitives.ReadUInt64LittleEndian(b[40..]);
            var props = BinaryPrimitives.ReadUInt32LittleEndian(b[128..]); // bit 0 = Aperture
            if ((props & 1) == 0 && limit > 0)
            {
                limitSum += limit;
                residentSum += resident;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiQueryVideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern unsafe int CreateDXGIFactory1(Guid* riid, out nint ppFactory);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern unsafe int D3DKMTQueryStatistics(void* pData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
