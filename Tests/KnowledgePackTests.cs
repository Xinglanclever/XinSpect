using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 知識包（EG-002／P0-09）契約：序列號由內容推導（同輸入同輸出）、缺 snapshotDate 整包拒收、
/// serial 不符拒收、缺檔＝內建表如實聲明；換包動作要進審計鏈且鏈可驗。
/// </summary>
public class KnowledgePackTests
{
    [Fact]
    public void DirectModelRejectsEscapedUtf8BytesBeforeHashingLargeBody()
    {
        // 2M UTF-16 characters pass the character guard, but default JSON escaping exceeds 8 MiB.
        var entries = Enumerable.Range(0, 512).ToDictionary(i => i.ToString(), _ => new string('中', 4096));
        var table = Table(entries: entries);
        var pack = new KnowledgePack(1, "2026-10-25", [table], "sha256:uncomputed");
        string? reason = KnowledgePackService.ValidateForConsumption(pack);
        Assert.Contains("JSON UTF-8", reason);
        Assert.DoesNotContain("serialNumber", reason);
    }

    [Fact]
    public void DirectModelPreCancellationPropagatesBeforeValidation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pack = KnowledgePackService.Build(1, "2026-10-25", [Table()]);
        Assert.Throws<OperationCanceledException>(() =>
            KnowledgePackService.ValidateForConsumption(pack, cancellation.Token));
    }

    [Fact]
    public void DigestAndPrivateHelperRejectUnverifiedDirectModel()
    {
        var table = Table(DeviceComplianceFactsService.TableName);
        table = table with { SourceManifest = new KnowledgeSourceManifest("fixture", "1", null,
            "fixture", "1", "sha256:wrong") };
        var pack = KnowledgePackService.Build(1, "2026-10-25", [table]);
        var fact = KnowledgePackReport.ToDigestFact(At, new KnowledgePackLoad(pack, null, pack.SerialNumber));
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("tableHash", fact.UnavailableReason);
        var description = KnowledgePackService.DescribePrivateProvenance(table);
        Assert.Contains("未核對", description);
        Assert.DoesNotContain("已核對", description);
    }

    [Fact]
    public void ManifestRoundTripBindsTheExactTableAndReportsAuthorClaims()
    {
        // Explicitly synthetic source: this tests transport, not an upstream attribution.
        var table = Table();
        table = table with { SourceManifest = new KnowledgeSourceManifest("fixture document", "fixture revision",
            null, "fixture extractor", "1", KnowledgePackService.ComputeTableHash(table)) };
        var pack = KnowledgePackService.Build(1, "2026-10-25", [table]);
        var load = LoadTemporary(pack);
        Assert.NotNull(load.Pack);
        Assert.Equal(table.SourceManifest, load.Pack!.Tables[0].SourceManifest);
        var text = KnowledgePackReport.ToDigestFact(At, load).Value;
        Assert.Contains("fixture document", text);
        Assert.Contains("未認證", text);
        Assert.Contains("非防偽", text);
    }

    [Fact]
    public void RecomputedPackDigestCannotHideManifestTableMismatch()
    {
        var table = Table();
        var manifest = new KnowledgeSourceManifest("fixture", "1", null, "fixture", "1",
            KnowledgePackService.ComputeTableHash(table));
        var changed = Table(entries: new Dictionary<string, string> { ["00:11:22"] = "Changed" })
            with { SourceManifest = manifest };
        var load = LoadTemporary(KnowledgePackService.Build(1, "2026-10-25", [changed]));
        Assert.Null(load.Pack);
        Assert.Contains("tableHash", load.Reason);
    }

    [Fact]
    public void ManifestMetadataChangesSerialAndMissingExtractorRemainsUnknown()
    {
        var table = Table();
        var manifest = new KnowledgeSourceManifest("fixture", "1", null, null, null,
            KnowledgePackService.ComputeTableHash(table));
        var claimed = table with { SourceManifest = manifest };
        Assert.NotEqual(KnowledgePackService.ComputeSerial(1, "2026-10-25", [table]),
            KnowledgePackService.ComputeSerial(1, "2026-10-25", [claimed]));
        Assert.NotEqual(KnowledgePackService.ComputeSerial(1, "2026-10-25", [claimed]),
            KnowledgePackService.ComputeSerial(1, "2026-10-25", [claimed with
                { SourceManifest = manifest with { Revision = "2" } }]));
        var load = LoadTemporary(KnowledgePackService.Build(1, "2026-10-25", [claimed]));
        Assert.NotNull(load.Pack);
        Assert.Contains("來源未知", KnowledgePackService.DescribeProvenance(load.Pack!.Tables[0]));
    }

    [Fact]
    public void TableHashIsOrderIndependentAndLegacySerialPayloadIsUnchanged()
    {
        var first = Table(entries: new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });
        var reordered = Table(entries: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        Assert.Equal(KnowledgePackService.ComputeTableHash(first), KnowledgePackService.ComputeTableHash(reordered));
        const string legacyJson = "{\"packVersion\":1,\"generatedAt\":\"2026-10-25\",\"tables\":[{\"table\":\"oui\",\"snapshotDate\":\"2026-10-11\",\"entries\":{\"a\":\"1\",\"b\":\"2\"}}]}";
        string expected = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(legacyJson))).ToLowerInvariant();
        Assert.Equal(expected, KnowledgePackService.ComputeSerial(1, "2026-10-25", [first]));
    }

    [Fact]
    public void PreCanceledLoadPropagatesCancellationWithoutReadingFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => KnowledgePackService.Load("missing.json", cancellation.Token));
    }

    [Theory]
    [InlineData("2026-02-30", "2026-10-25", "snapshotDate")]
    [InlineData("2026-10-26", "2026-10-25", "snapshotDate")]
    [InlineData("2026-10-11", "tomorrow", "generatedAt")]
    [InlineData("2026-10-11", "2026-10-25T01:00:00", "generatedAt")]
    public void InvalidDatesAreRejectedEvenWithMatchingDigest(string snapshot, string generated, string reason)
    {
        var pack = KnowledgePackService.Build(1, generated, [Table(date: snapshot)]);
        var load = LoadTemporary(pack);
        Assert.Null(load.Pack);
        Assert.Contains(reason, load.Reason);
        Assert.Equal(pack.SerialNumber, load.DeclaredSerial);
    }

    [Fact]
    public void FileSizeBudgetIsCheckedBeforeJsonParsing()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var file = File.OpenWrite(path)) file.SetLength(KnowledgePackService.MaximumFileBytes + 1L);
            var load = KnowledgePackService.Load(path);
            Assert.Null(load.Pack);
            Assert.Contains("大小預算", load.Reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TableAndAggregateEntryBudgetsRejectOversizedPacks()
    {
        var tables = Enumerable.Range(0, KnowledgePackService.MaximumTables + 1)
            .Select(i => Table(name: "table" + i)).ToArray();
        Assert.Contains("表數預算", LoadTemporary(KnowledgePackService.Build(1, "2026-10-25", tables)).Reason);
        var entries = Enumerable.Range(0, KnowledgePackService.MaximumEntries / 2 + 1)
            .ToDictionary(i => i.ToString(), _ => "v");
        var load = LoadTemporary(KnowledgePackService.Build(1, "2026-10-25",
            [Table("one", entries: entries), Table("two", entries: entries)]));
        Assert.Null(load.Pack);
        Assert.Contains("條目預算", load.Reason);
    }

    [Fact]
    public void ValidLegacyPackReportsUnknownProvenanceWithoutChangingDigest()
    {
        var pack = KnowledgePackService.Build(1, "2026-10-25", [Table()]);
        var load = LoadTemporary(pack);
        Assert.NotNull(load.Pack);
        Assert.Equal(pack.SerialNumber, load.Pack!.SerialNumber);
        Assert.Contains("來源未知", KnowledgePackReport.ToDigestFact(At, load).Value);
        Assert.Contains("不構成來源鏈", KnowledgePackReport.NonRepudiationNote);
    }

    private static KnowledgePackLoad LoadTemporary(KnowledgePack pack)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(pack));
            return KnowledgePackService.Load(path);
        }
        finally { File.Delete(path); }
    }

    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    private static KnowledgeTable Table(string name = "oui", string date = "2026-10-11",
        IReadOnlyDictionary<string, string>? entries = null) =>
        new(name, date, entries ?? new Dictionary<string, string> { ["00:11:22"] = "TestVendor" });

    [Fact]
    public void 序列號_同輸入同輸出_內容變了就變()
    {
        string a = KnowledgePackService.ComputeSerial(3, "2026-10-11", [Table()]);
        string b = KnowledgePackService.ComputeSerial(3, "2026-10-11", [Table()]);
        string c = KnowledgePackService.ComputeSerial(3, "2026-10-11", [Table("oui", "2026-10-01")]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("sha256:", a);
    }

    [Fact]
    public void 往返_建包寫檔再載回_完整性成立()
    {
        string path = Path.Combine(Path.GetTempPath(), "kpack-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var pack = KnowledgePackService.Build(3, "2026-10-11T00:00:00Z", [Table()]);
            File.WriteAllText(path, JsonSerializer.Serialize(pack));
            var (loaded, reason, declared) = KnowledgePackService.Load(path);
            Assert.Null(reason);
            Assert.NotNull(loaded);
            Assert.Equal(pack.SerialNumber, declared);
            Assert.Equal("oui", loaded!.Tables[0].Table);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 缺快照日期_整包拒收()
    {
        string path = Path.Combine(Path.GetTempPath(), "kpack-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var bad = new KnowledgePack(1, "2026-10-25", [new KnowledgeTable("oui", "", new Dictionary<string, string>())], "sha256:x");
            File.WriteAllText(path, JsonSerializer.Serialize(bad));
            var (loaded, reason, _) = KnowledgePackService.Load(path);
            Assert.Null(loaded);
            Assert.Contains("snapshotDate", reason);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 序列號不符_拒收並帶兩個值()
    {
        string path = Path.Combine(Path.GetTempPath(), "kpack-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var pack = KnowledgePackService.Build(1, "2026-10-25", [Table()]) with { SerialNumber = "sha256:deadbeef" };
            File.WriteAllText(path, JsonSerializer.Serialize(pack));
            var (loaded, reason, _) = KnowledgePackService.Load(path);
            Assert.Null(loaded);
            Assert.Contains("不符", reason);
            Assert.Contains("deadbeef", reason);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 缺檔_內建表如實聲明不是空清單()
    {
        var facts = KnowledgePackService.Collect(At, Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".json"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("內建表", f.Value);
        Assert.Contains("沒有外掛知識包", f.UnavailableReason);
    }

    [Fact]
    public void 合格包載入_狀態與表清單成列()
    {
        string path = Path.Combine(Path.GetTempPath(), "kpack-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var pack = KnowledgePackService.Build(7, "2026-10-11T00:00:00Z",
                [Table("oui"), Table("pci", "2026-10-01", new Dictionary<string, string> { ["10DE"] = "NVIDIA" })]);
            File.WriteAllText(path, JsonSerializer.Serialize(pack));
            var facts = KnowledgePackService.Collect(At, path);
            var status = facts.Single(f => f.Key == "knowledge.pack.status");
            Assert.Equal(FactAvailability.Present, status.Availability);
            Assert.Contains("第 7 版包已載入", status.Value);
            var tables = facts.Single(f => f.Key == "knowledge.pack.tables");
            Assert.Contains("oui（1 條，快照 2026-10-11）", tables.Value);
            Assert.Contains("pci（1 條，快照 2026-10-01）", tables.Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 換包記錄_進審計鏈且可驗_壞包不記錄()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kpack-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string packPath = Path.Combine(dir, "knowledge-pack.json");
        string auditPath = Path.Combine(dir, "audit.json");
        try
        {
            var pack = KnowledgePackService.Build(1, "2026-10-25", [Table()]);
            File.WriteAllText(packPath, JsonSerializer.Serialize(pack));
            Assert.True(KnowledgePackService.RecordSwap(packPath, auditPath, out string reason));
            Assert.Contains("審計鏈", reason);
            var verdict = AuditVerifier.Verify(AuditLogService.Load(auditPath));
            Assert.True(verdict.Valid, "換包後審計鏈必須可驗");

            File.WriteAllText(packPath, "{\"serialNumber\":\"sha256:x\"}");
            Assert.False(KnowledgePackService.RecordSwap(packPath, auditPath, out string badReason));
            Assert.Contains("不記錄", badReason);
            // 壞包不記錄——鏈還是原來那條
            Assert.True(AuditVerifier.Verify(AuditLogService.Load(auditPath)).Valid);
        }
        finally { Directory.Delete(dir, true); }
    }
}
