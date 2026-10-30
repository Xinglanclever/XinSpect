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
        Assert.Contains("顆粒", WheaMemorySectionDecoder.ErrorTypeHint(0x03)!);
        Assert.Contains("控制器", WheaMemorySectionDecoder.ErrorTypeHint(0x09)!);
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
        Assert.Contains("一筆的節區都解不開", s.Value);
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
        Assert.Single(facts.Where(f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix)));
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
        Assert.Contains("2 筆 WHEA 記憶體記錄", s.Value);
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
        Assert.Contains(17, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(19, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(23, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(46, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.Contains(47, WheaMemoryRecordsReader.CandidateEventIds);
    }

    [Fact]
    public void 讀取器不拆blob_理由要說出來()
    {
        Assert.Contains("不去拆 blob", WheaMemoryRecordsReader.BlobNotParsed);
        Assert.Contains("沒有可核的出處", WheaMemoryRecordsReader.BlobNotParsed);
    }
}
