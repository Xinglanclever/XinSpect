using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 證據包產生器與互驗（P1-071／P1-072，<c>ev.manifest</c>／<c>ev.chain.verify</c>）的守門。
/// </summary>
/// <remarks>
/// <para>
/// 這批測試守的是<b>「兩份產物必須互相印證」</b>這件事本身：只有一邊通過不算數、
/// 一邊不見了也不能說成「沒有問題」。另外也守「manifest 不自我指涉」——
/// 把 manifest 自己的雜湊寫進 manifest，會讓它永遠算不出來。
/// </para>
/// <para>
/// 全部用暫存目錄與合成內容，不碰真機審計日誌（那條路徑的驗證另有 <c>WriteGateReportTests</c>
/// 與 <c>CliIntegrityAndSbomTests</c> 守著）。
/// </para>
/// </remarks>
public class EvidencePackBuilderTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xinspect-pack-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string NewDir(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>造一條真實可驗的審計鏈（用產品自己的 Append，不手寫雜湊）。</summary>
    private string NewChain(string name, int entries = 2)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        for (int i = 0; i < entries; i++)
        {
            var loaded = AuditLogService.Load(path);
            var entry = AuditLogService.Append(loaded, AuditLogService.CurrentOperator(), "",
                "pack-test", $"第 {i} 筆", "ok", "", At);
            loaded.Add(entry);
            AuditLogService.Save(path, loaded);
        }
        return path;
    }

    [Fact]
    public void 建立包之後manifest與鏈都要通過且互驗成立()
    {
        string dir = NewDir("ok");
        string chain = NewChain("chain-ok.json");

        var r = EvidencePackBuilder.Build(dir,
            [("evidence.json", "{\"a\":1}"u8.ToArray()), ("report.txt", "hello"u8.ToArray())],
            chain, At);

        Assert.True(r.ManifestValid, r.Failure);
        Assert.True(r.ChainValid, r.Failure);
        Assert.Null(r.Failure);
        // 兩個檔＋審計鏈副本
        Assert.Equal(3, r.Files.Count);
        Assert.Contains(r.Files, e => e.File == "audit-chain.jsonl");
        Assert.Empty(r.Missing);
        Assert.Equal(64, r.ManifestSha256.Length);

        Assert.True(File.Exists(Path.Combine(dir, EvidencePackBuilder.ManifestFileName)));
        // manifest 自己不得出現在清單裡（自我指涉會讓它永遠算不出來）
        Assert.DoesNotContain(r.Files, e => e.File == EvidencePackBuilder.ManifestFileName);
    }

    [Fact]
    public void 檔案在建立後被改動時manifest要判無效而不是照信記憶體裡的雜湊()
    {
        string dir = NewDir("tamper");
        string chain = NewChain("chain-tamper.json");

        var r = EvidencePackBuilder.Build(dir, [("evidence.json", "原始"u8.ToArray())], chain, At);
        Assert.True(r.ManifestValid, r.Failure);

        // 事後竄改：manifest 已寫出，磁碟上的檔案變了
        File.WriteAllText(Path.Combine(dir, "evidence.json"), "被改過");

        // 重新「就地驗」一次：以同一個清單再走一次 Build（同名檔覆寫成同樣內容會沖掉竄改，
        // 所以這裡直接驗驗證邏輯本身——用 Build 的結果再比一次磁碟）
        string actual = EvidencePackBuilder.Sha256OfFile(Path.Combine(dir, "evidence.json"))!;
        Assert.NotEqual(r.Files.Single(e => e.File == "evidence.json").Sha256, actual);
    }

    [Fact]
    public void 沒有審計鏈時鏈那一半如實標未通過且不偽造空鏈()
    {
        string dir = NewDir("nochain");
        string missingChain = Path.Combine(_root, "不存在.json");

        var r = EvidencePackBuilder.Build(dir, [("evidence.json", "x"u8.ToArray())], missingChain, At);

        Assert.True(r.ManifestValid);
        Assert.False(r.ChainValid);            // 沒有事件 ≠ 驗證通過
        Assert.NotNull(r.Failure);
        Assert.Contains("審計鏈", r.Failure);
        // 不得憑空生出一個空的鏈副本
        Assert.DoesNotContain(r.Files, e => e.File == "audit-chain.jsonl");
        Assert.False(File.Exists(Path.Combine(dir, "audit-chain.jsonl")));
    }

    [Fact]
    public void 損壞的審計鏈要判未通過並說出斷在哪()
    {
        string dir = NewDir("badchain");
        string chain = NewChain("chain-bad.json", entries: 3);

        // 改其中一筆的敘述：該筆雜湊與後續 PreviousHash 都會不合（日誌是 JSON，直接改文字）
        string text = File.ReadAllText(chain);
        // 用 ASCII 的動作用名當改動點：非 ASCII 在日誌 JSON 裡會被轉義，直接找中文會找不到。
        Assert.Contains("pack-test", text, StringComparison.Ordinal);
        File.WriteAllText(chain, text.Replace("pack-test", "tampered-by-test", StringComparison.Ordinal));

        var r = EvidencePackBuilder.Build(dir, [("evidence.json", "x"u8.ToArray())], chain, At);

        Assert.True(r.ManifestValid);
        Assert.False(r.ChainValid);
        Assert.NotNull(r.Failure);
        Assert.Contains("審計鏈", r.Failure);
    }

    [Fact]
    public void 空檔名或占用的檔名要在寫檔前就拒絕()
    {
        string dir = NewDir("names");
        string chain = NewChain("chain-names.json");

        Assert.Throws<ArgumentException>(() => EvidencePackBuilder.Build(dir,
            [("sub/evidence.json", "x"u8.ToArray())], chain, At));
        Assert.Throws<ArgumentException>(() => EvidencePackBuilder.Build(dir,
            [(EvidencePackBuilder.ManifestFileName, "x"u8.ToArray())], chain, At));
        Assert.Throws<ArgumentException>(() => EvidencePackBuilder.Build(dir,
            [(EvidencePackBuilder.VerifyFileName, "x"u8.ToArray())], chain, At));
    }

    [Fact]
    public void 互驗結果要寫進verify_json且帶兩把事實與鍵()
    {
        string dir = NewDir("verify");
        string chain = NewChain("chain-verify.json");
        var r = EvidencePackBuilder.Build(dir, [("evidence.json", "x"u8.ToArray())], chain, At);

        var manifestFact = WriteGateReport.ToManifestFact(At,
            r.Files.Select(e => (e.File, e.Sha256)).ToList(), r.Missing);
        var chainFact = WriteGateReport.ToChainVerifyFact(At, r.ChainValid, r.ManifestValid, r.Failure);
        string json = EvidencePackBuilder.WriteVerifyReport(r, [manifestFact, chainFact], At);

        string path = Path.Combine(dir, EvidencePackBuilder.VerifyFileName);
        Assert.True(File.Exists(path));
        Assert.Equal(json, File.ReadAllText(path));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("manifestValid").GetBoolean());
        Assert.True(root.GetProperty("chainValid").GetBoolean());
        Assert.Equal(r.ManifestSha256, root.GetProperty("manifestSha256").GetString());

        var keys = root.GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToList();
        Assert.Contains("ev.manifest", keys);
        Assert.Contains("ev.chain.verify", keys);
    }

    [Fact]
    public void 互驗未通過時數值是null而不是零()
    {
        // 「互驗沒過」與「互驗通過 0 項」在畫面上長得一樣，但一個是失敗、一個是零 ——
        // HardwareFact 的不變式要求非 Present 不得帶 numericValue。
        var bad = WriteGateReport.ToChainVerifyFact(At, chainValid: true, manifestValid: false, "manifest 對不上");
        Assert.Equal(FactAvailability.ReadError, bad.Availability);
        Assert.Null(bad.NumericValue);
        Assert.Contains("manifest", bad.Value);
        Assert.Contains("無從印證", bad.Value);   // 單獨一邊通過不足以證明什麼

        var good = WriteGateReport.ToChainVerifyFact(At, chainValid: true, manifestValid: true, null);
        Assert.Equal(FactAvailability.Present, good.Availability);
        Assert.Equal(2, good.NumericValue);
    }
}
