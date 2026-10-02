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
