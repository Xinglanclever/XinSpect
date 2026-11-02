using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 合規結果的 CI 匯出：SARIF 與 JUnit 只能轉譯判定，不能把 Unverifiable 洗成通過。
/// </summary>
public class ComplianceExportTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 5, 30, 0, TimeSpan.Zero);

    private static ComplianceReport Report() => new(
    [
        new ComplianceControl("NIST-800-147-WP", "NIST SP 800-147", "BIOS 寫入保護",
            "韌體寫入須受保護", ComplianceStatus.Satisfied, "已鎖", []),
        new ComplianceControl("NIST-800-147-UPD", "NIST SP 800-147", "更新驗簽",
            "更新須驗簽", ComplianceStatus.NotSatisfied, "BIOS_CNTL 顯示可寫",
            [new ComplianceEvidence("chipset.bios_cntl", "Present", "1", "SMM_BWP=0")]),
        new ComplianceControl("NIST-800-155-QUOTE", "NIST SP 800-155", "遠端 Quote",
            "須能遠端證明", ComplianceStatus.Unverifiable, "沒有送出 challenge",
            [new ComplianceEvidence("tpm.present", "Present", null, "有 TPM")]),
        new ComplianceControl("NIST-800-193-RECOVER", "NIST SP 800-193", "韌體復原",
            "須能復原", ComplianceStatus.NotApplicable, "平台不支援", []),
    ],
    [
        new AttackTechnique("T1542.001", "System Firmware", AttackExposure.Exposed, "韌體可寫",
            ["chipset.bios_cntl"]),
        new AttackTechnique("T1495", "Firmware Corruption", AttackExposure.Unknown, "復原路徑未驗",
            ["bt.recovery"]),
        new AttackTechnique("T1014", "Rootkit", AttackExposure.Mitigated, "BYOVD 封鎖清單命中 0",
            ["byovd.hits"]),
    ]);

    [Fact]
    public void SARIF只把非通過項變成result且Unverifiable是warning()
    {
        using var doc = JsonDocument.Parse(ComplianceExportService.ToSarif(Report(), At));
        var root = doc.RootElement;

        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        var run = root.GetProperty("runs")[0];
        Assert.Equal("XinSpect", run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());

        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(7, rules.Count);
        Assert.Equal(rules.Count, rules.Select(r => r.GetProperty("id").GetString()).Distinct().Count());

        var results = run.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(4, results.Count); // NotSatisfied、Unverifiable、Exposed、Unknown
        Assert.DoesNotContain(results, r => r.GetProperty("ruleId").GetString() == "NIST-800-147-WP");
        Assert.DoesNotContain(results, r => r.GetProperty("ruleId").GetString() == "T1014");

        var unverifiable = Assert.Single(results,
            r => r.GetProperty("ruleId").GetString() == "NIST-800-155-QUOTE");
        Assert.Equal("warning", unverifiable.GetProperty("level").GetString());

        var notSatisfied = Assert.Single(results,
            r => r.GetProperty("ruleId").GetString() == "NIST-800-147-UPD");
        Assert.Equal("error", notSatisfied.GetProperty("level").GetString());
    }

    [Fact]
    public void JUnit把Unverifiable記成failure而不是skipped()
    {
        string xml = ComplianceExportService.ToJUnit(Report(), At);
        Assert.StartsWith("<?xml", xml, StringComparison.Ordinal);

        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        Assert.Equal("testsuites", root.Name.LocalName);
        Assert.Equal("7", root.Attribute("tests")?.Value);
        Assert.Equal("4", root.Attribute("failures")?.Value); // 2 控制項 + 2 技術
        Assert.Equal("1", root.Attribute("skipped")?.Value);  // NotApplicable 控制項

        var quote = Assert.Single(doc.Descendants("testcase"),
            t => (string?)t.Attribute("name") is { } n && n.StartsWith("NIST-800-155-QUOTE", StringComparison.Ordinal));
        var failure = Assert.Single(quote.Elements("failure"));
        Assert.Equal("Unverifiable", (string?)failure.Attribute("type"));
        Assert.Empty(quote.Elements("skipped"));

        var recover = Assert.Single(doc.Descendants("testcase"),
            t => (string?)t.Attribute("name") is { } n && n.StartsWith("NIST-800-193-RECOVER", StringComparison.Ordinal));
        Assert.Empty(recover.Elements("failure"));
        Assert.Single(recover.Elements("skipped"));

        var satisfied = Assert.Single(doc.Descendants("testcase"),
            t => (string?)t.Attribute("name") is { } n && n.StartsWith("NIST-800-147-WP", StringComparison.Ordinal));
        Assert.Empty(satisfied.Elements("failure"));
        Assert.Empty(satisfied.Elements("skipped"));
    }

    [Fact]
    public void CEF每條控制項與技術各一行_且severity由判定映射()
    {
        string cef = ComplianceExportService.ToCef(Report(), At);
        var lines = cef.Split('\n');

        Assert.Equal(7, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("CEF:0|XinSpect|XinSpect|", line, StringComparison.Ordinal));

        int SeverityOf(string id)
        {
            var line = Assert.Single(lines, l => l.Contains($"|{id}|", StringComparison.Ordinal));
            return int.Parse(line.Split('|')[6], System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(2, SeverityOf("NIST-800-147-WP"));   // Satisfied
        Assert.Equal(8, SeverityOf("NIST-800-147-UPD"));  // NotSatisfied
        Assert.Equal(4, SeverityOf("NIST-800-155-QUOTE")); // Unverifiable
        Assert.Equal(0, SeverityOf("NIST-800-193-RECOVER")); // NotApplicable
        Assert.Equal(8, SeverityOf("T1542.001"));         // Exposed
        Assert.Equal(4, SeverityOf("T1495"));             // Unknown
        Assert.Equal(2, SeverityOf("T1014"));             // Mitigated
    }

    [Fact]
    public void CEF會轉義標頭與延伸欄位()
    {
        var report = new ComplianceReport(
        [
            new ComplianceControl("TEST|ID", "測試|框架", "標題|管線",
                "需求", ComplianceStatus.NotSatisfied, "原因是 a=b\n下一行",
                [new ComplianceEvidence("k=v", "Present", null, "note")]),
        ], []);

        string line = Assert.Single(ComplianceExportService.ToCef(report, At).Split('\n'));

        Assert.Contains(@"TEST\|ID", line, StringComparison.Ordinal);
        Assert.Contains(@"標題\|管線", line, StringComparison.Ordinal);
        Assert.Contains(@"a\=b", line, StringComparison.Ordinal);
        Assert.Contains(@"k\=v", line, StringComparison.Ordinal);
        Assert.Contains(@"\n下一行", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    [Fact]
    public void CLI的CEF輸出逐行且保留退出碼2()
    {
        var source = new[]
        {
            Fact("chipset.bios_cntl", "最強保護"),
            Fact("tpm.present", "可用"),
        };

        var stdout = new StringWriter();
        int exit = CliService.Run(["--compliance", "--format", "cef"], () => source,
            stdout, new StringWriter());

        Assert.Equal(CliService.ExitPartial, exit);
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(16, lines.Length); // 11 控制項 + 5 ATT&CK
        Assert.All(lines, line => Assert.StartsWith("CEF:0|", line, StringComparison.Ordinal));
    }

    [Fact]
    public void OTel輸出有resource與scope且severity與CEF同源()
    {
        using var doc = JsonDocument.Parse(ComplianceExportService.ToOtel(Report(), At));
        var resourceLogs = doc.RootElement.GetProperty("resourceLogs");
        Assert.Equal(1, resourceLogs.GetArrayLength());
        var resource = resourceLogs[0].GetProperty("resource");
        Assert.Contains(resource.GetProperty("attributes").EnumerateArray(),
            a => a.GetProperty("key").GetString() == "service.name"
                 && a.GetProperty("value").GetProperty("stringValue").GetString() == "XinSpect");

        var scope = resourceLogs[0].GetProperty("scopeLogs")[0];
        Assert.Equal("XinSpect.Compliance", scope.GetProperty("scope").GetProperty("name").GetString());
        var records = scope.GetProperty("logRecords").EnumerateArray().ToList();
        Assert.Equal(7, records.Count);

        JsonElement RecordFor(string id) => Assert.Single(records, r =>
            r.GetProperty("attributes").EnumerateArray().Any(a =>
                a.GetProperty("key").GetString() is "compliance.id" or "attack.technique.id"
                && a.GetProperty("value").GetProperty("stringValue").GetString() == id));

        Assert.Equal(17, RecordFor("NIST-800-147-UPD").GetProperty("severityNumber").GetInt32());
        Assert.Equal(13, RecordFor("NIST-800-155-QUOTE").GetProperty("severityNumber").GetInt32());
        Assert.Equal(9, RecordFor("NIST-800-147-WP").GetProperty("severityNumber").GetInt32());
        Assert.Equal(17, RecordFor("T1542.001").GetProperty("severityNumber").GetInt32());
        Assert.Matches("^[0-9]+$", RecordFor("NIST-800-147-WP").GetProperty("timeUnixNano").GetString());
    }

    [Fact]
    public void CLI的OTel輸出保留退出碼2()
    {
        var source = new[]
        {
            Fact("chipset.bios_cntl", "最強保護"),
            Fact("tpm.present", "可用"),
        };

        var stdout = new StringWriter();
        int exit = CliService.Run(["--compliance", "--format", "otel"], () => source,
            stdout, new StringWriter());

        Assert.Equal(CliService.ExitPartial, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var records = doc.RootElement.GetProperty("resourceLogs")[0]
            .GetProperty("scopeLogs")[0].GetProperty("logRecords");
        Assert.Equal(16, records.GetArrayLength()); // 11 控制項 + 5 ATT&CK
    }

    [Fact]
    public void CLI的SARIF與JUnit都保留退出碼2且輸出可解析()
    {
        var source = new[]
        {
            Fact("chipset.bios_cntl", "最強保護"),
            Fact("tpm.present", "可用"),
        };

        var sarifOut = new StringWriter();
        int sarifExit = CliService.Run(["--compliance", "--format", "sarif"], () => source,
            sarifOut, new StringWriter());
        Assert.Equal(CliService.ExitPartial, sarifExit);
        using (var doc = JsonDocument.Parse(sarifOut.ToString()))
            Assert.Equal("2.1.0", doc.RootElement.GetProperty("version").GetString());

        var junitOut = new StringWriter();
        int junitExit = CliService.Run(["--compliance", "--format", "junit"], () => source,
            junitOut, new StringWriter());
        Assert.Equal(CliService.ExitPartial, junitExit);
        var xml = XDocument.Parse(junitOut.ToString());
        Assert.Equal("testsuites", xml.Root!.Name.LocalName);
    }

    [Fact]
    public void CLI不認得的格式要回致命且不輸出半份報告()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exit = CliService.Run(["--compliance", "--format", "xml"], () => [], stdout, stderr);

        Assert.Equal(CliService.ExitError, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("不支援的格式", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CLI的format沒有值時要明說而不是默默回JSON()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exit = CliService.Run(["--compliance", "--format"], () => [], stdout, stderr);

        Assert.Equal(CliService.ExitError, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("需要一個值", stderr.ToString(), StringComparison.Ordinal);
    }

    private static HardwareFact Fact(string key, string value)
        => new(key, "測試域", key, value, "", "測試來源", FactTrustLevel.Measured, false, At);
}
