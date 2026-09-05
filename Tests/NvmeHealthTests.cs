using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// NVMe 健康紀錄的型別化快照：位移與 128 位元計數器的取法。
/// </summary>
/// <remarks>
/// 1.9.1-B1 修掉的位移錯誤（關鍵警告被當 2 位元組、128 位元計數器被當 64 位元）之所以
/// 帶了三個發佈版沒被抓到，是因為當年的測試資料照著錯誤的實作位移合成——測試把 bug 鎖死了。
/// 所以這裡的每個欄位都用**互不相同的可辨識值**分別填入，任何串位都會讓某個欄位讀到別人的值。
/// <para>
/// 實機原始位元組的基準測試待補（見 <c>Fixtures/README.md</c>）：本機的磁碟 IOCTL 目前處於
/// 卡死狀態，要等重開機後才能安全傾印。合成資料能驗「欄位沒有互相串位」，
/// 但不能取代真實位元組——那一項是待辦。
/// </para>
/// </remarks>
public class NvmeHealthTests
{
    /// <summary>每個 128 位元計數器都填入不同的低 64 位元值，並在高 64 位元塞垃圾。</summary>
    private static byte[] Synthetic()
    {
        var log = new byte[NvmeHealth.LogSize];
        log[NvmeLogDecoder.OffCriticalWarning] = 0b0000_1010;                        // bit1 溫度、bit3 唯讀
        BitConverter.GetBytes((ushort)320).CopyTo(log, NvmeLogDecoder.OffCompositeTemp);   // 320 K = 47 °C
        log[NvmeLogDecoder.OffAvailableSpare] = 97;
        log[NvmeLogDecoder.OffSpareThreshold] = 10;
        log[NvmeLogDecoder.OffPercentageUsed] = 3;

        void Counter(int off, ulong low)
        {
            BitConverter.GetBytes(low).CopyTo(log, off);
            BitConverter.GetBytes(0xDEAD_BEEFul).CopyTo(log, off + 8);   // 高 64 位元：不該被讀進來
        }

        Counter(NvmeLogDecoder.OffDataUnitsRead, 111);
        Counter(NvmeLogDecoder.OffDataUnitsWritten, 222);
        Counter(NvmeLogDecoder.OffHostReadCommands, 333);
        Counter(NvmeLogDecoder.OffHostWriteCommands, 444);
        Counter(NvmeLogDecoder.OffPowerCycles, 555);
        Counter(NvmeLogDecoder.OffPowerOnHours, 666);
        Counter(NvmeLogDecoder.OffUnsafeShutdowns, 777);
        Counter(NvmeLogDecoder.OffMediaErrors, 888);
        Counter(NvmeLogDecoder.OffErrorLogEntries, 999);
        return log;
    }

    [Fact]
    public void 每個欄位都從自己的位移取值_不得互相串位()
    {
        var h = NvmeHealth.Decode(Synthetic())!.Value;

        Assert.Equal(0b0000_1010, h.CriticalWarning);
        Assert.Equal(320, h.CompositeTempKelvin);
        Assert.Equal(47, h.CompositeTempCelsius);
        Assert.Equal(97, h.AvailableSparePercent);
        Assert.Equal(10, h.SpareThresholdPercent);
        Assert.Equal(3, h.PercentageUsed);
        Assert.Equal(111u, h.DataUnitsRead);
        Assert.Equal(222u, h.DataUnitsWritten);
        Assert.Equal(333u, h.HostReadCommands);
        Assert.Equal(444u, h.HostWriteCommands);
        Assert.Equal(555u, h.PowerCycles);
        Assert.Equal(666u, h.PowerOnHours);
        Assert.Equal(777u, h.UnsafeShutdowns);
        Assert.Equal(888u, h.MediaErrors);
        Assert.Equal(999u, h.ErrorLogEntries);
    }

    [Fact]
    public void 長度不足回null_不得回一組零()
    {
        Assert.Null(NvmeHealth.Decode(new byte[16]));
        Assert.Null(NvmeHealth.Decode(new byte[NvmeHealth.LogSize - 1]));
        Assert.Null(NvmeHealth.Decode(null!));
    }

    [Fact]
    public void 全零紀錄是合法的_那是一顆全新的碟()
    {
        var h = NvmeHealth.Decode(new byte[NvmeHealth.LogSize])!.Value;
        Assert.Equal(0u, h.PowerOnHours);
        Assert.Null(h.CompositeTempCelsius);   // 溫度 0 K 代表沒讀到，不是零下 273 度
    }

    [Theory]
    [InlineData(0ul, 0.0)]
    [InlineData(2097152ul, 1000.0)]      // 2,097,152 單位 × 1000 × 512 B = 1000 GiB
    public void 寫入量換算_一個單位是1000乘512位元組(ulong units, double expectedGiB)
        => Assert.Equal(expectedGiB, NvmeHealth.DataUnitsToGiB(units), 1);

    [Fact]
    public void 關鍵警告逐位元解讀沿用既有解碼器()
    {
        var h = NvmeHealth.Decode(Synthetic())!.Value;
        var warns = NvmeLogDecoder.CriticalWarnings(h.CriticalWarning);

        Assert.Equal(2, warns.Count);
        Assert.Contains(warns, w => w.Bit == 1);
        Assert.Contains(warns, w => w.Bit == 3);
    }
}
