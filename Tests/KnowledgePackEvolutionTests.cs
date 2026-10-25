using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 知識包版本演進與撤回的機器守門（U10-EN-0012／<c>kp.evolution.proc</c>）。
/// 成文文件＝docs/ULTIMATE-10000/KNOWLEDGE-PACK-PROCEDURE-2026-10-25.md；
/// 本閘與文件一起搬才不會走散（文件形狀測試在最後）。
/// </summary>
public class KnowledgePackEvolutionTests
{
    /// <summary>追蹤鍵（U10-EN-0012，供掃描）。</summary>
    public const string Key = "kp.evolution.proc";

    private const string WithdrawAction = "knowledge-pack-withdraw";

    private static KnowledgeTable Table(string name = "oui", string date = "2026-10-11") =>
        new(name, date, new Dictionary<string, string> { ["00:11:22"] = "TestVendor" });

    // ── 版本演進 ─────────────────────────────────────────────────────────

    [Fact]
    public void 演進_新包帶前版完整序列號()
    {
        var v1 = KnowledgePackService.Build(1, "2026-10-01T00:00:00Z", [Table()]);
        var v2 = KnowledgePackService.Evolve(v1, 2, "2026-10-25T00:00:00Z",
            [Table("oui", "2026-10-25")]);

        Assert.Equal(v1.SerialNumber, v2.PreviousSerial);          // 前版 digest 一字不截
        Assert.NotEqual(v1.SerialNumber, v2.SerialNumber);         // 內容變了序列號就跟著變
    }

    [Fact]
    public void 前版指標納入序列號_同一組內容有無指標序列號不同()
    {
        string withPointer = KnowledgePackService.ComputeSerial(2, "t", [Table()], "sha256:prev");
        string without = KnowledgePackService.ComputeSerial(2, "t", [Table()]);
        Assert.NotEqual(without, withPointer);
        // 同指標同輸出（與既有「同輸入同輸出」契約同款）
        Assert.Equal(withPointer, KnowledgePackService.ComputeSerial(2, "t", [Table()], "sha256:prev"));
    }

    [Fact]
    public void 演進包_寫檔載回_前版指標與完整性都成立()
    {
        string path = Path.Combine(Path.GetTempPath(), "kpevo-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var v1 = KnowledgePackService.Build(1, "t0", [Table()]);
            var v2 = KnowledgePackService.Evolve(v1, 2, "t1", [Table("oui", "2026-10-25")]);
            File.WriteAllText(path, JsonSerializer.Serialize(v2));

            var (loaded, reason, declared) = KnowledgePackService.Load(path);
            Assert.Null(reason);
            Assert.NotNull(loaded);
            Assert.Equal(v2.SerialNumber, declared);
            Assert.Equal(v1.SerialNumber, loaded!.PreviousSerial);   // 演進鏈載得回來
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 篡改前版指標一個字元_整包拒收()
    {
        string path = Path.Combine(Path.GetTempPath(), "kpevo-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var v1 = KnowledgePackService.Build(1, "t0", [Table()]);
            var v2 = KnowledgePackService.Evolve(v1, 2, "t1", [Table()]);
            var forged = v2 with { PreviousSerial = v1.SerialNumber[..^1] + (v1.SerialNumber[^1] == '0' ? "1" : "0") };
            Assert.NotEqual(v2.PreviousSerial, forged.PreviousSerial);
            File.WriteAllText(path, JsonSerializer.Serialize(forged));

            var (loaded, reason, _) = KnowledgePackService.Load(path);
            Assert.Null(loaded);
            Assert.Contains("不符", reason);   // 演進指標不是說明欄，是完整性的一部分
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 舊格式包_沒有前版欄位_照樣載入且序列號與成文前一致()
    {
        // 序列號只算 packVersion／generatedAt／tables——不帶前版指標的 payload 形狀不得變，
        // 否則既有散佈出去的包會在同一支程式下變成「序列號不符」。
        string legacySerial = KnowledgePackService.ComputeSerial(3, "2026-10-11", [Table()]);
        Assert.StartsWith("sha256:", legacySerial);

        string path = Path.Combine(Path.GetTempPath(), "kpevo-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var legacy = new KnowledgePack(3, "2026-10-11", [Table()], legacySerial);
            File.WriteAllText(path, JsonSerializer.Serialize(legacy));
            var (loaded, reason, _) = KnowledgePackService.Load(path);
            Assert.Null(reason);
            Assert.NotNull(loaded);
            Assert.Null(loaded!.PreviousSerial);
            Assert.Equal(legacySerial, loaded.SerialNumber);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── 撤回 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 撤回聲明_進審計鏈且可驗_帶完整序列號與理由()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kpevo-withdraw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string auditPath = Path.Combine(dir, "audit.json");
        try
        {
            var pack = KnowledgePackService.Build(2, "t", [Table()]);
            Assert.True(KnowledgePackService.RecordWithdrawal(pack.SerialNumber,
                "徵狀：測試會過但我不確定為什麼會過；處置：退回未開工", auditPath, out string withdrawal));
            Assert.Contains("審計鏈", withdrawal);

            var entries = AuditLogService.Load(auditPath);
            var entry = Assert.Single(entries, e => e.Action == WithdrawAction);
            Assert.Equal(pack.SerialNumber, entry.Scope);              // 機器形狀：Scope＝完整 serialNumber
            Assert.Contains("徵狀", entry.ResultSummary);               // 人讀欄目（先例形狀）
            Assert.True(AuditVerifier.Verify(entries).Valid, "撤回後審計鏈必須可驗");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 撤回聲明缺序列號或理由_不成聲明_拒絕記錄()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kpevo-withdraw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string auditPath = Path.Combine(dir, "audit.json");
        try
        {
            Assert.False(KnowledgePackService.RecordWithdrawal("", "理由", auditPath, out string noSerial));
            Assert.Contains("不成聲明", noSerial);
            Assert.False(KnowledgePackService.RecordWithdrawal("sha256:x", " ", auditPath, out string noReason));
            Assert.Contains("不成聲明", noReason);
            Assert.Empty(AuditLogService.Load(auditPath));   // 什麼都沒記——鏈不被空聲明污染
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 撤回後移除包檔_載入如實回內建表_不假裝還有舊版()
    {
        string path = Path.Combine(Path.GetTempPath(), "kpevo-" + Guid.NewGuid().ToString("N") + ".json");
        var pack = KnowledgePackService.Build(2, "t", [Table()]);
        File.WriteAllText(path, JsonSerializer.Serialize(pack));
        File.Delete(path);   // 撤回聲明記完之後的標準動作：移除包檔

        var (loaded, reason, _) = KnowledgePackService.Load(path);
        Assert.Null(loaded);
        Assert.Contains("沒有外掛知識包", reason);
        Assert.Contains("內建表繼續服役", reason);
    }

    // ── 文件形狀（與守門一起搬）───────────────────────────────────────────

    [Fact]
    public void 成文文件存在且含必要欄目()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "docs", "ULTIMATE-10000",
            "KNOWLEDGE-PACK-PROCEDURE-2026-10-25.md");
        Assert.True(File.Exists(path), $"成文文件不在：{path}");

        string text = File.ReadAllText(path);
        foreach (string section in new[] { "版本演進", "撤回聲明的形狀", "撤回後載入行為", "先例引用", "WITHDRAWN-P2-052-FIT" })
            Assert.True(text.Contains(section), $"成文文件缺欄目「{section}」");
    }
}
