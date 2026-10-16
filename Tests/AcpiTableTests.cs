using Xunit;

namespace XinSpect.Tests;

public sealed class AcpiTableTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 表頭解析_簽章長度OEMID與校驗和正確()
    {
        var table = BuildTable("BERT", revision: 1, oemId: "TESTID", oemTableId: "XINSPECT", totalLength: 48);
        Assert.True(AcpiTable.TryParseHeader(table, out var h));
        Assert.Equal("BERT", h.Signature);
        Assert.Equal(48u, h.Length);
        Assert.Equal((byte)1, h.Revision);
        Assert.Equal("TESTID", h.OemId);
        Assert.True(h.ChecksumValid);
    }

    [Fact]
    public void 表頭解析_校驗和錯誤時標無效但仍解出簽章()
    {
        var table = BuildTable("HEST", 1, "TESTID", "XINSPECT", 48);
        table[40] ^= 0xFF; // 破壞 body 一個位元組，校驗和即不為零
        Assert.True(AcpiTable.TryParseHeader(table, out var h));
        Assert.Equal("HEST", h.Signature);
        Assert.False(h.ChecksumValid);
    }

    [Fact]
    public void 表頭解析_長度不足36_拒絕()
        => Assert.False(AcpiTable.TryParseHeader(new byte[10], out _));

    [Fact]
    public void HEST_錯誤源數_自表頭後u32讀出()
    {
        var t = BuildTable("HEST", 1, "OEMAAA", "XINSPECT", 40);
        BitConverter.GetBytes(3u).CopyTo(t, 36);
        Assert.Equal(3u, AcpiTable.HestErrorSourceCount(t));
        Assert.Null(AcpiTable.HestErrorSourceCount(BuildTable("FACP", 1, "OEMAAA", "XINSPECT", 40)));
        Assert.Null(AcpiTable.HestErrorSourceCount(new byte[20]));
    }

    [Fact]
    public void BERT_開機錯誤區長度_自表頭後u32讀出()
    {
        var t = BuildTable("BERT", 1, "OEMAAA", "XINSPECT", 48);
        BitConverter.GetBytes(1024u).CopyTo(t, 36);
        Assert.Equal(1024u, AcpiTable.BertBootErrorRegionLength(t));
        Assert.Null(AcpiTable.BertBootErrorRegionLength(BuildTable("HEST", 1, "OEMAAA", "XINSPECT", 48)));
    }

    [Fact]
    public void 服務_列舉表_每張都產出事實且清單摘要含全部簽章()
    {
        var src = new FakeSource(true, null,
        [
            BuildTable("BERT", 1, "OEMAAA", "XINSPECT", 48),
            BuildTable("HEST", 2, "OEMAAA", "XINSPECT", 48),
        ]);

        var facts = AcpiService.Collect(src, At);

        var bert = facts.Single(f => f.Key == "acpi.table.bert");
        Assert.Equal(FactAvailability.Present, bert.Availability);
        Assert.Contains("rev 1", bert.Value);
        Assert.Contains("正確", bert.Value);
        var list = facts.Single(f => f.Key == "acpi.tables");
        Assert.Contains("BERT", list.Value);
        Assert.Contains("HEST", list.Value);
    }

    [Fact]
    public void 服務_無法列舉_清單標三態不可用()
    {
        var facts = AcpiService.Collect(new FakeSource(false, "ACPI 列舉失敗", []), At);
        var list = facts.Single(f => f.Key == "acpi.tables");
        Assert.NotEqual(FactAvailability.Present, list.Availability);
    }

    [Fact]
    public void 服務_同簽章多張表_鍵不重複()
    {
        var src = new FakeSource(true, null,
        [
            BuildTable("SSDT", 1, "OEMAAA", "SSDT0001", 48),
            BuildTable("SSDT", 1, "OEMAAA", "SSDT0002", 48),
        ]);

        var ssdt = AcpiService.Collect(src, At).Where(f => f.Key.StartsWith("acpi.table.ssdt")).ToList();

        Assert.Equal(2, ssdt.Count);
        Assert.Equal(2, ssdt.Select(f => f.Key).Distinct().Count());
    }

    [Fact]
    public void 服務_HEST與BERT_附錯誤源數與開機錯誤區且記錄標需ring0()
    {
        var hest = BuildTable("HEST", 1, "OEMAAA", "XINSPECT", 40);
        BitConverter.GetBytes(2u).CopyTo(hest, 36);
        var bert = BuildTable("BERT", 1, "OEMAAA", "XINSPECT", 48);
        BitConverter.GetBytes(512u).CopyTo(bert, 36);

        var facts = AcpiService.Collect(new FakeSource(true, null, [hest, bert]), At);

        Assert.Equal("2", facts.Single(f => f.Key == "acpi.hest.sources").Value);
        Assert.Equal("512", facts.Single(f => f.Key == "acpi.bert.region_length").Value);
        var record = facts.Single(f => f.Key == "acpi.bert.record");
        Assert.Equal(FactAvailability.InsufficientPrivilege, record.Availability);
    }

    // ── 極致計畫階段二 P2-001／P2-005／P2-018：校驗和具名、位元碼界線、解碼覆蓋申報 ──

    /// <summary>
    /// 校驗和不符要<b>具名</b>，不能只給張數——「有 3 張不對」跟「DSDT、SSDT、FACP 不對」的處置完全不同。
    /// 同簽章多張時用 #序號區分，序號與 acpi.table.* 的鍵序號同一套。
    /// </summary>
    [Fact]
    public void 校驗和_不符的表要逐張具名且同簽章加序號()
    {
        var good = BuildTable("BERT", 1, "OEMAAA", "XINSPECT", 48);
        var bad1 = BuildTable("SSDT", 1, "OEMAAA", "SSDT0001", 48);
        bad1[40] ^= 0xFF;
        var bad2 = BuildTable("SSDT", 1, "OEMAAA", "SSDT0002", 48);
        bad2[41] ^= 0xFF;

        var f = AcpiService.Collect(new FakeSource(true, null, [good, bad1, bad2]), At)
            .Single(x => x.Key == "ac.checksum.bad");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(2, f.NumericValue);
        Assert.Contains("SSDT#1", f.Value);
        Assert.Contains("SSDT#2", f.Value);
        Assert.DoesNotContain("BERT", f.Value); // 過的表不列入
    }

    /// <summary>全部表頭都對時，「0 張」是一個值（量出來的），不是讀不到——三態必須是 Present。</summary>
    [Fact]
    public void 校驗和_零張不符是量出來的零不是讀不到()
    {
        var facts = AcpiService.Collect(new FakeSource(true, null,
            [BuildTable("DSDT", 2, "OEMAAA", "XINSPECT", 48)]), At);
        var f = facts.Single(x => x.Key == "ac.checksum.bad");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0, f.NumericValue);
        Assert.Contains("0 張", f.Value);
    }

    /// <summary>
    /// 位元碼界線是<b>聲明</b>：它必須說出「不評估 AML」與「不反編譯 ASL」兩件事，
    /// 並帶著本機實際看到幾張 DSDT/SSDT——空泛的「本專案不評估 AML」誰都會寫，帶數字的才有對帳價值。
    /// </summary>
    [Fact]
    public void 位元碼界線_聲明明示不評估AML且帶本機表數()
    {
        var facts = AcpiService.Collect(new FakeSource(true, null,
        [
            BuildTable("DSDT", 2, "OEMAAA", "XINSPECT", 48),
            BuildTable("SSDT", 1, "OEMAAA", "SSDT0001", 48),
        ]), At);

        var f = facts.Single(x => x.Key == "ac.aml.boundary");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("_PSS", f.Value);
        Assert.Contains("_DSM", f.Value);
        Assert.Contains("不評估 AML", f.Value);
        Assert.Contains("反編譯", f.Value);
        Assert.Equal(2, f.NumericValue);
    }

    /// <summary>沒有 DSDT/SSDT 的機器，界線聲明仍在（它是聲明不是量測），只是數字為 0。</summary>
    [Fact]
    public void 位元碼界線_無DSDT時聲明仍在且數字為零()
    {
        var f = AcpiService.Collect(new FakeSource(true, null,
            [BuildTable("FACP", 1, "OEMAAA", "XINSPECT", 48)]), At)
            .Single(x => x.Key == "ac.aml.boundary");
        Assert.Equal(0, f.NumericValue);
    }

    /// <summary>
    /// 解碼覆蓋申報：沒有專用解碼器的表要<b>逐簽章列出</b>，有解碼器的不列入。
    /// 這條的重點是「未收錄」講得出來——把沒解的表混進已解的數量裡，就是拿覆蓋率騙人。
    /// </summary>
    [Fact]
    public void 解碼覆蓋_未收錄的表逐簽章列出而已解的不列入()
    {
        var facts = AcpiService.Collect(new FakeSource(true, null,
        [
            BuildTable("BGRT", 1, "OEMAAA", "XINSPECT", 48),   // 有解碼器
            BuildTable("WSMT", 1, "OEMAAA", "XINSPECT", 48),   // 無解碼器
            BuildTable("LPIT", 1, "OEMAAA", "XINSPECT", 48),   // 無解碼器
        ]), At);

        var f = facts.Single(x => x.Key == "ac.decode.unrecorded");
        Assert.Contains("WSMT", f.Value);
        Assert.Contains("LPIT", f.Value);
        Assert.DoesNotContain("BGRT", f.Value);
        Assert.Equal(2, f.NumericValue);
    }

    /// <summary>
    /// 每個簽章在本版有沒有解碼器，是<b>清單</b>說了算——所以清單與實際解碼分支必須對得上。
    /// 這條釘住兩邊：清單裡的簽章，服務層要真的解；清單外的，服務層不得偷偷解。
    /// </summary>
    [Fact]
    public void 解碼器清單_與服務層實際解碼分支一致()
    {
        Assert.True(AcpiDeep.HasDedicatedDecoder("BGRT"));
        Assert.True(AcpiDeep.HasDedicatedDecoder("bgrt"));      // 大小寫不敏感
        Assert.True(AcpiDeep.HasDedicatedDecoder(" NFIT "));    // 尾端空白（表頭解析會 TrimEnd）
        Assert.False(AcpiDeep.HasDedicatedDecoder("WSMT"));
        Assert.False(AcpiDeep.HasDedicatedDecoder(""));
        Assert.Equal(AcpiDeep.DecodedSignatures.Count,
            AcpiDeep.DecodedSignatures.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private sealed class FakeSource(bool available, string? reason, byte[][] tables) : IAcpiTableSource
    {
        public bool Available => available;
        public string? UnavailableReason => reason;
        public IReadOnlyList<byte[]> ReadAll() => tables;
    }

    // 組一個指定簽章/長度、且校驗和正確（整表位元組和 mod 256 = 0）的合成 ACPI 表。
    private static byte[] BuildTable(string sig, byte revision, string oemId, string oemTableId, int totalLength)
    {
        var t = new byte[totalLength];
        System.Text.Encoding.ASCII.GetBytes(sig).CopyTo(t, 0);
        BitConverter.GetBytes((uint)totalLength).CopyTo(t, 4);
        t[8] = revision;
        PadAscii(oemId, 6).CopyTo(t, 10);
        PadAscii(oemTableId, 8).CopyTo(t, 16);
        int sum = 0;
        for (int i = 0; i < t.Length; i++) if (i != 9) sum += t[i];
        t[9] = (byte)((256 - (sum % 256)) % 256); // 補校驗和使整表和為 0
        return t;
    }

    private static byte[] PadAscii(string s, int len)
    {
        var b = new byte[len];
        System.Text.Encoding.ASCII.GetBytes(s).AsSpan(0, System.Math.Min(s.Length, len)).CopyTo(b);
        return b;
    }
}
