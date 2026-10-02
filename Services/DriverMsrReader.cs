using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>READ_MSR_LIST 單槽往返結果：成功帶值；拒絕／例外／驅動不可用帶 NTSTATUS 與原因，值一律 null（不以 0 頂替）。</summary>
public readonly record struct DriverMsrResult(uint Msr, bool IsSuccess, ulong? Value, uint Status, string? Reason = null)
{
    public const uint StatusAccessDenied = 0xC0000022;
    public const uint StatusInvalidParameter = 0xC000000D;

    /// <summary>NTSTATUS 轉人類可讀原因（進三態事實的 UnavailableReason）；本機拒絕（驅動不可用等）直接帶 Reason。</summary>
    public string FailReason => Reason ?? Status switch
    {
        StatusAccessDenied => $"MSR 0x{Msr:X} 不在驅動允許清單（核心端拒絕）",
        StatusInvalidParameter => $"MSR 0x{Msr:X} 驅動拒收（參數無效）",
        _ => $"MSR 0x{Msr:X} 讀取失敗（NTSTATUS 0x{Status:X8}，此平台可能未實作）",
    };
}

/// <summary>
/// XsRegProbe 驅動後端的 IKernelMsrReader：先 QUERY_INFO 能力協商（與 DriverMmioReader 同款對帳），
/// 再以 IOCTL_XRP_READ_MSR_LIST 批次讀（槽位 in/out 同塊、不重排）。managed 端先照契約鏡像自查允許清單，
/// 不在清單就不打擾驅動；驅動端逐槽回 NTSTATUS，拒絕／例外如實三態。
/// </summary>
public sealed class DriverMsrReader : IKernelMsrReader, IDisposable
{
    private readonly Func<IRawDeviceChannel> _channelFactory;
    private IRawDeviceChannel? _channel;
    private bool _handshaken;
    private string? _unavailableReason;
    private string? _lastFailReason;

    public DriverMsrReader(Func<IRawDeviceChannel>? channelFactory = null) =>
        _channelFactory = channelFactory ?? (() => new RawDeviceChannel());

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
            _unavailableReason = $"驅動握手不符（magic=0x{magic:X8} version={version}）——拒用";
            return false;
        }
        if ((features & XsRegProbeContract.FeatureMsrRead) == 0)
        {
            _unavailableReason = "驅動未提供 MSR 讀取能力";
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

    public ulong? ReadMsr(uint index)
    {
        _lastFailReason = null;
        if (!XsRegProbeContract.MsrAllowed(index))
        {
            _lastFailReason = $"MSR 0x{index:X} 不在驅動允許清單";
            return null;
        }
        var r = ReadMsrList(index).Single();
        if (!r.IsSuccess) _lastFailReason = r.FailReason;
        return r.Value;
    }

    /// <summary>批次讀取：超過 XRP_MSR_MAX_BATCH 自動分批，結果順序與輸入一致。</summary>
    public IReadOnlyList<DriverMsrResult> ReadMsrList(params uint[] msrs) => ReadMsrList((IReadOnlyList<uint>)msrs);

    public IReadOnlyList<DriverMsrResult> ReadMsrList(IReadOnlyList<uint> msrs)
    {
        var results = new DriverMsrResult[msrs.Count];
        int filled = 0;
        foreach (var batch in msrs.Chunk(XsRegProbeContract.MsrMaxBatch))
        {
            if (!EnsureHandshake())
            {
                foreach (var msr in batch)
                    results[filled++] = new DriverMsrResult(msr, false, null, DriverMsrResult.StatusAccessDenied, UnavailableReason);
                _lastFailReason = UnavailableReason;
                continue;
            }
            foreach (var r in ReadBatchCore(batch)) results[filled++] = r;
        }
        return results;
    }

    private List<DriverMsrResult> ReadBatchCore(ReadOnlySpan<uint> batch)
    {
        var results = new List<DriverMsrResult>(batch.Length);
        var input = new byte[8 + batch.Length * 16]; // XRP_MSR_REQUEST + XRP_MSR_SLOT[Count]
        BitConverter.GetBytes(batch.Length).CopyTo(input, 0);
        for (int i = 0; i < batch.Length; i++)
            BitConverter.GetBytes(batch[i]).CopyTo(input, 8 + i * 16);
        var output = new byte[input.Length];

        if (!_channel!.TryIoctl(XsRegProbeContract.IoctlReadMsrList, input, output, out _, out int ioctlError))
        {
            _lastFailReason = ioctlError == 5
                ? "READ_MSR_LIST 驅動拒絕（允許清單核心端不符或權限不足）"
                : $"READ_MSR_LIST 失敗（Win32 錯誤 {ioctlError}）";
            foreach (uint msr in batch)
                results.Add(new DriverMsrResult(msr, false, null, DriverMsrResult.StatusAccessDenied, _lastFailReason));
            return results;
        }

        for (int i = 0; i < batch.Length; i++)
        {
            int slot = 8 + i * 16;
            uint msr = BitConverter.ToUInt32(output, slot);
            uint status = BitConverter.ToUInt32(output, slot + 4);
            ulong value = BitConverter.ToUInt64(output, slot + 8);
            results.Add(status == 0
                ? new DriverMsrResult(msr, true, value, 0)
                : new DriverMsrResult(msr, false, null, status));
        }
        return results;
    }

    public void Dispose() => _channel?.Dispose();
}
