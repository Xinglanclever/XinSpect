using Xunit;

namespace XinSpect.Tests;

[Collection(HardwareOperationPolicyCollection.Name)]
public sealed class SyntheticThreatScenarioAuditTests
{
    [Fact]
    public void 合成情境分別驗證正常基線與BYOVD警示()
    {
        IReadOnlyList<SyntheticThreatScenarioResult> results = SyntheticThreatScenarioAudit.Run();

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Passed, result.Name));
        Assert.Equal(SecuritySeverity.Good, results[0].ActualSeverity);
        Assert.Equal("drv.byovd", results[1].FindingId);
        Assert.Equal(SecuritySeverity.Critical, results[1].ActualSeverity);
    }

    [Fact]
    public void 合成情境也受第二級開關控制()
    {
        var settings = new SettingsService(loadFromDisk: false);
        HardwareOperationGate.Bind(settings);
        var posture = new SecurityPostureService();
        try
        {
            posture.RunSyntheticThreatScenarios();
            Assert.Contains("需先啟用", posture.SyntheticThreatAuditStatus, StringComparison.Ordinal);

            settings.AdvancedDiagnosticsEnabled = true;
            settings.HighRiskHardwareOperationsEnabled = true;
            Assert.True(posture.CanRunSyntheticThreatScenarios);

            posture.RunSyntheticThreatScenarios();
            Assert.Contains("2/2 通過", posture.SyntheticThreatAuditStatus, StringComparison.Ordinal);
            Assert.Contains("未讀取或變更真實系統", posture.SyntheticThreatAuditStatus, StringComparison.Ordinal);
        }
        finally
        {
            HardwareOperationGate.Bind(null);
        }
    }
}
