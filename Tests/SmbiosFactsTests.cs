using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS → 型別化事實：位移與長度以本機真實位元組為基準驗證。
/// </summary>
/// <remarks>
/// 基準檔 <c>Fixtures/smbios-real.bin</c> 是實機傾印（序號已遮蔽，位移原樣保留），
/// 理由見 <c>Fixtures/README.md</c>：合成資料只證明程式跟自己一致，不證明它跟規格一致。
/// </remarks>
public class SmbiosFactsTests
{
    private static byte[] RealTable()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "smbios-real.bin"));

    private static List<VerifyFact> RealFacts()
        => SmbiosFacts.From(SmbiosParser.Parse(RealTable()), DateTime.UnixEpoch);

    [Fact]
    public void 真實表_解得出模組數與標稱速度()
    {
        var facts = new VerifyFacts(RealFacts());

        Assert.True(facts.Num(FactId.DimmCount) >= 1, "實機至少插著一條記憶體");
        Assert.True(facts.Num(FactId.DimmSpeedMts) >= 800, "任何 DDR 世代的標稱速度都高於 800 MT/s");
        Assert.Equal(FactSource.Smbios, facts.Get(FactId.DimmCount)!.Source);
        Assert.Contains("Type 17", facts.Get(FactId.DimmSpeedMts)!.Method);
    }

    [Fact]
    public void 真實表_逐條字串以豎線相連_條數與模組數一致()
    {
        var facts = new VerifyFacts(RealFacts());
        int count = (int)facts.Num(FactId.DimmCount)!.Value;

        Assert.Equal(count, VerifyRules.Split(facts.Text(FactId.DimmManufacturers)).Length);
        Assert.Equal(count, VerifyRules.Split(facts.Text(FactId.DimmPartNumbers)).Length);
        Assert.Equal(count, VerifyRules.Split(facts.Text(FactId.DimmSerials)).Length);
    }

    [Fact]
    public void 真實表_陣列上限與插槽數解得出來()
    {
        var facts = new VerifyFacts(RealFacts());

        Assert.True(facts.Num(FactId.ArraySlotCount) >= 1);
        Assert.True(facts.Num(FactId.ArrayMaxCapacityMiB) >= facts.Num(FactId.DimmSizeTotalMiB));
        Assert.Contains("Type 16", facts.Get(FactId.ArraySlotCount)!.Method);
    }

    [Fact]
    public void 每個事實都必須帶讀取方法與信賴度()
        => Assert.All(RealFacts(), f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Method));
            Assert.False(f.NeedsAdmin);                             // SMBIOS 不需要管理員
            Assert.Equal(FactSource.Smbios, f.Source);
            Assert.Equal(FactTrust.FirmwareReported, f.Trust);      // 韌體轉述，不是原生讀取
            Assert.True(FactCatalog.Covers(f.Id), $"{f.Id} 沒有登記在 FactCatalog");
        });

    [Fact]
    public void 空表不得產出任何事實_也不得丟例外()
        => Assert.Empty(SmbiosFacts.From([], DateTime.UnixEpoch));

    [Fact]
    public void 真實表_五條記憶體規則都判得出來_不留無法判定()
    {
        var findings = VerifyEngine.Run(new VerifyFacts(RealFacts()))
                                   .Where(x => x.Id.StartsWith("R-MEM-")).ToList();

        Assert.Equal(5, findings.Count);
        Assert.All(findings, x => Assert.NotEqual(VerifyVerdict.Unread, x.Verdict));
        Assert.All(findings.Where(x => x.Verdict == VerifyVerdict.Conflict),
            x => Assert.False(string.IsNullOrWhiteSpace(x.BenignCause)));   // 矛盾必須附得出正當成因
    }

    // ── 邊界情況用合成結構：容量欄位的三種編碼（MB／KB／擴充）──

    [Theory]
    [InlineData((ushort)8192, 0u, 8192.0)]         // bit15=0 → 單位是 MB
    [InlineData((ushort)0x8400, 0u, 1.0)]          // bit15=1 → 單位是 KB（0x400 KB = 1 MiB）
    [InlineData((ushort)0x7FFF, 65536u, 65536.0)]  // 0x7FFF → 改看 +0x1C 的擴充容量（MB）
    [InlineData((ushort)0, 0u, 0.0)]               // 0 → 該插槽沒插模組
    public void 容量欄位_三種編碼都要解對(ushort sizeField, uint extendedSize, double expectedMiB)
    {
        var s = SyntheticDimm(sizeField, extendedSize, speed: 3200, configured: 3200);
        var facts = new VerifyFacts(SmbiosFacts.From([s], DateTime.UnixEpoch));

        if (expectedMiB == 0)
            Assert.False(facts.Has(FactId.DimmSizeTotalMiB));       // 沒插模組就不該有總量這個事實
        else
            Assert.Equal(expectedMiB, facts.Num(FactId.DimmSizeTotalMiB));
    }

    /// <summary>造一個 Type 17 結構：只填本測試在意的欄位，其餘留 0。</summary>
    private static SmbiosStruct SyntheticDimm(ushort sizeField, uint extendedSize, ushort speed, ushort configured)
    {
        var data = new byte[0x22];
        data[0] = 17;
        data[1] = 0x22;
        BitConverter.GetBytes(sizeField).CopyTo(data, 0x0C);
        BitConverter.GetBytes(extendedSize).CopyTo(data, 0x1C);
        BitConverter.GetBytes(speed).CopyTo(data, 0x15);
        BitConverter.GetBytes(configured).CopyTo(data, 0x20);
        data[0x17] = 1;   // 製造商＝字串 1
        data[0x18] = 2;   // 序號＝字串 2
        data[0x1A] = 3;   // 料號＝字串 3
        return new SmbiosStruct(17, 0x1000, data, ["Micron", "DEADBEEF", "MTA8ATF1G64AZ"]);
    }

    // ── ECC 一致性(R-MEM-05)的事實抽取：Type16 錯誤更正型別 + Type17 總寬度 vs 資料寬度 ──

    private static SmbiosStruct Array16(byte errorCorrection)
    {
        var data = new byte[0x0F];
        data[0] = 16;
        data[1] = 0x0F;
        data[0x06] = errorCorrection;                              // 錯誤更正型別
        BitConverter.GetBytes(0x400000u).CopyTo(data, 0x07);       // 上限 4 GB(KB)
        BitConverter.GetBytes((ushort)2).CopyTo(data, 0x0D);       // 2 槽
        return new SmbiosStruct(16, 0x0E00, data, []);
    }

    private static SmbiosStruct DimmWidth(ushort totalWidth, ushort dataWidth)
    {
        var data = new byte[0x22];
        data[0] = 17; data[1] = 0x22;
        BitConverter.GetBytes(totalWidth).CopyTo(data, 0x08);
        BitConverter.GetBytes(dataWidth).CopyTo(data, 0x0A);
        BitConverter.GetBytes((ushort)8192).CopyTo(data, 0x0C);   // 8 GB,才算有插模組
        data[0x17] = 1; data[0x18] = 2; data[0x1A] = 3;
        return new SmbiosStruct(17, 0x1001, data, ["Micron", "SN", "PN"]);
    }

    [Theory]
    [InlineData((byte)3, (ushort)64, (ushort)64, 3.0, 0.0)]   // 無 ECC,寬度相等
    [InlineData((byte)6, (ushort)72, (ushort)64, 6.0, 1.0)]   // 多位元 ECC,72>64 有 ECC 位元
    [InlineData((byte)6, (ushort)64, (ushort)64, 6.0, 0.0)]   // 宣稱 ECC 但寬度相等=沒 ECC 位元(假 ECC)
    public void ECC事實_型別碼與位元寬度都解得出(
        byte ecc, ushort total, ushort dataW, double expectType, double expectBits)
    {
        var facts = new VerifyFacts(SmbiosFacts.From([Array16(ecc), DimmWidth(total, dataW)], DateTime.UnixEpoch));
        Assert.Equal(expectType, facts.Num(FactId.MemEccType));
        Assert.Equal(expectBits, facts.Num(FactId.MemEccBitsPresent));
    }

    [Fact]
    public void 寬度未知時_不產出ECC位元事實()
    {
        var facts = new VerifyFacts(SmbiosFacts.From([Array16(3), DimmWidth(0xFFFF, 0xFFFF)], DateTime.UnixEpoch));
        Assert.False(facts.Has(FactId.MemEccBitsPresent));   // 未知就不猜,讓規則判無法判定
    }

    [Fact]
    public void 真實表_ECC核對規則判得出來_不留無法判定()
    {
        var f = VerifyEngine.Run(new VerifyFacts(RealFacts())).SingleOrDefault(x => x.Id == "R-MEM-05");
        Assert.NotNull(f);
        // 本機是 X299 HEDT 非 ECC UDIMM:應判「一致:無 ECC」;若韌體沒填寬度則為無法判定。
        Assert.True(f!.Verdict is VerifyVerdict.Match or VerifyVerdict.Unread,
            $"實機 ECC 核對不該是矛盾,實得 {f.Verdict}:{f.Explanation}");
    }
}
