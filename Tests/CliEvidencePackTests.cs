using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// <c>--evidence-pack</c> 命令層的守門：包要真的被寫出來、互驗結果要真的被算出並落檔。
/// </summary>
/// <remarks>
/// 這裡不驗「審計鏈一定有效」——那取決於這台機器上真實的審計日誌（可能還沒建立、
/// 也可能被前一次測試動過）。命令的責任是<b>如實算出並寫出來</b>，
/// 所以斷言看的是「有沒有算出結論、結論有沒有落檔」，不是「結論一定是通過」。
/// </remarks>
public class CliEvidencePackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xinspect-cli-pack-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static HardwareFact SampleFact() => new("spi.bios_hash", "韌體", "BIOS 區雜湊",
        "abc123", "", "測試來源", FactTrustLevel.Measured, false, DateTimeOffset.UtcNow, null);

    [Fact]
    public void 建立證據包要把事實清單manifest與互驗結果都寫出來()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exit = CliService.Run(["--evidence-pack", _dir], () => [SampleFact()], stdout, stderr);

        // 0＝互驗通過、2＝包已建立但互驗未通過（本機審計鏈狀態取決於環境）——兩者都是「有結論」
        Assert.True(exit is 0 or 2, $"退出碼應為 0 或 2，實際 {exit}；stderr：{stderr}");
        Assert.True(File.Exists(Path.Combine(_dir, "evidence.json")));
        Assert.True(File.Exists(Path.Combine(_dir, EvidencePackBuilder.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(_dir, EvidencePackBuilder.VerifyFileName)));

        using var verify = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, EvidencePackBuilder.VerifyFileName)));
        var root = verify.RootElement;
        // 兩半都必須有明確結論（true/false），不得缺席
        Assert.True(root.TryGetProperty("manifestValid", out _));
        Assert.True(root.TryGetProperty("chainValid", out _));
        Assert.True(root.TryGetProperty("facts", out var facts));
        var keys = facts.EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToList();
        Assert.Contains("ev.manifest", keys);
        Assert.Contains("ev.chain.verify", keys);

        // 事實內容真的被封存進包裡（不是只寫了一個空包）
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "evidence.json")));
        Assert.Equal(1, evidence.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void 沒給目錄參數時要說清楚用法並回致命碼()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exit = CliService.Run(["--evidence-pack"], () => [], stdout, stderr);

        Assert.Equal(CliService.ExitError, exit);
        Assert.Contains("--evidence-pack", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 證據包模式要出現在headless目錄裡而說明與實作同源()
    {
        string json = CliService.GenerateHeadlessModesJson();
        Assert.Contains(CliService.EvidencePackArg, json, StringComparison.Ordinal);
        Assert.Contains("ev.manifest", json, StringComparison.Ordinal);
    }
}
