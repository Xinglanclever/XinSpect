using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace XinSpect;

/// <summary>
/// 把 <see cref="ComplianceReport"/> 轉成 CI／SIEM 吃得下的格式：SARIF 2.1.0 與 JUnit XML。
/// </summary>
/// <remarks>
/// <para>
/// <b>只做格式轉換，不重新判定。</b>輸入是 <see cref="ComplianceMappingService.Evaluate"/> 的結果；
/// 這裡不讀事實、不碰硬體、不改變任何 <see cref="ComplianceStatus"/>。
/// </para>
/// <para>
/// <b>Unverifiable 不進「通過」。</b>SARIF 給 <c>warning</c>，JUnit 給 <c>failure</c>——
/// 兩者都會讓 CI 看見它。若把 Unverifiable 寫成 skipped，管線會顯示綠色，
/// 那就等於把「工具沒驗」升格成「機器沒問題」。
/// </para>
/// </remarks>
public static class ComplianceExportService
{
    /// <summary>SARIF 版本；目前只輸出這個版本。</summary>
    public const string SarifVersion = "2.1.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>SARIF 2.1.0：每個控制項／ATT&amp;CK 技術是一條 rule，非通過者才是 result。</summary>
    public static string ToSarif(ComplianceReport report, DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rules = new List<object>();
        var results = new List<object>();

        foreach (var control in report.Controls)
        {
            rules.Add(new
            {
                id = control.Id,
                name = control.Title,
                shortDescription = new { text = control.Title },
                fullDescription = new { text = control.Requirement },
                properties = new
                {
                    framework = control.Framework,
                    status = control.Status.ToString(),
                    evidenceKeys = control.Evidence.Select(e => e.Key).ToArray(),
                },
            });

            if (control.Status is ComplianceStatus.Satisfied or ComplianceStatus.NotApplicable)
                continue;

            results.Add(new
            {
                ruleId = control.Id,
                level = control.Status == ComplianceStatus.NotSatisfied ? "error" : "warning",
                message = new { text = $"{control.Status}：{control.Reason}" },
                locations = new[]
                {
                    new
                    {
                        logicalLocations = new[]
                        {
                            new
                            {
                                fullyQualifiedName = $"XinSpect/{control.Framework}/{control.Id}",
                                kind = "member",
                            },
                        },
                    },
                },
                properties = new
                {
                    status = control.Status.ToString(),
                    reason = control.Reason,
                    evidenceKeys = control.Evidence.Select(e => e.Key).ToArray(),
                },
            });
        }

        foreach (var technique in report.Techniques)
        {
            rules.Add(new
            {
                id = technique.Id,
                name = technique.Name,
                shortDescription = new { text = technique.Name },
                fullDescription = new { text = "MITRE ATT&CK 技術 " + technique.Id },
                properties = new
                {
                    framework = "MITRE ATT&CK",
                    exposure = technique.Exposure.ToString(),
                    factKeys = technique.FactKeys.ToArray(),
                },
            });

            if (technique.Exposure is AttackExposure.Mitigated or AttackExposure.NotApplicable)
                continue;

            results.Add(new
            {
                ruleId = technique.Id,
                level = technique.Exposure == AttackExposure.Exposed ? "error" : "warning",
                message = new { text = $"{technique.Exposure}：{technique.Reason}" },
                locations = new[]
                {
                    new
                    {
                        logicalLocations = new[]
                        {
                            new
                            {
                                fullyQualifiedName = $"XinSpect/MITRE-ATTACK/{technique.Id}",
                                kind = "member",
                            },
                        },
                    },
                },
                properties = new
                {
                    exposure = technique.Exposure.ToString(),
                    reason = technique.Reason,
                    factKeys = technique.FactKeys.ToArray(),
                },
            });
        }

        var payload = new
        {
            schema = "https://json.schemastore.org/sarif-2.1.0.json",
            version = SarifVersion,
            runs = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new
                        {
                            name = "XinSpect",
                            semanticVersion = ToolVersion(),
                            informationUri = "https://github.com/Xinglanclever/XinSpect",
                            rules,
                        },
                    },
                    results,
                    properties = new
                    {
                        generatedAtUtc,
                        note = "Satisfied／NotApplicable／Mitigated 不產生 result；"
                             + "Unverifiable／Unknown 是 warning，不是通過。"
                             + "本檔只轉譯既有判定，沒有重新量測或驗簽。",
                    },
                },
            },
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// JUnit XML：每個控制項／技術一個 testcase。
    /// NotSatisfied／Exposed＝failure；Unverifiable／Unknown＝failure（不可顯示為綠）；
    /// NotApplicable＝skipped；其餘＝pass。
    /// </summary>
    public static string ToJUnit(ComplianceReport report, DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(report);

        var groups = new List<(string Name, List<ComplianceControl> Controls, List<AttackTechnique> Techniques)>();
        foreach (var group in report.Controls.GroupBy(c => c.Framework, StringComparer.Ordinal))
            groups.Add((group.Key, group.ToList(), []));
        if (report.Techniques.Count > 0)
            groups.Add(("MITRE ATT&CK", [], report.Techniques.ToList()));

        var buffer = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false,
        };
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("testsuites");
            writer.WriteAttributeString("name", "XinSpect.Compliance");
            writer.WriteAttributeString("tests", Total(report).ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("failures", Failures(report).ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("skipped", Skips(report).ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("time", "0");
            writer.WriteAttributeString("timestamp", generatedAtUtc.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

            foreach (var (name, controls, techniques) in groups)
            {
                int tests = controls.Count + techniques.Count;
                int failures = controls.Count(IsFailure)
                    + techniques.Count(t => t.Exposure is AttackExposure.Exposed or AttackExposure.Unknown);
                int skipped = controls.Count(c => c.Status == ComplianceStatus.NotApplicable)
                    + techniques.Count(t => t.Exposure == AttackExposure.NotApplicable);

                writer.WriteStartElement("testsuite");
                writer.WriteAttributeString("name", name);
                writer.WriteAttributeString("tests", tests.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteAttributeString("failures", failures.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteAttributeString("skipped", skipped.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteAttributeString("time", "0");

                foreach (var control in controls)
                {
                    writer.WriteStartElement("testcase");
                    writer.WriteAttributeString("classname", name);
                    writer.WriteAttributeString("name", $"{control.Id}：{control.Title}");
                    writer.WriteAttributeString("time", "0");

                    if (IsFailure(control))
                    {
                        writer.WriteStartElement("failure");
                        writer.WriteAttributeString("type", control.Status.ToString());
                        writer.WriteAttributeString("message", control.Reason);
                        writer.WriteString(EvidenceText(control));
                        writer.WriteEndElement();
                    }
                    else if (control.Status == ComplianceStatus.NotApplicable)
                    {
                        writer.WriteStartElement("skipped");
                        writer.WriteAttributeString("message", control.Reason);
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }

                foreach (var technique in techniques)
                {
                    writer.WriteStartElement("testcase");
                    writer.WriteAttributeString("classname", name);
                    writer.WriteAttributeString("name", $"{technique.Id}：{technique.Name}");
                    writer.WriteAttributeString("time", "0");

                    if (technique.Exposure is AttackExposure.Exposed or AttackExposure.Unknown)
                    {
                        writer.WriteStartElement("failure");
                        writer.WriteAttributeString("type", technique.Exposure.ToString());
                        writer.WriteAttributeString("message", technique.Reason);
                        writer.WriteString("事實鍵：" + string.Join("、", technique.FactKeys));
                        writer.WriteEndElement();
                    }
                    else if (technique.Exposure == AttackExposure.NotApplicable)
                    {
                        writer.WriteStartElement("skipped");
                        writer.WriteAttributeString("message", technique.Reason);
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsFailure(ComplianceControl control)
        => control.Status is ComplianceStatus.NotSatisfied or ComplianceStatus.Unverifiable;

    private static int Total(ComplianceReport report)
        => report.Controls.Count + report.Techniques.Count;

    private static int Failures(ComplianceReport report)
        => report.Controls.Count(IsFailure)
           + report.Techniques.Count(t => t.Exposure is AttackExposure.Exposed or AttackExposure.Unknown);

    private static int Skips(ComplianceReport report)
        => report.Controls.Count(c => c.Status == ComplianceStatus.NotApplicable)
           + report.Techniques.Count(t => t.Exposure == AttackExposure.NotApplicable);

    private static string EvidenceText(ComplianceControl control)
        => control.Evidence.Count == 0
            ? "沒有引用任何證據鍵。"
            : "證據：" + string.Join("；", control.Evidence.Select(e =>
                e.Numeric is null ? $"{e.Key}（{e.Availability}）" : $"{e.Key}={e.Numeric}（{e.Availability}）"));

    private static string ToolVersion()
    {
        var version = typeof(ComplianceExportService).Assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }
}
