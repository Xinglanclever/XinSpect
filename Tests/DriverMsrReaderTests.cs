using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// DriverMsrReader（IOCTL_XRP_READ_MSR_LIST 消費者）的契約：QUERY_INFO 握手對帳、清單外自查不打擾驅動、
/// 槽位 in/out 同塊不重排、驅動端拒絕與例外逐槽三態。全用假通道，測試不碰核心。
/// </summary>
public sealed class DriverMsrReaderTests
{
    [Fact]
    public void 未載驅動_三態標示裝置不存在_讀取回null()
    {
        using var reader = new DriverMsrReader(() => new FakeChannel { OpenResult = false, OpenError = 2 });
        Assert.False(reader.Available);
        Assert.Contains("未載入", reader.UnavailableReason);
        Assert.Null(reader.ReadMsr(0xCE));
        Assert.Equal(reader.UnavailableReason, reader.LastFailReason);
    }

    [Fact]
    public void 無權開啟_標需管理員提示()
    {
        using var reader = new DriverMsrReader(() => new FakeChannel { OpenResult = false, OpenError = 5 });
        Assert.False(reader.Available);
        Assert.Contains("管理員", reader.UnavailableReason);
    }

    [Fact]
    public void 缺MSR能力位元_拒用()
    {
        using var reader = new DriverMsrReader(() => new FakeChannel { Features = XsRegProbeContract.FeatureMmioRead });
        Assert.False(reader.Available);
        Assert.Contains("MSR", reader.UnavailableReason);
    }

    [Fact]
    public void 允許清單筆數對帳不符_拒用()
    {
        using var reader = new DriverMsrReader(() => new FakeChannel { MsrExact = 1 });
        Assert.False(reader.Available);
        Assert.Contains("不符", reader.UnavailableReason);
    }

    [Fact]
    public void 清單外MSR_managed自查拒絕_不打擾驅動()
    {
        var channel = new FakeChannel();
        using var reader = new DriverMsrReader(() => channel);

        Assert.Null(reader.ReadMsr(0x3B)); // 清單外（既有契約測試釘過）

        Assert.Contains("不在驅動允許清單", reader.LastFailReason);
        Assert.Equal(0, channel.ReadMsrListCalls);
    }

    [Fact]
    public void 批次讀取_同槽往返_成功與拒絕並存且順序不變()
    {
        var channel = new FakeChannel
        {
            Msrs = new Dictionary<uint, (uint Status, ulong Value)>
            {
                [0x3A] = (0, 0x5),
                [0x401] = (0, 0xABCDEF),
            },
        };
        using var reader = new DriverMsrReader(() => channel);

        var results = reader.ReadMsrList(0x3A, 0x401, 0x3B);

        Assert.Equal(3, results.Count);
        Assert.Equal(0x3Au, results[0].Msr);
        Assert.True(results[0].IsSuccess);
        Assert.Equal(0x5ul, results[0].Value);
        Assert.Equal(0x401u, results[1].Msr);
        Assert.Equal(0xABCDEFul, results[1].Value);
        Assert.Equal(0x3Bu, results[2].Msr);
        Assert.False(results[2].IsSuccess);
        Assert.Equal(DriverMsrResult.StatusAccessDenied, results[2].Status);
        Assert.Null(results[2].Value);
        Assert.Equal(1, channel.ReadMsrListCalls); // 一次批次往返
    }

    [Fact]
    public void 批次超過上限_自動分批且順序不變()
    {
        // MCA 範圍 0x400-0x4FF 供 65 個允許的 MSR（64+1，跨兩批）
        var msrs = Enumerable.Range(0, 65).ToDictionary(
            i => (uint)(0x400 + i), _ => (0u, 0x7777ul));
        var channel = new FakeChannel { Msrs = msrs };
        using var reader = new DriverMsrReader(() => channel);

        var results = reader.ReadMsrList(Enumerable.Range(0, 65).Select(i => (uint)(0x400 + i)).ToArray());

        Assert.Equal(65, results.Count);
        Assert.Equal(2, channel.ReadMsrListCalls);
        Assert.Equal(0x400u, results[0].Msr);
        Assert.Equal(0x440u, results[64].Msr);
        Assert.All(results, r => Assert.True(r.IsSuccess));
    }

    [Fact]
    public void 驅動端讀取例外_逐槽三態帶NTSTATUS()
    {
        var channel = new FakeChannel
        {
            Msrs = new Dictionary<uint, (uint Status, ulong Value)> { [0xC80] = (0xC0000096, 0) },
        };
        using var reader = new DriverMsrReader(() => channel);

        Assert.Null(reader.ReadMsr(0xC80));
        Assert.Contains("C0000096", reader.LastFailReason);
    }

