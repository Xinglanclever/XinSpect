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
            var bad = new KnowledgePack(1, "t", [new KnowledgeTable("oui", "", new Dictionary<string, string>())], "sha256:x");
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
            var pack = KnowledgePackService.Build(1, "t", [Table()]) with { SerialNumber = "sha256:deadbeef" };
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
            var pack = KnowledgePackService.Build(1, "t", [Table()]);
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
