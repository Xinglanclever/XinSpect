using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 原始裝置通道接縫：開裝置＋送 METHOD_BUFFERED IOCTL。真實走 CreateFileW／DeviceIoControl
/// （\\.\XsRegProbe）；測試注入假通道，不在單測碰真實驅動。
/// </summary>
public interface IRawDeviceChannel : IDisposable
{
    /// <summary>開啟裝置；失敗回 false 並帶 Win32 錯誤碼。</summary>
    bool TryOpen(string devicePath, out int win32Error);

    /// <summary>送 IOCTL；回 false 時 win32Error 帶出錯誤碼。</summary>
    bool TryIoctl(uint code, byte[]? input, byte[] output, out int bytesReturned, out int win32Error);
}

/// <summary>真實通道：管理員開啟 \\.\XsRegProbe（SDDL 已限管理員），逐次 DeviceIoControl。</summary>
public sealed class RawDeviceChannel : IRawDeviceChannel
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    private SafeFileHandle? _handle;

    public bool TryOpen(string devicePath, out int win32Error)
    {
        var handle = CreateFileW(devicePath, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }
        _handle = handle;
        win32Error = 0;
        return true;
    }

    public bool TryIoctl(uint code, byte[]? input, byte[] output, out int bytesReturned, out int win32Error)
    {
        if (_handle is null) { bytesReturned = 0; win32Error = 6; return false; } // 6 = ERROR_INVALID_HANDLE
        if (!DeviceIoControl(_handle, code, input, input?.Length ?? 0, output, output.Length, out int bytes, IntPtr.Zero))
        {
            bytesReturned = 0;
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }
        bytesReturned = bytes;
        win32Error = 0;
        return true;
    }

    public void Dispose() => _handle?.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize,
        byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);
}

/// <summary>
/// XsRegProbe 驅動後端的 IMmioReader：先 QUERY_INFO 能力協商（magic／版本／能力位元／允許清單筆數對帳），
/// 再以 IOCTL_XRP_READ_MMIO 讀區塊。managed 端先照契約鏡像自查範圍，不在清單就不打擾驅動、原因直接說明。
/// 驅動未載入＝三態（不謊稱讀過）；這層只讀不寫，位元組如實帶回。
/// </summary>
public sealed class DriverMmioReader : IMmioReader, IDisposable
{
    private readonly Func<IRawDeviceChannel> _channelFactory;
    private IRawDeviceChannel? _channel;
    private bool _handshaken;
    private string? _unavailableReason;
    private string? _lastFailReason;

    public DriverMmioReader(Func<IRawDeviceChannel>? channelFactory = null) =>
        _channelFactory = channelFactory ?? (() => new RawDeviceChannel());

    public string? BackendName => "XsRegProbe 白名單 IOCTL";

    public string? LastFailReason => _lastFailReason;

    private bool EnsureHandshake()
    {
        if (_handshaken) return _unavailableReason is null;
        _handshaken = true;

        var channel = _channelFactory();
        if (!channel.TryOpen(XsRegProbeContract.DevicePath, out int openError))
        {
            channel.Dispose();
            _unavailableReason = openError == 2
                ? "驅動裝置不存在（XsRegProbe 未載入）"
                : openError == 5
                    ? "無權開啟驅動裝置（需管理員；或未先在韌體安全頁啟用深層核心存取）"
                    : $"開啟驅動裝置失敗（Win32 錯誤 {openError}）";
            return false;
        }
        _channel = channel;

        var info = new byte[24]; // XRP_DRIVER_INFO：六個 ULONG
        if (!channel.TryIoctl(XsRegProbeContract.IoctlQueryInfo, null, info, out _, out int ioctlError))
        {
            _unavailableReason = $"QUERY_INFO 失敗（Win32 錯誤 {ioctlError}）";
            return false;
        }
        uint magic = BitConverter.ToUInt32(info, 0);
        uint version = BitConverter.ToUInt32(info, 4);
        uint features = BitConverter.ToUInt32(info, 8);
        uint msrExact = BitConverter.ToUInt32(info, 12);
        uint msrRange = BitConverter.ToUInt32(info, 16);
        uint mmioRange = BitConverter.ToUInt32(info, 20);

        if (magic != XsRegProbeContract.Magic || version != XsRegProbeContract.IoctlVersion)
        {
            _unavailableReason = $"驅動握手不符（magic=0x{magic:X8} version={version}，契約 0x{XsRegProbeContract.Magic:X8}/{XsRegProbeContract.IoctlVersion}）——拒用";
            return false;
        }
        if ((features & XsRegProbeContract.FeatureMmioRead) == 0)
        {
            _unavailableReason = "驅動未提供 MMIO 讀取能力";
            return false;
        }
        if (msrExact != XsRegProbeContract.MsrAllow.Count || msrRange != 1 || mmioRange != XsRegProbeContract.MmioAllow.Length)
        {
            _unavailableReason =
                $"驅動允許清單筆數與 managed 鏡像不符（驅動 {msrExact}+{msrRange} 條 MSR／{mmioRange} 條 MMIO，鏡像 {XsRegProbeContract.MsrAllow.Count}+1／{XsRegProbeContract.MmioAllow.Length}）——拒用，先更新兩邊契約";
            return false;
        }
        return true;
    }

    public bool Available
    {
        get { try { return EnsureHandshake(); } catch (Win32Exception) { return false; } }
    }

    public string? UnavailableReason
    {
        get { try { EnsureHandshake(); } catch (Win32Exception ex) { _unavailableReason = ex.Message; } return _unavailableReason; }
    }

    public byte[]? ReadBlock(ulong physicalAddress, int length)
    {
        _lastFailReason = null;
        if (!EnsureHandshake()) return null;
        if (length <= 0 || length > XsRegProbeContract.MmioMax)
        {
            _lastFailReason = $"長度 {length} 超出契約（1..{XsRegProbeContract.MmioMax}）";
            return null;
        }
        if (!XsRegProbeContract.MmioRangeAllowed(physicalAddress, length))
        {
            _lastFailReason = $"0x{physicalAddress:X}+{length} 不在驅動允許清單";
            return null;
        }

        var input = new byte[16]; // XRP_MMIO_REQUEST
        BitConverter.GetBytes(physicalAddress).CopyTo(input, 0);
        BitConverter.GetBytes(length).CopyTo(input, 8);
        var output = new byte[8 + length]; // Status + Length + Data

        if (!_channel!.TryIoctl(XsRegProbeContract.IoctlReadMmio, input, output, out _, out int ioctlError))
        {
            _lastFailReason = ioctlError == 5
                ? "驅動拒絕（位址不在核心端允許清單）"
                : $"READ_MMIO 失敗（Win32 錯誤 {ioctlError}）";
            return null;
        }
        uint status = BitConverter.ToUInt32(output, 0);
        uint got = BitConverter.ToUInt32(output, 4);
        if ((int)status < 0 || got != (uint)length)
        {
            _lastFailReason = $"驅動回報失敗（NTSTATUS 0x{status:X8}，取回 {got}/{length} bytes）";
            return null;
        }
        var data = new byte[length];
        Buffer.BlockCopy(output, 8, data, 0, length);
        return data;
    }

    public void Dispose() => _channel?.Dispose();
}
