using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WHEA 記憶體節區的金標（U10 第三圈 WH-0003／WH-0004）：逐欄位移照 cper.h 的
/// <c>CPER_FIELD_CHECK</c> 擺位元組，並驗「ValidBits 沒設就不印值」這條規則。
/// </summary>
public class WheaMemoryFactsTests
{
    /// <summary>
    /// 造一個 WHEA_MEMORY_ERROR_SECTION。預設 ValidBits 把所有欄位都設起來（0x3FFFF），
    /// 這樣「欄位有沒有解對」與「ValidBits 有沒有被尊重」可以分開測。
    /// </summary>
    private static byte[] Section(
        ulong validBits = 0x3_FFFF,
        ulong errorStatus = 0,
        ulong physicalAddress = 0,
        ulong mask = 0,
        ushort node = 0, ushort card = 0, ushort module = 0, ushort bank = 0,
        ushort device = 0, ushort row = 0, ushort column = 0, ushort bitPosition = 0,
        byte errorType = 0x02,
        ushort rankNumber = 0, ushort cardHandle = 0, ushort moduleHandle = 0)
    {
        var b = new byte[80];
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0, 8), validBits);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8, 8), errorStatus);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16, 8), physicalAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24, 8), mask);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(32, 2), node);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34, 2), card);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(36, 2), module);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(38, 2), bank);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(40, 2), device);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(42, 2), row);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(44, 2), column);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(46, 2), bitPosition);
        b[72] = errorType;
        b[73] = 0; // Extended
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(74, 2), rankNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(76, 2), cardHandle);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(78, 2), moduleHandle);
        return b;
    }

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    // ── 解碼器：逐欄 ────────────────────────────────────────────────────

    [Fact]
    public void 節區_逐欄比對()
    {
        var s = WheaMemorySectionDecoder.Parse(Section(
            physicalAddress: 0x1_2345_6789, mask: 0x3F,
            node: 0, card: 0, module: 1, bank: 2, device: 3, row: 0x1234, column: 0x5678,
            bitPosition: 42, errorType: 0x03, rankNumber: 1, cardHandle: 0xAA, moduleHandle: 0xBB))!;

        Assert.Equal(0x1_2345_6789UL, s.PhysicalAddress);
        Assert.Equal(0x3FUL, s.PhysicalAddressMask);
        Assert.Equal(1, s.Module);
        Assert.Equal(2, s.Bank);
        Assert.Equal(3, s.Device);
        Assert.Equal(0x1234, s.Row);
        Assert.Equal(0x5678, s.Column);
        Assert.Equal(42, s.BitPosition);
        Assert.Equal(0x03, s.ErrorType);
        Assert.Equal(1, s.RankNumber);
        Assert.Equal(0xAA, s.CardHandle);
        Assert.Equal(0xBB, s.ModuleHandle);
    }

    [Fact]
    public void 節區_不足80位元組回null()
    {
        Assert.Null(WheaMemorySectionDecoder.Parse(new byte[79]));
    }

    [Fact]
    public void ValidBits_逐位對應cper_h宣告順序()
    {
        // 只設 bit1（PhysicalAddress）與 bit5（Module）
        var s = WheaMemorySectionDecoder.Parse(Section(validBits: (1UL << 1) | (1UL << 5)))!;
        Assert.True(s.HasPhysicalAddress);
        Assert.True(s.HasModule);
        Assert.False(s.HasBitPosition);      // bit10
        Assert.False(s.HasErrorType);       // bit14
        Assert.False(s.HasModuleHandle);    // bit17
    }

    [Fact]
    public void ValidBits_全零時每個旗標都是false()
    {
        var s = WheaMemorySectionDecoder.Parse(Section(validBits: 0))!;
        Assert.False(s.HasPhysicalAddress);
        Assert.False(s.HasModule);
        Assert.False(s.HasBitPosition);
        Assert.False(s.HasErrorType);
        Assert.False(s.HasModuleHandle);
    }

    [Fact]
    public void ErrorStatus_型別在高8位_旗標從bit16起()
    {
        // 型別 0x0A 放在 bit8–15，並設 Address(bit16) 與 Overflow(bit22)
        ulong status = (0x0AUL << 8) | (1UL << 16) | (1UL << 22);
        var s = WheaMemorySectionDecoder.Parse(Section(errorStatus: status))!;
        Assert.Equal(0x0A, s.ErrorStatusType);
        Assert.True(s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.Address));
        Assert.True(s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.Overflow));
        Assert.False(s.ErrorStatusFlags.HasFlag(WheaErrorStatusFlags.Data));
    }

    [Fact]
    public void 錯誤類型碼_0x00到0x0C逐條有名_0x0D以上不命名()
    {
        for (byte b = 0x00; b <= 0x0C; b++)
            Assert.NotNull(WheaMemorySectionDecoder.ErrorTypeName(b));
        // 上界之外：不是「未知」，是「本版沒有出處」
        Assert.Null(WheaMemorySectionDecoder.ErrorTypeName(0x0D));
        Assert.Null(WheaMemorySectionDecoder.ErrorTypeName(0xFF));
    }

    [Fact]
    public void 單一位元與多位要分得出來()
    {
        Assert.Contains("單一位元", WheaMemorySectionDecoder.ErrorTypeName(0x02)!);
        Assert.Contains("多位", WheaMemorySectionDecoder.ErrorTypeName(0x03)!);
        Assert.Contains("未由本節區確認", WheaMemorySectionDecoder.ErrorTypeHint(0x03)!);
        Assert.Contains("未由本節區確認", WheaMemorySectionDecoder.ErrorTypeHint(0x09)!);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(12)]
    public void 類型不是嚴重度或維修建議(int code)
    {
        var fact = Fact(WheaMemoryFactsService.Collect([Rec(Section(errorType: (byte)code))],
            DateTimeOffset.UnixEpoch), WheaMemoryFactsService.RecordPrefix + "0001");
        Assert.Contains("更正狀態、嚴重度與故障位置未由本節區確認", fact.Value);
        Assert.DoesNotContain("典型處置", fact.Value);
        Assert.DoesNotContain("可更正且", fact.Value);
        Assert.DoesNotContain("不可更正且", fact.Value);
        Assert.DoesNotContain("不是顆粒", fact.Value);
    }

    [Fact]
    public void 碼零一_是無錯誤而不是未知()
    {
        Assert.Contains("無錯誤", WheaMemorySectionDecoder.ErrorTypeName(0x01)!);
        Assert.DoesNotContain("未知", WheaMemorySectionDecoder.ErrorTypeName(0x01)!);
        Assert.Null(WheaMemorySectionDecoder.ErrorTypeHint(0x01));
    }

    // ── 服務 ────────────────────────────────────────────────────────────

    private static WheaMemoryRecord Rec(byte[]? section, int eventId = 46, int daysAgo = 0) =>
        new(eventId, DateTimeOffset.UtcNow.AddDays(-daysAgo), section, section is null ? "測不到" : null);

    [Theory]
    [InlineData(WheaMemoryReadStatus.Truncated, FactAvailability.Unknown, false)]
    [InlineData(WheaMemoryReadStatus.Truncated, FactAvailability.Unknown, true)]
    [InlineData(WheaMemoryReadStatus.TimedOut, FactAvailability.ReadError, false)]
    [InlineData(WheaMemoryReadStatus.TimedOut, FactAvailability.ReadError, true)]
    [InlineData(WheaMemoryReadStatus.ReadError, FactAvailability.ReadError, false)]
    [InlineData(WheaMemoryReadStatus.ReadError, FactAvailability.ReadError, true)]
    [InlineData(WheaMemoryReadStatus.InsufficientPrivilege, FactAvailability.InsufficientPrivilege, false)]
    [InlineData(WheaMemoryReadStatus.InsufficientPrivilege, FactAvailability.InsufficientPrivilege, true)]
    public void IncompleteEnumerationIsPreservedByTheProductionConsumer(WheaMemoryReadStatus status,
        FactAvailability expected, bool partial)
    {
        var end = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        var records = new WheaMemoryReadResult(partial ? [Rec(Section())] : [], status,
            end.AddDays(-7), end, "injected failure");
        var lab = new EvidenceLabService();
        lab.LoadWheaMemory(records);
        var summary = Fact(lab.WheaMemoryFacts, WheaMemoryFactsService.SummaryKey);
        Assert.Equal(expected, summary.Availability);
        Assert.Null(summary.NumericValue);
        Assert.Contains(status.ToString(), summary.UnavailableReason);
        Assert.Contains("injected failure", summary.UnavailableReason);
        Assert.Contains("非完整總數", summary.Value);
        Assert.Contains(end.ToString("O"), summary.Value);
        Assert.Equal(partial ? 1 : 0,
            lab.WheaMemoryFacts.Count(f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void 空記錄_說沒有記錄可解而不是沒有錯誤()
    {
        var facts = WheaMemoryFactsService.Collect([], DateTimeOffset.UnixEpoch);
        var s = Fact(facts, WheaMemoryFactsService.SummaryKey);
        Assert.Equal(FactAvailability.Unknown, s.Availability);
        Assert.Null(s.NumericValue);
        Assert.Contains("「沒有記錄」與「沒有記憶體錯誤」是兩件事", s.UnavailableReason);
        Assert.Equal(FactAvailability.Present, Fact(facts, WheaMemoryFactsService.BoundaryKey).Availability);
    }

    [Fact]
    public void 全部節區讀不到_仍給筆數但明說解不開()
    {
        var facts = WheaMemoryFactsService.Collect(
            [Rec(null, 46), Rec(null, 47)], DateTimeOffset.UnixEpoch);
        var s = Fact(facts, WheaMemoryFactsService.SummaryKey);
        Assert.Equal(2d, s.NumericValue!.Value);
        Assert.Equal(FactAvailability.Present, s.Availability);
        Assert.Contains("沒有可解的記憶體節區", s.Value);
        Assert.Contains("記憶體節區 0 筆", s.Value);
        Assert.Contains("測不到", s.Value);
        Assert.Contains("46×1", s.Value);
        Assert.Contains("47×1", s.Value);
        Assert.Contains("只是入口，不是錯誤類型", s.Value);
    }

    [Fact]
    public void 單筆記錄_四張臉都要寫()
    {
        var facts = WheaMemoryFactsService.Collect(
            [Rec(Section(physicalAddress: 0x1000, module: 1, bitPosition: 7, errorType: 0x02,
                moduleHandle: 0xBB))], DateTimeOffset.UnixEpoch);
        var rec = Fact(facts, WheaMemoryFactsService.RecordPrefix + "0001");
        Assert.Equal(FactAvailability.Present, rec.Availability);
        Assert.Null(rec.UnavailableReason);
        Assert.Contains("單一位元 ECC", rec.Value);
        Assert.Contains("實體位址 0x1000", rec.Value);
        Assert.Contains("模組 1", rec.Value);
        Assert.Contains("SMBIOS 模組 handle 0x00BB", rec.Value);
        Assert.Contains("出錯位元 7", rec.Value);
        Assert.Contains("不判斷該換哪一條 RAM", rec.Value);
    }

    [Fact]
    public void ValidBits沒設的欄位不能印成0()
    {
        // ValidBits 只設 ErrorType（bit14），其餘全不設
        var facts = WheaMemoryFactsService.Collect(
            [Rec(Section(validBits: 1UL << 14, module: 0, bitPosition: 0, errorType: 0x03))],
            DateTimeOffset.UnixEpoch);
        var rec = Fact(facts, WheaMemoryFactsService.RecordPrefix + "0001");
        Assert.Contains("實體位址：**韌體沒給**", rec.Value);
        Assert.Contains("**韌體沒給**——不能指到某條 DIMM", rec.Value);
        Assert.Contains("出錯位元：**韌體沒給**", rec.Value);
        Assert.DoesNotContain("模組 0", rec.Value);
        Assert.Contains("多位 ECC", rec.Value);
    }

    [Theory]
    [InlineData(0, "ErrorStatus 0x0000000000010A00")]
    [InlineData(1, "實體位址 0x20000000000001")]
    [InlineData(2, "位址遮罩 0x3F")]
    [InlineData(3, "NUMA 節點 3")]
    [InlineData(4, "card 4")]
    [InlineData(5, "模組 5")]
    [InlineData(6, "bank 6")]
    [InlineData(7, "device 7")]
    [InlineData(8, "row 8")]
    [InlineData(9, "column 9")]
    [InlineData(10, "出錯位元 10")]
    [InlineData(11, "RequesterId 0x1111111111111111")]
    [InlineData(12, "ResponderId 0x2222222222222222")]
    [InlineData(13, "TargetId 0x3333333333333333")]
    [InlineData(14, "類型 單一位元 ECC")]
    [InlineData(15, "rank 15")]
    [InlineData(16, "SMBIOS card handle 0x0016")]
    [InlineData(17, "SMBIOS 模組 handle 0x0017")]
    public void EachValidBitAuthorizesOnlyItsOwnField(int bit, string expected)
    {
        // Distinct nonzero bytes expose accidental printing of invalid neighbouring fields.
        var section = Section(validBits: 1UL << bit, errorStatus: 0x10A00,
            physicalAddress: 0x20000000000001, mask: 0x3F, node: 3, card: 4,
            module: 5, bank: 6, device: 7, row: 8, column: 9, bitPosition: 10,
            errorType: 2, rankNumber: 15, cardHandle: 0x16, moduleHandle: 0x17);
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(48, 8), 0x1111111111111111);
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(56, 8), 0x2222222222222222);
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(64, 8), 0x3333333333333333);
        string[] allFields = ["ErrorStatus 0x0000000000010A00", "實體位址 0x20000000000001",
            "位址遮罩 0x3F", "NUMA 節點 3", "card 4", "模組 5", "bank 6", "device 7",
            "row 8", "column 9", "出錯位元 10", "RequesterId 0x1111111111111111",
            "ResponderId 0x2222222222222222", "TargetId 0x3333333333333333",
            "類型 單一位元 ECC", "rank 15", "SMBIOS card handle 0x0016", "SMBIOS 模組 handle 0x0017"];
        var record = Fact(WheaMemoryFactsService.Collect([Rec(section)], DateTimeOffset.UnixEpoch),
            WheaMemoryFactsService.RecordPrefix + "0001");
        Assert.Contains(expected, record.Value);
        foreach (string other in allFields.Where(value => value != expected))
            Assert.DoesNotContain(other, record.Value);
        Assert.Equal(bit is 1 or 11 or 12 or 13, record.Sensitive);
        Assert.Null(record.NumericValue);
    }

    [Fact]
    public void InvalidErrorTypeCannotCreateDiagnosisOrSummaryClassification()
    {
        var facts = WheaMemoryFactsService.Collect(
            [Rec(Section(validBits: 0, errorType: 2))], DateTimeOffset.UnixEpoch);
        var record = Fact(facts, WheaMemoryFactsService.RecordPrefix + "0001");
        var summary = Fact(facts, WheaMemoryFactsService.SummaryKey);
        Assert.DoesNotContain("單一位元 ECC", record.Value);
        Assert.DoesNotContain("典型處置", record.Value);
        Assert.DoesNotContain("單一位元 ECC", summary.Value);
        Assert.Contains("1 筆的錯誤類型未確認", summary.Value);
    }

    [Fact]
    public void EventOffsetsAreConvertedBeforeLabellingUtc()
    {
        var local = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(8));
        HardwareFact Read(DateTimeOffset when) => Fact(WheaMemoryFactsService.Collect(
            [new WheaMemoryRecord(46, when, Section(), null)], DateTimeOffset.UnixEpoch),
            WheaMemoryFactsService.RecordPrefix + "0001");
        Assert.StartsWith("2026-11-17 04:00:00Z", Read(local).Value);
        Assert.Equal(Read(local.ToUniversalTime()).Value, Read(local).Value);
    }

    [Fact]
    public void PhysicalAddressRemainsExactAndIsRedactedByDefaultExport()
    {
        const ulong address = 0x20000000000001; // Cannot be represented exactly as double.
        var facts = WheaMemoryFactsService.Collect(
            [Rec(Section(validBits: 1UL << 1, physicalAddress: address))], DateTimeOffset.UnixEpoch);
        var record = Fact(facts, WheaMemoryFactsService.RecordPrefix + "0001");
        Assert.Contains("0x20000000000001", record.Value);
        Assert.Null(record.NumericValue);
        Assert.True(record.Sensitive);
        var snapshot = HardwareSnapshotService.Create("3.30", facts, includeSensitive: true);
        string json = HardwareSnapshotService.ToJson(snapshot);
        Assert.DoesNotContain("20000000000001", json);
    }

    [Fact]
    public void TruncatedEnumerationKeepsTheCperDecodingLimitation()
    {
        var at = DateTimeOffset.UnixEpoch;
        var records = new WheaMemoryReadResult(
            [new WheaMemoryRecord(46, at, null, WheaMemoryRecordsReader.BlobNotParsed)],
            WheaMemoryReadStatus.Truncated, at.AddDays(-7), at, "candidate limit");
        var summary = Fact(WheaMemoryFactsService.Collect(records, at), WheaMemoryFactsService.SummaryKey);
        Assert.Equal(FactAvailability.Unknown, summary.Availability);
        Assert.Null(summary.NumericValue);
        Assert.Contains("非完整總數", summary.Value);
        Assert.Contains("沒有可解的記憶體節區", summary.Value);
        Assert.Contains("記憶體節區 0 筆", summary.Value);
        Assert.Contains(WheaMemoryRecordsReader.BlobNotParsed, summary.Value);
        Assert.Contains("哪一條 DIMM", summary.Value);
    }

    [Fact]
    public void 未收錄的錯誤類型碼_不命名()
    {
        var facts = WheaMemoryFactsService.Collect(
            [Rec(Section(errorType: 0x2A))], DateTimeOffset.UnixEpoch);
        var rec = Fact(facts, WheaMemoryFactsService.RecordPrefix + "0001");
        Assert.Contains("0x2A——本版沒有這個碼的出處，不命名", rec.Value);
        var summary = Fact(facts, WheaMemoryFactsService.SummaryKey);
        Assert.Contains("0x2A（**本版沒有這個碼的出處，不命名**）×1", summary.Value);
    }

    [Fact]
    public void 太短的節區進彙總但不逐筆列()
    {
        var facts = WheaMemoryFactsService.Collect(
            [Rec(new byte[40]), Rec(Section())], DateTimeOffset.UnixEpoch);
        var s = Fact(facts, WheaMemoryFactsService.SummaryKey);
        Assert.Contains("1 筆的節區不足 80 位元組", s.Value);
        Assert.Single(facts, f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix));
    }

    [Fact]
    public void 超過上限時只列最近16筆並在彙總說明()
    {
        var records = Enumerable.Range(0, 20)
            .Select(i => Rec(Section(errorType: 0x02), daysAgo: i)).ToList();
        var facts = WheaMemoryFactsService.Collect(records, DateTimeOffset.UnixEpoch);
        Assert.Equal(16, facts.Count(f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix)));
        var s = Fact(facts, WheaMemoryFactsService.SummaryKey);
        Assert.Contains("只逐筆列出最近 16 筆", s.Value);
    }

    [Fact]
    public void 彙總要給模組與位址的覆蓋數()
    {
        var records = new List<WheaMemoryRecord>
        {
            Rec(Section(physicalAddress: 0x10, module: 1)),
            Rec(Section(validBits: 1UL << 14)),   // 沒有位址也沒有模組
        };
        var s = Fact(WheaMemoryFactsService.Collect(records, DateTimeOffset.UnixEpoch),
            WheaMemoryFactsService.SummaryKey);
        Assert.Contains("2 筆 WHEA-Logger 候選事件", s.Value);
        Assert.Contains("記憶體節區 2 筆", s.Value);
        Assert.Equal(2d, s.NumericValue);
        Assert.Contains("1 筆韌體有給實體位址", s.Value);
        Assert.Contains("1 筆有給模組資訊", s.Value);
    }

    [Fact]
    public void 界線宣告_五條都要在()
    {
        var f = Fact(WheaMemoryFactsService.Collect([], DateTimeOffset.UnixEpoch),
            WheaMemoryFactsService.BoundaryKey);
        Assert.Contains("只解記憶體節區", f.Value);
        Assert.Contains("Extended", f.Value);
        Assert.Contains("韌體沒填的欄位不印成 0", f.Value);
        Assert.Contains("保留期", f.Value);
        Assert.Contains("不判斷該換哪一條 RAM", f.Value);
    }

    [Fact]
    public void 記錄讀取器的候選ID與窗_要和事件事實一致()
    {
        Assert.Equal(EventIdEvidenceFactsService.WindowDays, WheaMemoryRecordsReader.WindowDays);
        Assert.Equal(new[] { 1, 17, 18, 19, 23, 46, 47 }, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(1, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(18, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(17, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(19, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(23, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(46, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(47, WheaMemoryRecordsReader.CandidateEventIds);
    }

    [Fact]
    public void 讀取器沒有可驗證外層時_理由要說出來()
    {
        Assert.Contains("未提供可驗證", WheaMemoryRecordsReader.BlobNotParsed);
        Assert.Contains("RawData 外層", WheaMemoryRecordsReader.BlobNotParsed);
        var summary = Fact(WheaMemoryFactsService.Collect([new(46, DateTimeOffset.UnixEpoch, null, WheaMemoryRecordsReader.BlobNotParsed)], DateTimeOffset.UnixEpoch), WheaMemoryFactsService.SummaryKey);
        Assert.Equal(1d, summary.NumericValue);
        Assert.Contains(WheaMemoryRecordsReader.BlobNotParsed, summary.Value);
        Assert.Contains("記憶體節區 0 筆", summary.Value);
    }
}
