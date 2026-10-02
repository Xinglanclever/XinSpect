using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 後端與環境事實的單元測試：純函式 <see cref="BackendEnvironmentService.BuildFacts"/> 釘全矩陣，
/// <see cref="BackendEnvironmentService.Collect"/> 用假讀取器＋注入探測釘接線。不碰真實 SCM／登錄檔／ntdll。
/// </summary>
public class BackendEnvironmentTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static BackendEnvironmentInputs Inputs(
        bool msrAvailable = true, string? msrName = "WinRing0", string? msrReason = null,
        bool mmioAvailable = true, string? mmioName = "WinRing0 實體記憶體", string? mmioReason = null,
        uint? ciOptions = 0x0001, int? secureBoot = 0, string? pawnIo = "not-installed") =>
        new(msrAvailable, msrName, msrReason, mmioAvailable, mmioName, mmioReason, ciOptions, secureBoot, pawnIo);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        Assert.Single(facts, f => f.Key == key);

    [Fact]
    public void 全部可用時七項事實齊且值正確()
    {
        var facts = BackendEnvironmentService.BuildFacts(Inputs(pawnIo: "running"), At);

        Assert.Equal(7, facts.Count);
        Assert.Equal("WinRing0", Fact(facts, "backend.msr").Value);
        Assert.Equal("WinRing0 實體記憶體", Fact(facts, "backend.mmio").Value);
        Assert.Equal("關閉", Fact(facts, "platform.hvci").Value);
        Assert.Equal("關閉", Fact(facts, "platform.testsigning").Value);
        Assert.Equal("關閉", Fact(facts, "platform.secure_boot").Value);
        Assert.Equal("服務執行中", Fact(facts, "platform.pawnio").Value);
        Assert.Contains("WinRing0 為主力", Fact(facts, "backend.environment_decision").Value);
        Assert.All(facts, f => Assert.Equal(FactAvailability.Present, f.Availability));
    }

    [Fact]
    public void 全部落空時三態帶原因且不帶數值()
    {
        var facts = BackendEnvironmentService.BuildFacts(Inputs(
            msrAvailable: false, msrName: null, msrReason: "橋接掛了",
            mmioAvailable: false, mmioName: null, mmioReason: "兩個後端都不行",
            ciOptions: null, secureBoot: null, pawnIo: null), At);

        var msr = Fact(facts, "backend.msr");
        Assert.Equal(FactAvailability.NotSupported, msr.Availability);
        Assert.Equal("橋接掛了", msr.UnavailableReason);
        Assert.Null(msr.NumericValue);

        var mmio = Fact(facts, "backend.mmio");
        Assert.Equal(FactAvailability.NotSupported, mmio.Availability);
        Assert.Equal("兩個後端都不行", mmio.UnavailableReason);
        Assert.Null(mmio.NumericValue);

        Assert.Equal(FactAvailability.ReadError, Fact(facts, "platform.hvci").Availability);
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "platform.testsigning").Availability);
        Assert.Equal(FactAvailability.NotSupported, Fact(facts, "platform.secure_boot").Availability);
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "platform.pawnio").Availability);
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "backend.environment_decision").Availability);

        // 誠實不變式：讀不到的事實不得帶數值。
        Assert.All(facts, f => { if (f.Availability != FactAvailability.Present) Assert.Null(f.NumericValue); });
    }

    [Fact]
    public void 環境矩陣裁決依HVCI狀態分岔()
    {
        var hvciOff = Fact(BackendEnvironmentService.BuildFacts(Inputs(ciOptions: 0x0001), At), "backend.environment_decision");
        Assert.Contains("HVCI 關閉", hvciOff.Value);
        Assert.Contains("WinRing0", hvciOff.Value);

        var hvciOn = Fact(BackendEnvironmentService.BuildFacts(Inputs(ciOptions: 0x0401), At), "backend.environment_decision");
        Assert.Contains("HVCI 開啟", hvciOn.Value);
        Assert.Contains("PawnIO", hvciOn.Value);
    }

    [Fact]
    public void 測試簽章開啟走警示色關閉不走()
    {
        var on = Fact(BackendEnvironmentService.BuildFacts(Inputs(ciOptions: 0x0003), At), "platform.testsigning");
        Assert.StartsWith("測試簽章模式開啟", on.Value);
        Assert.True(EvidenceFactRow.From(on).IsWarning);

        var off = Fact(BackendEnvironmentService.BuildFacts(Inputs(ciOptions: 0x0001), At), "platform.testsigning");
        Assert.Equal("關閉", off.Value);
        Assert.False(EvidenceFactRow.From(off).IsWarning);
    }

    [Fact]
    public void CodeIntegrity位元與解碼器旗標表一致()
    {
        // 單一齣處守則：本服務的位元常數必須與 PlatformTrustDecoder 的旗標表同名同值。
        Assert.Equal(0x0002u, PlatformTrustDecoder.CodeIntegrityFlags.First(f => f.Name.Contains("testsigning")).Flag);
        Assert.Equal(0x0400u, PlatformTrustDecoder.CodeIntegrityFlags.First(f => f.Name.Contains("HVCI 核心模式已啟用")).Flag);
        Assert.Equal(0x0002u, BackendEnvironmentService.CiFlagTestSigning);
        Assert.Equal(0x0400u, BackendEnvironmentService.CiFlagHvciKmci);
    }

    [Fact]
    public void 收集端把讀取器後端名與探測結果帶進事實()
    {
        var msr = new FakeMsr(available: true, backendName: "假 MSR");
        var mmio = new FakeMmio(available: false, backendName: null, reason: "握手失敗");

        var facts = BackendEnvironmentService.Collect(msr, mmio, At,
            codeIntegrityProbe: () => 0x0001, secureBootProbe: () => 1, pawnIoProbe: () => "running");

        Assert.Equal("假 MSR", Fact(facts, "backend.msr").Value);
        var mmioFact = Fact(facts, "backend.mmio");
        Assert.Equal(FactAvailability.NotSupported, mmioFact.Availability);
        Assert.Equal("握手失敗", mmioFact.UnavailableReason);
        Assert.Equal("開啟", Fact(facts, "platform.secure_boot").Value);
        Assert.Equal("服務執行中", Fact(facts, "platform.pawnio").Value);
    }

    [Fact]
    public void 事實鍵全數符合鍵名規則()
    {
        var facts = BackendEnvironmentService.BuildFacts(Inputs(ciOptions: null, secureBoot: null, pawnIo: null), At);
        var rx = new Regex("^[a-z0-9](?:[a-z0-9._\\-\\[\\]]{0,159})$");
        Assert.All(facts, f => Assert.Matches(rx, f.Key));
    }

    private sealed class FakeMsr(bool available, string? backendName) : IKernelMsrReader
    {
        public bool Available { get; } = available;
        public string? UnavailableReason => Available ? null : "假 MSR 不可用";
        public string? BackendName { get; } = backendName;
        public ulong? ReadMsr(uint index) => null;
    }

    private sealed class FakeMmio(bool available, string? backendName, string reason) : IMmioReader
    {
        public bool Available { get; } = available;
        public string? UnavailableReason => Available ? null : reason;
        public string? BackendName { get; } = backendName;
        public byte[]? ReadBlock(ulong physicalAddress, int length) => null;
    }
}
