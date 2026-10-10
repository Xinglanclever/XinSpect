using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Prefetch 事實服務（FS-003 檔名層）三態契約：目錄缺席、列舉失敗（權限）、空目錄、正常列舉——
/// 「檔名存在≠執行成功」「空目錄≠沒有執行過程式」「近似時間」界線要寫進值裡。
/// </summary>
public class PrefetchFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    private static byte[] SccaHead(string exe) =>
        PrefetchHeaderDecoderTestsAccess.BuildScca(30, exe, 0x11223344u);

    [Fact]
    public void 目錄不存在_如實說沒有預取資料()
    {
        var facts = PrefetchFactsService.Collect(At, () => null);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有預取資料", f.UnavailableReason);
    }

    [Fact]
    public void 列舉失敗_標權限不足()
    {
        var facts = PrefetchFactsService.Collect(At, () => throw new System.UnauthorizedAccessException("denied"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
        Assert.Contains("UnauthorizedAccessException", f.UnavailableReason);
        Assert.Contains("讀不到不等於沒有", f.UnavailableReason);
    }

    [Fact]
    public void 空目錄_如實列零檔不冒充沒有執行()
    {
        var facts = PrefetchFactsService.Collect(At, () => []);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是「沒有執行過程式」", f.UnavailableReason);
    }

    [Fact]
    public void 正常列舉_MAM與SCCA混合_逐欄與界線()
    {
        var t1 = new DateTimeOffset(2026, 10, 6, 23, 37, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        byte[] mam = new byte[64];
        Encoding.ASCII.GetBytes("MAM").CopyTo(mam, 0);
        mam[3] = 0x04;
        BinaryPrimitives.WriteUInt32LittleEndian(mam.AsSpan(4, 4), 1000);

        var facts = PrefetchFactsService.Collect(At, () =>
        [
            new PrefetchListing("NOTEPAD.EXE-11223344.pf", @"C:\Windows\Prefetch\NOTEPAD.EXE-11223344.pf", t1, SccaHead("NOTEPAD.EXE")),
            new PrefetchListing("TOOL.EXE-55667788.pf", @"C:\Windows\Prefetch\TOOL.EXE-55667788.pf", t2, mam),
        ]);

        Assert.Equal(4, facts.Count);
        var count = facts.Single(f => f.Key == "fs.pf.count");
        Assert.Equal(2, count.NumericValue);
        Assert.Contains("曾被預取", count.Value);
        Assert.Contains("不等於執行成功", count.Value);

        var formats = facts.Single(f => f.Key == "fs.pf.formats");
        Assert.Contains("v30（Win10/11） 1", formats.Value);
        Assert.Contains("MAM 0x04 1", formats.Value);

        var latest = facts.Single(f => f.Key == "fs.pf.latest");
        Assert.Contains("NOTEPAD.EXE-11223344.pf", latest.Value);

        var sample = facts.Single(f => f.Key == "fs.pf.sample");
        Assert.Contains("NOTEPAD.EXE", sample.Value);
        Assert.Contains("近似不是精確時刻", sample.Value);
        Assert.Contains("MAM 壓縮內容未解壓", sample.Value);
    }

    [Fact]
    public void 全部檔頭讀不到_如實計數略過不冒充解析()
    {
        var facts = PrefetchFactsService.Collect(At, () =>
        [
            new PrefetchListing("A.EXE-00000000.pf", @"C:\Windows\Prefetch\A.pf", At, null),
            new PrefetchListing("B.EXE-00000001.pf", @"C:\Windows\Prefetch\B.pf", At, null),
        ]);
        Assert.Equal(4, facts.Count);
        var count = facts.Single(f => f.Key == "fs.pf.count");
        Assert.Equal(2, count.NumericValue);
        Assert.Contains("讀不到", count.Value);
        var formats = facts.Single(f => f.Key == "fs.pf.formats");
        Assert.Contains("檔頭讀不到 2", formats.Value);
    }
}

/// <summary>給服務測試重用的 SCCA 合成器（與解碼器金標同款版面）。</summary>
internal static class PrefetchHeaderDecoderTestsAccess
{
    public static byte[] BuildScca(int version, string exe, uint hash)
    {
        byte[] b = new byte[80];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0, 4), version);
        Encoding.ASCII.GetBytes("SCCA").CopyTo(b, 4);
        byte[] name = Encoding.Unicode.GetBytes(exe);
        Array.Resize(ref name, 60);
        name.CopyTo(b, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(76, 4), hash);
        return b;
    }
}