    [Fact]
    public void IOCTL層失敗_全部槽位三態帶原因()
    {
        var channel = new FakeChannel { MsrListWin32Error = 5 };
        using var reader = new DriverMsrReader(() => channel);

        var results = reader.ReadMsrList(0xCE, 0x3A);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => { Assert.False(r.IsSuccess); Assert.Null(r.Value); });
        Assert.Contains("READ_MSR_LIST", reader.LastFailReason);
    }

    [Fact]
    public void 平台安全事實的兩條MSR都在驅動允許清單()
    {
        // 消費者契約：驅動連線時平台安全事實要能整組走自家後端，清單缺任一條就會誤標三態
        Assert.True(XsRegProbeContract.MsrAllowed(0x3A));
        Assert.True(XsRegProbeContract.MsrAllowed(0xC80));
    }

    [Fact]
    public void 驅動連線時平台安全事實走驅動後端_拒絕原因進三態事實()
    {
        var channel = new FakeChannel
        {
            Msrs = new Dictionary<uint, (uint Status, ulong Value)>
            {
                [0x3A] = (0, 0x5),                // Lock=1、VMX outside SMX=1
                [0xC80] = (0xC0000096, 0),        // 驅動端讀取例外
            },
        };
        using var reader = new DriverMsrReader(() => channel);

        var facts = PlatformSecurityMsrService.Collect(reader, DateTimeOffset.UtcNow);

        var featureControl = Assert.Single(facts, f => f.Key == "platform.feature_control");
        Assert.Equal(FactAvailability.Present, featureControl.Availability);
        Assert.Contains("已鎖定", featureControl.Value);
        var debugInterface = Assert.Single(facts, f => f.Key == "platform.debug_interface");
        Assert.Equal(FactAvailability.ReadError, debugInterface.Availability);
        Assert.Contains("C0000096", debugInterface.UnavailableReason);
    }

    /// <summary>假通道：QUERY_INFO 回契約鏡像數值、READ_MSR_LIST 逐槽回字典值或 ACCESS_DENIED（模擬驅動語義）。</summary>
    private sealed class FakeChannel : IRawDeviceChannel
    {
        public bool OpenResult { get; init; } = true;
        public int OpenError { get; init; }
        public uint Features { get; init; } = XsRegProbeContract.FeatureMsrRead | XsRegProbeContract.FeatureMmioRead;
        public uint MsrExact { get; init; } = (uint)XsRegProbeContract.MsrAllow.Count;
        public int MsrListWin32Error { get; init; }
        public Dictionary<uint, (uint Status, ulong Value)> Msrs { get; init; } = new();
        public int ReadMsrListCalls { get; private set; }

        public bool TryOpen(string devicePath, out int win32Error)
        {
            win32Error = OpenError;
            return OpenResult;
        }

        public bool TryIoctl(uint code, byte[]? input, byte[] output, out int bytesReturned, out int win32Error)
        {
            if (code == XsRegProbeContract.IoctlQueryInfo)
            {
                BitConverter.GetBytes(XsRegProbeContract.Magic).CopyTo(output, 0);
                BitConverter.GetBytes(XsRegProbeContract.IoctlVersion).CopyTo(output, 4);
                BitConverter.GetBytes(Features).CopyTo(output, 8);
                BitConverter.GetBytes(MsrExact).CopyTo(output, 12);
                BitConverter.GetBytes(1u).CopyTo(output, 16);
                BitConverter.GetBytes((uint)XsRegProbeContract.MmioAllow.Length).CopyTo(output, 20);
                bytesReturned = 24;
                win32Error = 0;
                return true;
            }
            if (code == XsRegProbeContract.IoctlReadMsrList)
            {
                ReadMsrListCalls++;
                if (MsrListWin32Error != 0) { bytesReturned = 0; win32Error = MsrListWin32Error; return false; }
                int count = BitConverter.ToInt32(input!, 0);
                for (int i = 0; i < count; i++)
                {
                    int slot = 8 + i * 16;
                    uint msr = BitConverter.ToUInt32(input!, slot);
                    var (status, value) = Msrs.TryGetValue(msr, out var hit) ? hit : (0xC0000022u, 0ul);
                    BitConverter.GetBytes(msr).CopyTo(output, slot);
                    BitConverter.GetBytes(status).CopyTo(output, slot + 4);
                    BitConverter.GetBytes(value).CopyTo(output, slot + 8);
                }
                bytesReturned = 8 + count * 16;
                win32Error = 0;
                return true;
            }
            bytesReturned = 0;
            win32Error = 87; // ERROR_INVALID_PARAMETER
            return false;
        }

        public void Dispose() { }
    }
}
