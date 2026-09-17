using Xunit;

namespace XinSpect.Tests;

public class BlueSquadronSmokeTests
{
    [Fact]
    public void PageRegistry包含bluesquadron頁()
    {
        var page = PageRegistry.Pages.FirstOrDefault(p => p.Key == "bluesquadron");
        Assert.NotNull(page);
        Assert.Equal("防護", page.Title);
        Assert.Equal("安全", page.Group);
        Assert.True(page.Advanced, "藍色中隊應為進階頁面");
    }

    [Fact]
    public void PageRegistry的bluesquadron圖示可解析()
    {
        var page = PageRegistry.Pages.First(p => p.Key == "bluesquadron");
        var icon = page.Icon;  // 觸發 Geometry.Parse
        Assert.NotNull(icon);
    }

    [Fact]
    public void SecurityFact_Record型別可建構()
    {
        var finding = new SecurityFinding(
            "test.id", "測試分類", "測試標題", "測試詳情",
            SecuritySeverity.Warning, "建議做法");
        Assert.Equal("test.id", finding.Id);
        Assert.Equal(SecuritySeverity.Warning, finding.Severity);
    }

    [Fact]
    public void DefenseLineStatus_預設六條防線()
    {
        var module = new BlueSquadronModule();
        Assert.Equal(6, module.DefenseLines.Count);
        Assert.Contains(module.DefenseLines, l => l.Id == "dma");
        Assert.Contains(module.DefenseLines, l => l.Id == "firmware");
        Assert.Contains(module.DefenseLines, l => l.Id == "cpu");
        Assert.Contains(module.DefenseLines, l => l.Id == "storage");
        Assert.Contains(module.DefenseLines, l => l.Id == "drivers");
        Assert.Contains(module.DefenseLines, l => l.Id == "surface");
    }

    [Fact]
    public void SecurityPosture的Verdict不為null()
    {
        var posture = SecurityScoreEngine.Evaluate(
            new SecurityScoreEngine.DmaFacts(true, true, true, true, true, true, true, 3, true),
            new SecurityScoreEngine.FirmwareFacts(true, false, false, true, false, true, "16.0",
                true, "2.0", true, null),
            new SecurityScoreEngine.CpuFacts(true, true, true, true, true, true, true, true, true, true),
            new SecurityScoreEngine.StorageFacts(true, true, true, true, "XTS-AES-256"),
            new SecurityScoreEngine.DriverFacts(100, 0, 0, true, true, true, true, true, true),
            new SecurityScoreEngine.SurfaceFacts(true, true, true, true, true, true, true, true, true, true, true, true, true, true, 5));
        Assert.NotNull(posture.Verdict);
        Assert.NotEmpty(posture.Verdict);
        Assert.NotNull(posture.AssessedAt);
    }

    [Fact]
    public void UpdateDefenseLines更新防線狀態()
    {
        var module = new BlueSquadronModule();
        var posture = SecurityScoreEngine.Evaluate(
            new SecurityScoreEngine.DmaFacts(false, false, false, false, false, false, false, 0, false),
            new SecurityScoreEngine.FirmwareFacts(true, false, false, true, false, true, null,
                true, "2.0", true, null),
            new SecurityScoreEngine.CpuFacts(true, true, true, true, true, true, true, true, true, true),
            new SecurityScoreEngine.StorageFacts(true, true, true, true, "XTS-AES-256"),
            new SecurityScoreEngine.DriverFacts(100, 0, 0, true, true, true, true, true, true),
            new SecurityScoreEngine.SurfaceFacts(true, true, true, true, true, true, true, true, true, true, true, true, true, true, 5));

        module.UpdateDefenseLines(posture);

        var dmaLine = module.DefenseLines.First(l => l.Id == "dma");
        Assert.True(dmaLine.Score < 50, "DMA 全關應該低分");

        var cpuLine = module.DefenseLines.First(l => l.Id == "cpu");
        Assert.True(cpuLine.Score >= 90, "CPU 全開應該高分");
    }
}
