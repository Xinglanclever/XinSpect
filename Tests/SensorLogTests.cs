using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Synthetic observations and temporary files only; no hardware, user settings or production log folders.</summary>
public sealed class SensorLogTests
{
    private static readonly DateTimeOffset ReadTime = DateTimeOffset.Parse("2026-11-27T01:02:03+00:00", CultureInfo.InvariantCulture);
    private static readonly long Start = Stopwatch.Frequency * 10;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);
    private static SensorReading Reading(string id = "/cpu/0/temp/0", double? value = 45.25)
        => new(id, "/cpu/0", "CPU 測試", "核心 #0", "Temperature", "°C", value,
            FactAvailability.Present, FactTrustLevel.Reported, "Synthetic fixture", ReadTime, null, "");
    private static SensorFrame Frame(long sequence, long captured, params SensorReading[] readings)
        => new(sequence, captured, ReadTime, readings);
    private static SensorCsvSession Session(TextWriter writer, params SensorReading[] selected)
        => new(writer, selected.Select(SensorLogSensorDefinition.FromReading), Start);

    [Fact]
    public void SelectedStableIdsLockOrderAndLabelsWhileNewAndMissingSensorsDoNotChangeSchema()
    {
        var first = Reading(); var second = Reading("/gpu/1/fan/0", 1234);
        using var output = new StringWriter();
        using var session = Session(output, second, first);
        Assert.True(session.TryWriteFrame(Frame(1, Start, first, second, Reading("not-selected")), Start, Interval, MaxAge));
        Assert.True(session.TryWriteFrame(Frame(2, Start + Stopwatch.Frequency * 2, first with { Name = "changed label", Value = 51 }),
            Start + Stopwatch.Frequency * 2, Interval, MaxAge));
        var rows = Data(output.ToString());
        Assert.Equal(4, rows.Count);
        Assert.Equal(second.Id, rows[0]["SensorId"]);
        Assert.Equal(first.Id, rows[1]["SensorId"]);
        Assert.Equal("Missing", rows[2]["State"]);
        Assert.Equal("", rows[2]["Value"]);
        Assert.Contains("no previous value", rows[2]["Reason"]);
        Assert.Equal(first.Name, rows[3]["SensorName"]);
        Assert.Equal("51", rows[3]["Value"]);
        Assert.Equal(2, session.FrameCount);
        Assert.Equal(4L, session.RecordCount);
    }

    [Theory]
    [InlineData(FactAvailability.Unknown)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.NotApplicable)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.ReadError)]
    public void UnavailableValuesAreBlankAndKeepExactProviderStateAndReason(FactAvailability availability)
    {
        var reading = Reading() with { Availability = availability, Value = 99, Reason = "provider reason" };
        using var output = new StringWriter();
        using var session = Session(output, reading);
        session.TryWriteFrame(Frame(1, Start, reading), Start, Interval, MaxAge);
        var row = Assert.Single(Data(output.ToString()));
        Assert.Equal("", row["Value"]);
        Assert.Equal(availability.ToString(), row["Availability"]);
        Assert.Equal(availability.ToString(), row["ProviderAvailability"]);
        Assert.Contains("provider reason", row["Reason"]);
    }

    [Theory]
    [InlineData(FactTrustLevel.Unknown)]
    [InlineData(FactTrustLevel.Speculative)]
    public void UnverifiedProviderValuesAreNotExportedAsNumbers(FactTrustLevel trust)
    {
        var reading = Reading() with { Trust = trust };
        using var output = new StringWriter();
        using var session = Session(output, reading);
        session.TryWriteFrame(Frame(1, Start, reading), Start, Interval, MaxAge);
        var row = Assert.Single(Data(output.ToString()));
        Assert.Equal("Unverified", row["State"]); Assert.Equal("", row["Value"]);
        Assert.Equal("Present", row["ProviderAvailability"]); Assert.Equal(trust.ToString(), row["Trust"]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(null)]
    public void NonFiniteOrMissingNumbersAreBlankAndExplicitlyInvalid(double? value)
    {
        var reading = Reading(value: value);
        using var output = new StringWriter(); using var session = Session(output, reading);
        session.TryWriteFrame(Frame(1, Start, reading), Start, Interval, MaxAge);
        var row = Assert.Single(Data(output.ToString()));
        Assert.Equal("Invalid", row["State"]); Assert.Equal("", row["Value"]);
    }

    [Fact]
    public void RealZeroIsPreservedAndSourceUpdateTimeRemainsUnknownInsteadOfInvented()
    {
        var reading = Reading(value: 0);
        using var output = new StringWriter(); using var session = Session(output, reading);
        session.TryWriteFrame(Frame(1, Start, reading), Start, Interval, MaxAge);
        var row = Assert.Single(Data(output.ToString()));
        Assert.Equal("Present", row["State"]); Assert.Equal("0", row["Value"]);
        Assert.Equal("", row["SourceUpdatedAtUtc"]); Assert.Equal("Unknown", row["SourceTimeState"]);
        Assert.Equal(ReadTime.ToString("O", CultureInfo.InvariantCulture), row["ReadAtUtc"]);
    }

    [Fact]
    public void FreshnessUsesMonotonicFrameTimeAndStaleFrameCannotRepeatPreviousNumbers()
    {
        var reading = Reading();
        using var output = new StringWriter(); using var session = Session(output, reading);
        session.TryWriteFrame(Frame(1, Start, reading), Start, Interval, MaxAge);
        session.TryWriteFrame(Frame(2, Start + 1, reading), Start + Stopwatch.Frequency * 8, Interval, MaxAge);
        var rows = Data(output.ToString());
        Assert.Equal("45.25", rows[0]["Value"]);
        Assert.Equal("Stale", rows[1]["State"]); Assert.Equal("", rows[1]["Value"]);
        Assert.Equal("Unknown", rows[1]["Availability"]); Assert.Equal("Present", rows[1]["ProviderAvailability"]);
    }

    [Fact]
    public void IntervalAndDuplicateDetectionIgnoreWallClockChangesAndOutOfOrderFrames()
    {
        var reading = Reading();
        using var output = new StringWriter(); using var session = Session(output, reading);
        Assert.True(session.TryWriteFrame(Frame(5, Start, reading), Start, Interval, MaxAge));
        Assert.False(session.TryWriteFrame(Frame(6, Start + 1, reading) with { ReadAtUtc = ReadTime.AddYears(2) }, Start + 1, Interval, MaxAge));
        long due = Start + Stopwatch.Frequency * 2;
        Assert.False(session.TryWriteFrame(Frame(5, due, reading), due, Interval, MaxAge));
        Assert.False(session.TryWriteFrame(Frame(4, due, reading), due, Interval, MaxAge));
        Assert.True(session.TryWriteFrame(Frame(6, due, reading) with { ReadAtUtc = ReadTime.AddYears(-2) }, due, Interval, MaxAge));
        Assert.Equal("2", Data(output.ToString())[1]["ElapsedSeconds"]);
        Assert.False(session.TryWriteFrame(Frame(7, Start, reading), Start, Interval, MaxAge));
        Assert.Equal(2, session.FrameCount);
    }

    [Fact]
    public void UnitOrIdentityDriftAndDuplicateFrameIdentitiesAreWithheld()
    {
        var reading = Reading();
        using var output = new StringWriter(); using var session = Session(output, reading);
        session.TryWriteFrame(Frame(1, Start, reading with { Unit = "°F" }), Start, Interval, MaxAge);
        long due = Start + Stopwatch.Frequency * 2;
        session.TryWriteFrame(Frame(2, due, reading, reading), due, Interval, MaxAge);
        var rows = Data(output.ToString());
        Assert.Equal("SchemaMismatch", rows[0]["State"]); Assert.Equal("°C", rows[0]["Unit"]);
        Assert.Equal("Ambiguous", rows[1]["State"]);
        Assert.All(rows, row => Assert.Equal("", row["Value"]));
    }

    [Fact]
    public void CommasQuotesNewlinesAndFormulaLabelsRoundTripSafelyWithInvariantNumbers()
    {
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var reading = Reading(value: -12.3456789012345) with
            {
                Name = " =HYPERLINK(\"x\"),\r\n中文", DeviceName = "@SUM(1+1)", Source = "+source", Reason = "-reason",
                SourceUpdatedAtUtc = ReadTime.AddSeconds(-1),
            };
            using var output = new StringWriter(); using var session = Session(output, reading);
            session.TryWriteFrame(Frame(1, Start, reading), Start, Interval, MaxAge);
            var row = Assert.Single(Data(output.ToString()));
            Assert.Equal("'" + reading.Name, row["SensorName"]); Assert.Equal("'@SUM(1+1)", row["DeviceName"]);
            Assert.Equal("'+source", row["Source"]); Assert.Equal("'-reason", row["Reason"]);
            Assert.Equal("-12.3456789012345", row["Value"]);
            Assert.Equal("Reported", row["SourceTimeState"]);
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    }

    [Theory]
    [InlineData("\tcmd")]
    [InlineData("\rcmd")]
    [InlineData("\ncmd")]
    [InlineData("  =1+1")]
    [InlineData("\u2003@cmd")]
    [InlineData("\0=1")]
    [InlineData("\uFEFF=1")]
    [InlineData("\u200B+1")]
    public void SpreadsheetFormulaEscapingIncludesLeadingWhitespaceAndControlBypasses(string label)
        => Assert.Equal("'" + label, Parse(SensorCsvSession.Cell(label) + "\r\n")[0][0]);

    [Fact]
    public void SessionRequiresUniqueNonemptySelection()
    {
        using var output = new StringWriter();
        Assert.Throws<ArgumentException>(() => Session(output));
        Assert.Throws<ArgumentException>(() => Session(output, Reading(), Reading()));
        Assert.Throws<ArgumentException>(() => Session(output, Reading("")));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void RealFilesHaveUtf8BomAndSameTimestampCreatesNewFileWithoutOverwriting()
    {
        string directory = Path.Combine(Path.GetTempPath(), "XinSpect-SensorLogTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = SensorCsvFile.Create(directory, ReadTime);
            first.Writer.WriteLine("original 中文"); first.Writer.Dispose();
            byte[] original = File.ReadAllBytes(first.Path);
            var second = SensorCsvFile.Create(directory, ReadTime);
            second.Writer.WriteLine("second"); second.Writer.Dispose();
            Assert.NotEqual(first.Path, second.Path);
            Assert.Equal(original, File.ReadAllBytes(first.Path));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, original[..3]);
            Assert.Contains("中文", File.ReadAllText(first.Path, Encoding.UTF8));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CompatibilityStartWaitsWithoutCreatingFalseEmptyFile()
    {
        int creates = 0;
        var logger = new SensorLogService(() => Start, () => ReadTime, (_, _) => { creates++; return ("memory.csv", new StringWriter()); });
        var settings = new SettingsService(false);
        logger.StartLogging(settings); logger.StopLogging();
        Assert.Equal(0, creates); Assert.Equal("—", logger.CurrentFile); Assert.Equal(0, logger.RowCount);
    }

    [Fact]
    public void EmptyFrameAndEmptySelectionDoNotCreateFiles()
    {
        int creates = 0;
        var logger = new SensorLogService(() => Start, () => ReadTime, (_, _) => { creates++; return ("memory.csv", new StringWriter()); });
        var settings = new SettingsService(false);
        logger.StartFrames(settings, []); Assert.False(logger.IsLogging); Assert.Contains("勾選", logger.StatusText);
        logger.StartFrames(settings, [Reading()]); logger.SampleFrame(settings, SensorFrame.Empty);
        Assert.Equal(0, creates); logger.StopLogging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteOrFlushFailuresCloseSessionAndRemainVisibleAfterStop(bool failOnFlush)
    {
        var writer = new FailingWriter();
        var logger = new SensorLogService(() => Start, () => ReadTime, (_, _) => ("memory.csv", writer));
        var settings = new SettingsService(false); var reading = Reading();
        logger.StartFrames(settings, [reading]);
        // Allow header initialization; fail when the data record or its flush is attempted.
        writer.FailDataWrite = !failOnFlush; writer.FailFlushNumber = failOnFlush ? 2 : int.MaxValue;
        logger.SampleFrame(settings, Frame(1, Start, reading));
        Assert.False(logger.IsLogging); Assert.True(writer.Disposed); Assert.Equal(0, logger.RowCount);
        string fault = logger.StatusText; Assert.Contains("injected", fault); Assert.Contains("injected", logger.LastError);
        logger.StopLogging(); Assert.Equal(fault, logger.StatusText);
    }

    [Fact]
    public void StopFlushFailureDoesNotClaimSuccessfulStopAndKeepsCompletedCounts()
    {
        var writer = new FailingWriter();
        var logger = new SensorLogService(() => Start, () => ReadTime, (_, _) => ("memory.csv", writer));
        var settings = new SettingsService(false); var reading = Reading();
        logger.StartFrames(settings, [reading]); logger.SampleFrame(settings, Frame(1, Start, reading));
        Assert.Equal(1, logger.RowCount); writer.FailFlushNumber = 3;
        logger.StopLogging();
        Assert.False(logger.IsLogging); Assert.True(writer.Disposed); Assert.Equal(1, logger.RowCount);
        Assert.Contains("injected", logger.StatusText); Assert.Contains("injected", logger.LastError);
    }

    [Fact]
    public void FileCreationFailureIsVisibleAndDoesNotClaimAnOpenedFile()
    {
        var logger = new SensorLogService(() => Start, () => ReadTime, (_, _) => throw new IOException("injected create failure"));
        var settings = new SettingsService(false);
        logger.StartFrames(settings, [Reading()]); logger.SampleFrame(settings, Frame(1, Start, Reading()));
        Assert.False(logger.IsLogging); Assert.Equal("—", logger.CurrentFile); Assert.Equal(0, logger.RowCount);
        Assert.Contains("injected create failure", logger.StatusText);
        logger.StopLogging(); Assert.Contains("injected create failure", logger.StatusText);
    }

    [Fact]
    public void MixedFailureStatusLocalizesOnlyTheProductPrefixAcrossLanguages()
    {
        const string raw = "C:\\記憶體\\sensor.csv: access denied";
        var logger = new SensorLogService(() => Start, () => ReadTime, (_, _) => throw new IOException(raw));
        var settings = new SettingsService(false);
        var reading = Reading();
        logger.StartFrames(settings, [reading]);
        logger.SampleFrame(settings, Frame(1, Start, reading));
        Assert.Equal(raw, logger.LastError);

        var oldEnglish = LanguageService.IsEnglish;
        var oldSimplified = LanguageService.IsSimplified;
        try
        {
            LanguageService.SetEnglishForTests(true);
            Assert.Equal("Logging failed: " + raw, logger.DisplayStatusText);
            LanguageService.SetSimplifiedForTests(true);
            Assert.Equal("记录失败：" + raw, logger.DisplayStatusText);
        }
        finally
        {
            if (oldEnglish) LanguageService.SetEnglishForTests(true);
            else { LanguageService.SetEnglishForTests(false); LanguageService.SetSimplifiedForTests(oldSimplified); }
        }
    }

    [Fact]
    public void HeaderFlushFailureClosesWriterAndKeepsPrimaryFailure()
    {
        var writer = new FailingWriter { FailFlushNumber = 1 };
        var logger = new SensorLogService(() => Start, () => ReadTime, (_, _) => ("memory.csv", writer));
        var settings = new SettingsService(false);
        logger.StartFrames(settings, [Reading()]); logger.SampleFrame(settings, Frame(1, Start, Reading()));
        Assert.False(logger.IsLogging); Assert.True(writer.Disposed); Assert.Equal(0, logger.RowCount);
        Assert.Contains("injected flush failure", logger.StatusText);
        logger.StopLogging(); Assert.Contains("injected flush failure", logger.StatusText);
    }

    private sealed class FailingWriter : StringWriter
    {
        internal bool FailDataWrite; internal int FailFlushNumber = int.MaxValue; internal bool Disposed; private int _flushes;
        public override void Write(string? value)
        {
            if (FailDataWrite && value?.Contains("Synthetic fixture", StringComparison.Ordinal) == true) throw new IOException("injected write failure");
            base.Write(value);
        }
        public override void Flush() { if (++_flushes >= FailFlushNumber) throw new IOException("injected flush failure"); base.Flush(); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static List<Dictionary<string, string>> Data(string csv)
    {
        var rows = Parse(csv);
        return rows.Skip(1).Select(row =>
        {
            Assert.Equal(rows[0].Length, row.Length);
            return rows[0].Select((name, i) => (name, row[i])).ToDictionary(x => x.name, x => x.Item2, StringComparer.Ordinal);
        }).ToList();
    }
    private static List<string[]> Parse(string csv)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (c == '"')
            {
                if (quoted && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' && !quoted)
            {
                row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add(row.ToArray()); row.Clear();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        Assert.False(quoted); return rows;
    }
}
