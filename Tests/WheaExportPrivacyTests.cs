using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using Xunit;
namespace XinSpect.Tests;

// Pure saved-event fixture through reader injection and actual export services. No EventLog/hardware query.
public sealed class WheaExportPrivacyTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
    private const string Serial = "FEDCBA9876543210";
    private static IReadOnlyList<HardwareFact> PcieFacts()
    {
        var b = new byte[408];
        "CPER"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 0x0210);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), (uint)b.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(128), 200);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(132), 208);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(136), 0x0300);
        CperMemoryEnvelopeDecoder.PcieType.TryWriteBytes(b.AsSpan(144, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(176), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(200), 1UL << 4);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(240), 0xfedcba9876543210);
        string xml = $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events'><System><Provider Guid='c26c4f3c-3f66-4e99-8f8a-39405cfed220'/><EventID>17</EventID><Version>0</Version><EventRecordID>42</EventRecordID></System><EventData><Data Name='Length'>{b.Length}</Data><Data Name='RawData'>{Convert.ToHexString(b)}</Data></EventData></Event>";
        var e = WheaEventEnvelopeParser.Parse(xml, 17);
        Assert.True(e.Valid); Assert.Empty(e.Cper!.Sections);
        var read = WheaMemoryRecordsReader.ReadResult(() => [new WheaMemoryEvent(17, At) { Envelope = e }], At, () => TimeSpan.Zero);
        Assert.Single(read); Assert.Same(e, read[0].Envelope);
        var facts = WheaErrorFactsService.Collect(read, At);
        Assert.Contains(facts, f => f.Key == "whea.pcie.rec.e42.s0.serial" && f.Sensitive && f.Value.Contains(Serial));
        return facts;
    }

    [Fact]
    public void AcceptedPcieOnlyReaderEvidenceIsRedactedInRealCliJsonAndStdinRoutes()
    {
        var facts = PcieFacts();
        using var output = new StringWriter(); using var errors = new StringWriter();
        int code = CliService.Run(["--json", "evidence", "--query", "whea.pcie."], () => facts, output, errors);
        Assert.True(code is CliService.ExitOk or CliService.ExitPartial);
        string json = output.ToString(); Assert.DoesNotContain(Serial, json);
        using var parsed = JsonDocument.Parse(json);
        var serial = parsed.RootElement.GetProperty("facts").EnumerateArray().Single(f => f.GetProperty("key").GetString()!.EndsWith(".serial"));
        Assert.True(serial.GetProperty("sensitive").GetBoolean());
        Assert.Equal(HardwareSnapshotService.RedactedValue, serial.GetProperty("value").GetString());
        using var batchOutput = new StringWriter(); using var input = new StringReader("whea.pcie.\n");
        Assert.Equal(CliService.ExitOk, CliService.Run([CliService.StdinBatchArg], () => facts, batchOutput, errors, stdin: input));
        Assert.DoesNotContain(Serial, batchOutput.ToString());
        Assert.Contains("whea.pcie.rec.e42.s0.serial", batchOutput.ToString());
    }

    [Fact]
    public void CliSensitiveNumericAndReasonCannotCarrySecretPastMaskedValue()
    {
        HardwareFact secret = new("whea.pcie.rec.e7.s0.serial", "硬體錯誤", "serial", "secret-value", "", "fixture", FactTrustLevel.Reported, true, At,
            987654321d, FactAvailability.Unknown, "secret-reason");
        using var output = new StringWriter(); using var errors = new StringWriter();
        CliService.Run(["--json", "evidence"], () => [secret], output, errors);
        string json = output.ToString(); Assert.DoesNotContain("secret-value", json); Assert.DoesNotContain("secret-reason", json);
        Assert.DoesNotContain("987654321", json);
        using var parsed = JsonDocument.Parse(json);
        var fact = parsed.RootElement.GetProperty("facts")[0];
        Assert.Equal(JsonValueKind.Null, fact.GetProperty("numericValue").ValueKind);
        Assert.Equal("Unknown", fact.GetProperty("availability").GetString());
        Assert.Equal(HardwareSnapshotService.RedactedValue, fact.GetProperty("unavailableReason").GetString());
    }

    [Fact]
    public void HtmlDefaultsToRedactionAndExplicitPreserveIsHonoredWithValidIntegrity()
    {
        var facts = PcieFacts();
        var rows = facts.Select(EvidenceFactRow.From).ToArray();
        string redacted = HtmlReportService.Build(rows, "fixture", At);
        Assert.DoesNotContain(Serial, redacted); Assert.True(HtmlReportService.Verify(redacted));
        string preserved = HtmlReportService.Build(rows, "fixture", At, includeSensitive: true);
        Assert.Contains(Serial, preserved); Assert.True(HtmlReportService.Verify(preserved));
        Assert.DoesNotContain(Serial, HardwareSnapshotService.ToJson(HardwareSnapshotService.Create("fixture", facts, false)));
    }

    [Fact]
    public void HtmlMasksSensitiveUnavailableReasonAndKeepsPublicRowsAndEscaping()
    {
        EvidenceFactRow secret = new("test", "secret", "secret-value", "fixture", "Reported", true, FactAvailability.ReadError, "secret-reason");
        EvidenceFactRow ordinary = new("test", "ordinary", "<public>", "fixture", "Reported", false);
        string masked = HtmlReportService.Build([secret, ordinary], "fixture", At);
        Assert.DoesNotContain("secret-value", masked); Assert.DoesNotContain("secret-reason", masked);
        Assert.Contains("&lt;public&gt;", masked); Assert.Contains("class=\"unavail\"", masked); Assert.True(HtmlReportService.Verify(masked));
        string preserved = HtmlReportService.Build([secret], "fixture", At, includeSensitive: true);
        Assert.Contains("secret-reason", preserved); Assert.True(HtmlReportService.Verify(preserved));
    }
}
