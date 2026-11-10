namespace XinSpect;

public sealed record SyntheticThreatScenarioResult(
    string Name,
    SecuritySeverity ExpectedSeverity,
    SecuritySeverity ActualSeverity,
    bool Passed,
    string FindingId);

/// <summary>Exercises the existing security scorer with fixed synthetic facts only.</summary>
public static class SyntheticThreatScenarioAudit
{
    public static IReadOnlyList<SyntheticThreatScenarioResult> Run()
    {
        SecurityFinding baseline = EvaluateDrivers(new SecurityScoreEngine.DriverFacts(
            TotalDrivers: 4, UnsignedDrivers: 0, KnownVulnerableDrivers: 0,
            VulnerableDriverBlocklistEnabled: true, LsassProtected: true, WdacActive: true,
            KernelDebugOff: true, CoInstallersDisabled: true, StaleDriverCertsClean: true));
        SecurityFinding simulatedByovd = EvaluateDrivers(new SecurityScoreEngine.DriverFacts(
            TotalDrivers: 4, UnsignedDrivers: 1, KnownVulnerableDrivers: 1,
            VulnerableDriverBlocklistEnabled: false, LsassProtected: true, WdacActive: true,
            KernelDebugOff: true, CoInstallersDisabled: true, StaleDriverCertsClean: true));

        return
        [
            Result("正常基線", baseline, SecuritySeverity.Good),
            Result("合成 BYOVD 驅動", simulatedByovd, SecuritySeverity.Critical),
        ];
    }

    private static SecurityFinding EvaluateDrivers(SecurityScoreEngine.DriverFacts drivers)
    {
        SecurityPosture posture = SecurityScoreEngine.Evaluate(
            new SecurityScoreEngine.DmaFacts(null, null, null, null, null, null, null, null, null),
            new SecurityScoreEngine.FirmwareFacts(null, null, null, null, null, null, null, null, null, null, null),
            new SecurityScoreEngine.CpuFacts(null, null, null, null, null, null, null, null, null, null),
            new SecurityScoreEngine.StorageFacts(null, null, null, null, null),
            drivers,
            new SecurityScoreEngine.SurfaceFacts(null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null));
        return posture.Categories.SelectMany(category => category.Findings)
            .Single(finding => finding.Id == "drv.byovd");
    }

    private static SyntheticThreatScenarioResult Result(
        string name, SecurityFinding finding, SecuritySeverity expected) =>
        new(name, expected, finding.Severity, finding.Severity == expected, finding.Id);
}
