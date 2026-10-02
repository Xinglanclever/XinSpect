using Xunit;

namespace XinSpect.Tests;

public sealed class DriverMmioReaderTests
{
    /// <summary>假通道：QUERY_INFO 回可控資訊、READ_MMIO 依字典回位元組或指定錯誤；完全不碰真實驅動。</summary>
    private sealed class FakeChannel : IRawDeviceChannel
    {
        public bool OpenResult { get; init; } = true;
        public int OpenError { get; init; }
        public uint Magic { get; init; } = XsRegProbeContract.Magic;
        public uint Version { get; init; } = XsRegProbeContract.IoctlVersion;
        public uint Features { get; init; } = XsRegProbeContract.FeatureMsrRead | XsRegProbeContract.FeatureMmioRead;
        public uint MsrExact { get; init; } = (uint)XsRegProbeContract.MsrAllow.Count;
        public uint MsrRange { get; init; } = 1;
        public uint MmioRange { get; init; } = (uint)XsRegProbeContract.MmioAllow.Length;
        public IReadOnlyDictionary<ulong, byte[]> Blocks { get; init; } =
            new Dictionary<ulong, byte[]>();
        public int ReadMmioWin32Error { get; init; }
        public uint ReadMmioNtStatus { get; init; }
        public uint ReadMmioGot { get; init; } = uint.MaxValue;
        public int QueryIoctlError { get; init; }
        public int ReadMmioCallCount { get; private set; }

        public bool TryOpen(string devicePath, out int win32Error)
        {
            win32Error = OpenError;
            return OpenResult;
        }

        public bool TryIoctl(uint code, byte[]? input, byte[] output, out int bytesReturned, out int win32Error)
        {
            if (code == XsRegProbeContract.IoctlQueryInfo)
            {
                if (QueryIoctlError != 0) { bytesReturned = 0; win32Error = QueryIoctlError; return false; }
                BitConverter.GetBytes(Magic).CopyTo(output, 0);
                BitConverter.GetBytes(Version).CopyTo(output, 4);
                BitConverter.GetBytes(Features).CopyTo(output, 8);
                BitConverter.GetBytes(MsrExact).CopyTo(output, 12);
                BitConverter.GetBytes(MsrRange).CopyTo(output, 16);
                BitConverter.GetBytes(MmioRange).CopyTo(output, 20);
                bytesReturned = 24;
                win32Error = 0;
                return true;
            }
            if (code == XsRegProbeContract.IoctlReadMmio)
            {
                ReadMmioCallCount++;
                if (ReadMmioWin32Error != 0) { bytesReturned = 0; win32Error = ReadMmioWin32Error; return false; }
                ulong addr = BitConverter.ToUInt64(input!, 0);
                int len = BitConverter.ToInt32(input!, 8);
                var covered = Blocks.FirstOrDefault(kv => addr >= kv.Key && addr + (ulong)len <= kv.Key + (ulong)kv.Value.Length);
                if (covered.Value is null)
                {
                    BitConverter.GetBytes(ReadMmioNtStatus == 0 ? 0xC0000001u : ReadMmioNtStatus).CopyTo(output, 0);
                    BitConverter.GetBytes(ReadMmioGot == uint.MaxValue ? 0u : ReadMmioGot).CopyTo(output, 4);
                    bytesReturned = 8;
                    win32Error = 0;
                    return true; // 驅動以 NTSTATUS 回報失敗（本測試用 STATUS_UNSUCCESSFUL 模擬）
                }
                BitConverter.GetBytes(0u).CopyTo(output, 0);
                BitConverter.GetBytes(len).CopyTo(output, 4);
                Buffer.BlockCopy(covered.Value, (int)(addr - covered.Key), output, 8, len);
                bytesReturned = 8 + len;
                win32Error = 0;
                return true;
            }
            bytesReturned = 0;
            win32Error = 87; // ERROR_INVALID_PARAMETER
            return false;
        }

        public void Dispose() { }
    }

    [Fact]
    public void 未載驅動_三態標示裝置不存在()
    {
        var reader = new DriverMmioReader(() => new FakeChannel { OpenResult = false, OpenError = 2 });
        Assert.False(reader.Available);
        Assert.Contains("未載入", reader.UnavailableReason);
        Assert.Null(reader.ReadBlock(0xFED10004, 4));
    }

    [Fact]
    public void 無權開啟_標需管理員提示()
    {
        var reader = new DriverMmioReader(() => new FakeChannel { OpenResult = false, OpenError = 5 });
        Assert.False(reader.Available);
        Assert.Contains("管理員", reader.UnavailableReason);
    }

    [Fact]
    public void 版本不符_拒用不送讀取()
    {
        var channel = new FakeChannel { Version = 99 };
        var reader = new DriverMmioReader(() => channel);
        Assert.False(reader.Available);
        Assert.Contains("握手不符", reader.UnavailableReason);
        Assert.Null(reader.ReadBlock(0xFED10004, 4));
        Assert.Equal(0, channel.ReadMmioCallCount);
    }

    [Fact]
    public void 允許清單筆數對帳不符_拒用並說明兩邊數字()
    {
        var reader = new DriverMmioReader(() => new FakeChannel { MsrExact = 99 });
        Assert.False(reader.Available);
        Assert.Contains("不符", reader.UnavailableReason);
        Assert.Contains(XsRegProbeContract.MsrAllow.Count.ToString(), reader.UnavailableReason);
    }

    [Fact]
    public void 握手成功_讀得到允許清單內的位元組()
    {
        var block = new byte[0x88];
        BitConverter.GetBytes(0x8000u).CopyTo(block, 0x04); // HSFSTS：FLOCKDN
        var reader = new DriverMmioReader(() => new FakeChannel { Blocks = new Dictionary<ulong, byte[]> { [0xFED10000] = block } });
        Assert.True(reader.Available);
        var data = reader.ReadBlock(0xFED10004, 4);
        Assert.NotNull(data);
        Assert.Equal(0x8000u, BitConverter.ToUInt32(data!, 0));
    }

    [Fact]
    public void 清單外位址_managed先擋_不打擾驅動()
    {
        var channel = new FakeChannel();
        var reader = new DriverMmioReader(() => channel);
        Assert.True(reader.Available);
        Assert.Null(reader.ReadBlock(0xDEADBEE0, 4));
        Assert.Contains("允許清單", reader.LastFailReason);
        Assert.Equal(0, channel.ReadMmioCallCount);
    }

    [Fact]
    public void 驅動回ACCESS_DENIED_如實轉三態()
    {
        var reader = new DriverMmioReader(() => new FakeChannel { ReadMmioWin32Error = 5 });
        Assert.True(reader.Available);
        Assert.Null(reader.ReadBlock(0xFED10004, 4));
        Assert.Contains("拒絕", reader.LastFailReason);
    }

    [Fact]
    public void 跨出允許範圍端點_managed先擋()
    {
        var channel = new FakeChannel();
        var reader = new DriverMmioReader(() => channel);
        Assert.Null(reader.ReadBlock(0xFED10080, 16)); // SPIBAR 允許到 +0x87，0x80+16 跨出
        Assert.Equal(0, channel.ReadMmioCallCount);
    }
}
