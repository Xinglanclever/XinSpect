using System.IO;
using Xunit;

namespace XinSpect.Tests;

public sealed class SensorAlertStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "XinSpect-sensor-alert-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "rules.json");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void IndependentConfigurationRoundTripsRuleIdentityUnitsAndDisabledStateAcrossRestart()
    {
        var store = new SensorAlertStore(FilePath);
        Assert.Empty(store.Load(out var initialError)); Assert.Null(initialError);
        var rule = SensorAlertEngineTests.Rule(2.5, 90) with { Enabled = false, Unit = "°C" };
        Assert.True(store.Save(new[] { rule }, out var error), error);
        var restarted = new SensorAlertService(new SensorAlertStore(FilePath));
        Assert.Equal(rule, Assert.Single(restarted.Rules));
        Assert.Equal(SensorAlertPhase.Disabled, Assert.Single(restarted.Statuses).Phase);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void CorruptConfigurationIsVisibleAndNeverOverwrittenBySave()
    {
        Directory.CreateDirectory(_directory); const string broken = "{not JSON: retained evidence}"; File.WriteAllText(FilePath, broken);
        var service = new SensorAlertService(new SensorAlertStore(FilePath));
        Assert.Empty(service.Rules); Assert.NotEmpty(service.ConfigurationProblem);
        Assert.False(service.SaveRule(SensorAlertEngineTests.Rule(), out var error));
        Assert.NotNull(error); Assert.Empty(service.Rules); Assert.Equal(broken, File.ReadAllText(FilePath));
    }

    [Fact]
    public void OversizedConfigurationAndUnknownSchemaRefuseLoadWithoutErasingEvidence()
    {
        Directory.CreateDirectory(_directory); File.WriteAllBytes(FilePath, new byte[SensorAlertStore.MaxFileBytes + 1]);
        Assert.Empty(new SensorAlertStore(FilePath).Load(out var error)); Assert.NotNull(error);
        Assert.Equal(SensorAlertStore.MaxFileBytes + 1, new FileInfo(FilePath).Length);
        const string future = "{\"Version\":999,\"Rules\":[]}"; File.WriteAllText(FilePath, future);
        Assert.Empty(new SensorAlertStore(FilePath).Load(out var versionError)); Assert.NotNull(versionError);
        Assert.Equal(future, File.ReadAllText(FilePath));
    }

    [Fact]
    public void InvalidAndDuplicateRulesCannotReplaceLastGoodConfiguration()
    {
        var store = new SensorAlertStore(FilePath); var rule = SensorAlertEngineTests.Rule();
        Assert.True(store.Save(new[] { rule }, out _)); var original = File.ReadAllBytes(FilePath);
        Assert.False(store.Save(new[] { rule with { CooldownSeconds = double.PositiveInfinity } }, out _));
        Assert.False(store.Save(new[] { rule, rule }, out _));
        Assert.Equal(original, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void FailedLocalSaveDoesNotClaimAnUnsavedRuleIsActive()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "blocked"); File.WriteAllText(blocked, "ordinary file");
        var service = new SensorAlertService(new SensorAlertStore(Path.Combine(blocked, "rules.json")));
        Assert.False(service.SaveRule(SensorAlertEngineTests.Rule(), out var error));
        Assert.NotNull(error); Assert.Empty(service.Rules); Assert.Empty(service.Statuses);
    }

    [Fact]
    public void RuleToggleAndDeletionArePersistedRatherThanOnlyChangingTheView()
    {
        var service = new SensorAlertService(new SensorAlertStore(FilePath)); var rule = SensorAlertEngineTests.Rule();
        Assert.True(service.SaveRule(rule, out _)); Assert.True(service.SaveRule(rule with { Enabled = false }, out _));
        Assert.False(Assert.Single(new SensorAlertService(new SensorAlertStore(FilePath)).Rules).Enabled);
        Assert.True(service.DeleteRule(rule.Id, out _));
        Assert.Empty(new SensorAlertService(new SensorAlertStore(FilePath)).Rules);
    }

    [Fact]
    public void ArbitraryAlertUsesExistingBalloonButNeverAutomaticAiDiagnosis()
    {
        var service = new SensorAlertService(new SensorAlertStore(FilePath));
        Assert.True(service.SaveRule(SensorAlertEngineTests.Rule(), out _));
        var alerts = new AlertService(service); int balloons = 0, diagnoses = 0;
        alerts.Balloon = (_, _) => balloons++; alerts.Diagnose = (_, _) => diagnoses++;
        var frame = SensorAlertEngineTests.Frame(1, 0, SensorAlertEngineTests.Reading());
        service.Check(frame, true, SensorAlertEngineTests.Tick(0), DateTimeOffset.UtcNow);
        service.Check(frame, true, SensorAlertEngineTests.Tick(1), DateTimeOffset.UtcNow);
        Assert.Equal(1, balloons); Assert.Equal(0, diagnoses); Assert.Single(alerts.Events); Assert.Single(service.History);
    }

    [Fact]
    public void AlertEventAndBalloonLocalizeKnownProseWhileKeepingRuleNameAndRawMessage()
    {
        var oldEnglish = LanguageService.IsEnglish;
        var oldSimplified = LanguageService.IsSimplified;
        try
        {
            LanguageService.SetEnglishForTests(true);
            var service = new SensorAlertService(new SensorAlertStore(FilePath));
            var rule = SensorAlertEngineTests.Rule() with { Name = "機殼風扇 原名" };
            Assert.True(service.SaveRule(rule, out _));
            var alerts = new AlertService(service);
            string? balloon = null;
            alerts.Balloon = (_, body) => balloon = body;
            string rawMessage = $"{rule.Name}：90 RPM；≥ 80 RPM；恢復 ≤ 70 RPM，持續 0.0 秒。已觸發通知。";

            service.Check(SensorAlertEngineTests.Frame(1, 0, SensorAlertEngineTests.Reading()), true,
                SensorAlertEngineTests.Tick(0), new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));

            var alert = Assert.Single(alerts.Events);
            Assert.Equal(rawMessage, alert.Message);
            Assert.Equal(alert.DisplayMessage, balloon);
            Assert.StartsWith("機殼風扇 原名: 90 RPM;", alert.DisplayMessage);
            Assert.Contains("sustained for 0.0 s", alert.DisplayMessage);
            Assert.Contains("Notification triggered.", alert.DisplayMessage);
            Assert.Contains("recovery ≤ 70 RPM", alert.DisplayMessage);
            Assert.Equal(alert.DisplayMessage, Assert.Single(service.History).DisplayMessage);
            Assert.DoesNotContain("恢復", alert.DisplayMessage);
            Assert.DoesNotContain("已觸發通知", alert.DisplayMessage);
        }
        finally
        {
            if (oldEnglish) LanguageService.SetEnglishForTests(true);
            else { LanguageService.SetEnglishForTests(false); LanguageService.SetSimplifiedForTests(oldSimplified); }
        }
    }

    [Fact]
    public void HistoryIsBoundedAndRecoveryRemainsAuditableWhenCooldownSuppressesNotification()
    {
        var service = new SensorAlertService(new SensorAlertStore(FilePath));
        Assert.True(service.SaveRule(SensorAlertEngineTests.Rule(cooldown: 604800), out _));
        for (int i = 0; i < 240; i++)
        {
            var frame = SensorAlertEngineTests.Frame(i + 1, i, SensorAlertEngineTests.Reading(i % 2 == 0 ? 90 : 60));
            service.Check(frame, true, SensorAlertEngineTests.Tick(i), DateTimeOffset.UtcNow);
        }
        Assert.Equal(200, service.History.Count);
        Assert.Equal(SensorAlertTransitionKind.Recovered, service.History[0].Kind);
        Assert.Contains(service.History, h => h.Kind == SensorAlertTransitionKind.Triggered && h.Message.Contains("冷卻"));
    }

    [Fact]
    public void EditorRejectsNonFiniteInputsAndReportsTheInvalidFieldBeforeWriting()
    {
        var service = new SensorAlertService(new SensorAlertStore(FilePath));
        var editor = new SensorAlertEditor(service);
        editor.RefreshSensors(SensorAlertEngineTests.Frame(1, 0, SensorAlertEngineTests.Reading()));
        editor.SelectedSensor = Assert.Single(editor.Sensors);
        editor.Threshold = "NaN"; editor.Recovery = "70";
        Assert.False(editor.Save()); Assert.True(editor.HasErrors); Assert.NotEmpty(editor.GetErrors(nameof(editor.Threshold)).Cast<string>());
        Assert.False(File.Exists(FilePath));
        editor.Threshold = "80"; Assert.True(editor.Save(), editor.ErrorSummary);
        Assert.Single(service.Rules); Assert.True(File.Exists(FilePath));
        editor.Cooldown = "604801"; Assert.False(editor.Save());
        Assert.Equal(60, Assert.Single(service.Rules).CooldownSeconds);
    }
}
