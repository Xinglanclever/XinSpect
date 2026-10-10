using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 核心傾印解碼器金標（P0-04 RC-001）：合成 PAGEDU64 檔頭＋已知答案逐欄比對；
/// 位移（0x38 代碼／0x40..0x58 參數）以本機真實傾印驗證過；bugcheck 知識表對準 Microsoft Learn 逐條。
/// </summary>
public class KernelDumpDecoderTests
{
    private static byte[] BuildHeader(uint code, ulong p1 = 0, ulong p2 = 0x1000, ulong p3 = 0, ulong p4 = 5)
    {
        byte[] b = new byte[0x60];
        Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(b, 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8, 4), 15);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12, 4), 26100);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x38, 4), code);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x40, 8), p1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x48, 8), p2);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x50, 8), p3);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x58, 8), p4);
        return b;
    }

    [Fact]
    public void Pagedu64_逐欄比對()
    {
        var h = KernelDumpDecoder.Parse(BuildHeader(0x124, 0, 0xFFFF890449144028, 0xF2000040, 5));
        Assert.NotNull(h);
        Assert.Equal("PAGEDU64", h.Kind);
        Assert.Equal(15, h.MajorVersion);
        Assert.Equal(26100, h.BuildNumber);
        Assert.Equal(0x124u, h.BugCheckCode);
        Assert.Equal(0xFFFF890449144028UL, h.Parameter2);
        Assert.Equal(5UL, h.Parameter4);
    }

    [Fact]
    public void 太短或壞簽名_回null不猜()
    {
        Assert.Null(KernelDumpDecoder.Parse(new byte[0x5F]));
        byte[] wrong = new byte[0x60];
        Encoding.ASCII.GetBytes("PAGEDUMP").CopyTo(wrong, 0);
        Assert.Null(KernelDumpDecoder.Parse(wrong)); // x86 版面未對照——不解碼
        Assert.Null(KernelDumpDecoder.Parse(null));
    }

    [Theory]
    [InlineData(0x124u, "WHEA_UNCORRECTABLE_ERROR")]
    [InlineData(0xAu, "IRQL_NOT_LESS_OR_EQUAL")]
    [InlineData(0xD1u, "DRIVER_IRQL_NOT_LESS_OR_EQUAL")]
    [InlineData(0xCEu, "DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS")]
    [InlineData(0x133u, "DPC_WATCHDOG_VIOLATION")]
    [InlineData(0xDEADDEADu, "MANUALLY_INITIATED_CRASH1")]
    public void 知識表_對準MicrosoftLearn逐條(uint code, string expected)
    {
        Assert.Equal(expected, KernelDumpDecoder.CodeName(code));
    }

    [Fact]
    public void 未收錄代碼_回null不猜名稱()
    {
        Assert.Null(KernelDumpDecoder.CodeName(0x7777));
        Assert.True(KernelDumpDecoder.TableCount() >= 60, "知識表縮水——只許增不許減");
    }
}

/// <summary>
/// Minidump 事實服務（RC-001）三態契約：目錄缺席＝好消息如實列、列舉失敗＝權限、
/// 空目錄＝0 份、正常列舉＝逐份解碼；「解碼不是診斷」界線要寫進值裡。
/// </summary>
public class MinidumpFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    private static byte[] Head(uint code)
    {
        byte[] b = new byte[0x60];
        Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x38, 4), code);
        return b;
    }

    [Fact]
    public void 目錄不存在_如實說是好消息()
    {
        var facts = MinidumpFactsService.Collect(At, () => null);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("好消息", f.UnavailableReason);
    }

    [Fact]
    public void 列舉失敗_標權限不足()
    {
        var facts = MinidumpFactsService.Collect(At, () => throw new System.UnauthorizedAccessException("denied"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
        Assert.Contains("讀不到不等於沒有", f.UnavailableReason);
    }

    [Fact]
    public void 空目錄_如實列零份()
    {
        var facts = MinidumpFactsService.Collect(At, () => []);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("0 份", f.Value);
    }

    [Fact]
    public void 未收錄代碼_如實標原碼不猜名稱()
    {
        var facts = MinidumpFactsService.Collect(At, () =>
        [
            new DumpListing("010126-00001-01.dmp", At, Head(0x7777)),
        ]);
        var latest = facts.Single(f => f.Key == "rc.dump.latest");
        Assert.Contains("0x7777", latest.Value);
        Assert.Contains("未收錄（不猜名稱）", latest.Value);
    }

    [Fact]
    public void 正常列舉_逐份解碼與日期並列()
    {
        var facts = MinidumpFactsService.Collect(At, () =>
        [
            new DumpListing("082326-16656-01.dmp", At, Head(0x124)),
            new DumpListing("061726-15031-01.dmp", At.AddDays(-60), Head(0xA)),
            new DumpListing("broken.dmp", At.AddDays(-30), null),
        ]);

        Assert.Equal(3, facts.Count);
        var count = facts.Single(f => f.Key == "rc.dump.count");
        Assert.Equal(3, count.NumericValue);
        Assert.Contains("1 份檔頭讀不到", count.Value);
        Assert.Contains("解碼不是診斷", count.Value);

        var latest = facts.Single(f => f.Key == "rc.dump.latest");
        Assert.Contains("082326-16656-01.dmp", latest.Value);
        Assert.Contains("2026-08-23", latest.Value);
        Assert.Contains("WHEA_UNCORRECTABLE_ERROR", latest.Value);

        var sample = facts.Single(f => f.Key == "rc.dump.sample");
        Assert.True(sample.Sensitive, "參數可能是核心位址——樣本要標敏感，匯出預設遮蔽");
        Assert.Contains("0xA", sample.Value);
    }
}
