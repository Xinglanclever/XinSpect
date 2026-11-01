using Xunit;

namespace XinSpect.Tests;

public class SecurityScoreTests
{
    // Helper to build a default "all good" SurfaceFacts
    private static SecurityScoreEngine.SurfaceFacts GoodSurface() => new(
        DefenderRealtimeOn: true,
        FirewallDomain: true,
        FirewallPublic: true,
        FirewallPrivate: true,
        RdpDisabled: true,
        AutoLogonOff: true,
        GuestDisabled: true,
        AdminSafe: true,
        PsRestricted: true,
        WshDisabled: true,
        RdpNlaRequired: true,
        SmbV1Disabled: true,
        WinRmStopped: true,
        PrintSpoolerStopped: true,
        DaysSinceUpdate: 5
    );

    private static SecurityScoreEngine.SurfaceFacts NullSurface() => new(
        DefenderRealtimeOn: null,
        FirewallDomain: null,
        FirewallPublic: null,
        FirewallPrivate: null,
        RdpDisabled: null,
        AutoLogonOff: null,
        GuestDisabled: null,
        AdminSafe: null,
        PsRestricted: null,
        WshDisabled: null,
        RdpNlaRequired: null,
        SmbV1Disabled: null,
        WinRmStopped: null,
        PrintSpoolerStopped: null,
        DaysSinceUpdate: null
    );

    [Fact]
    public void AllGood_ScoresHigh()
    {
        var dma = new SecurityScoreEngine.DmaFacts(
            HvciEnabled: true, VbsRunning: true, IommuAvailable: true,
            DmaProtection: true, CredentialGuard: true,
            BitLockerEnabled: true, BitLockerDmaProtection: true,
            ThunderboltSecurityLevel: 3, DmaEnforced: true);
        var fw = new SecurityScoreEngine.FirmwareFacts(
            SecureBootEnabled: true, SecureBootInSetupMode: false,
            TestSigningEnabled: false, SpiWriteProtected: true,
            MeManufacturingMode: false, MeasuredBootPresent: true,
            MeVersion: "16.0.0",
            TpmPresent: true, TpmVersion: "2.0",
            UefiBoot: true, BiosPasswordSet: true);
        var cpu = new SecurityScoreEngine.CpuFacts(
            SpecMitigationsEnabled: true, NxEnabled: true,
            DepEnabled: true, AslrHighEntropy: true,
            CfgEnabled: true, CetEnabled: true,
            SmepEnabled: true, SmapEnabled: true,
            LsassPplEnabled: true, SpecOverrideSafe: true);
        var stor = new SecurityScoreEngine.StorageFacts(
            BitLockerEnabled: true, NvmeFirmwareBaseline: true,
            SmartHealthy: true, BitLockerEscrow: true,
            EncryptionMethod: "XTS-AES-256");
        var drv = new SecurityScoreEngine.DriverFacts(
            TotalDrivers: 120, UnsignedDrivers: 0, KnownVulnerableDrivers: 0,
            VulnerableDriverBlocklistEnabled: true, LsassProtected: true,
            WdacActive: true, KernelDebugOff: true,
            CoInstallersDisabled: true, StaleDriverCertsClean: true);

        var result = SecurityScoreEngine.Evaluate(dma, fw, cpu, stor, drv, GoodSurface());
        Assert.InRange(result.TotalScore, 85, 100);
    }

    [Fact]
    public void AllBad_ScoresLow()
    {
        var dma = new SecurityScoreEngine.DmaFacts(
            HvciEnabled: false, VbsRunning: false, IommuAvailable: false,
            DmaProtection: false, CredentialGuard: false,
            BitLockerEnabled: false, BitLockerDmaProtection: false,
            ThunderboltSecurityLevel: 0, DmaEnforced: false);
        var fw = new SecurityScoreEngine.FirmwareFacts(
            SecureBootEnabled: false, SecureBootInSetupMode: true,
            TestSigningEnabled: true, SpiWriteProtected: false,
            MeManufacturingMode: true, MeasuredBootPresent: false,
            MeVersion: null,
            TpmPresent: false, TpmVersion: null,
            UefiBoot: false, BiosPasswordSet: false);
        var cpu = new SecurityScoreEngine.CpuFacts(
            SpecMitigationsEnabled: false, NxEnabled: false,
            DepEnabled: false, AslrHighEntropy: false,
            CfgEnabled: false, CetEnabled: false,
            SmepEnabled: false, SmapEnabled: false,
            LsassPplEnabled: false, SpecOverrideSafe: false);
        var stor = new SecurityScoreEngine.StorageFacts(
            BitLockerEnabled: false, NvmeFirmwareBaseline: false,
            SmartHealthy: false, BitLockerEscrow: false,
            EncryptionMethod: "None");
        var drv = new SecurityScoreEngine.DriverFacts(
            TotalDrivers: 100, UnsignedDrivers: 5, KnownVulnerableDrivers: 2,
            VulnerableDriverBlocklistEnabled: false, LsassProtected: false,
            WdacActive: false, KernelDebugOff: false,
            CoInstallersDisabled: false, StaleDriverCertsClean: false);
        var sfc = new SecurityScoreEngine.SurfaceFacts(
            DefenderRealtimeOn: false, FirewallDomain: false,
            FirewallPublic: false, FirewallPrivate: false,
            RdpDisabled: false, AutoLogonOff: false,
            GuestDisabled: false, AdminSafe: false,
            PsRestricted: false, WshDisabled: false,
            RdpNlaRequired: false, SmbV1Disabled: false,
            WinRmStopped: false, PrintSpoolerStopped: false,
            DaysSinceUpdate: 90);

        var result = SecurityScoreEngine.Evaluate(dma, fw, cpu, stor, drv, sfc);
        Assert.InRange(result.TotalScore, 0, 25);
    }

    [Fact]
    public void AllNull_NotScored_NoGapVerdict()
    {
        var dma = new SecurityScoreEngine.DmaFacts(
            null, null, null, null, null, null, null, null, null);
        var fw = new SecurityScoreEngine.FirmwareFacts(
            null, null, null, null, null, null, null,
            null, null, null, null);
        var cpu = new SecurityScoreEngine.CpuFacts(
            null, null, null, null, null, null, null, null, null, null);
        var stor = new SecurityScoreEngine.StorageFacts(
            null, null, null, null, null);
        var drv = new SecurityScoreEngine.DriverFacts(
            0, 0, 0, null, null, null, null, null, null);

        var result = SecurityScoreEngine.Evaluate(dma, fw, cpu, stor, drv, NullSurface());
        // 讀不到 ≠ 有缺口：全 null 不可以被算成「50 分的爛機器」，
        // 而是「沒有資料，所以不給分」。這正是防護頁被抱怨的那個問題。
        Assert.Equal(0, result.TotalScore);
        Assert.False(result.HasScore, "全未評估時不該給總分");
        Assert.True(result.CoveragePercent < 50,
            $"覆蓋率應該很低，實際 {result.CoveragePercent}%");
        Assert.Contains("資料不足", result.Verdict);
        Assert.DoesNotContain("缺口", result.Verdict);
        // DMA 只有「有沒有 Thunderbolt 控制器」這一項算量到（沒有＝確認過），
        // 其餘五條防線應該是 0：全部讀不到。
        foreach (var c in result.Categories)
            Assert.Equal(c.Id == "dma" ? 10 : 0, c.AssessedWeight);
    }

    [Fact]
    public void UnknownItems_DoNotDragScoreDown()
    {
        // 同一台機器，量到的那一項是好是壞才是分數的依據；
        // 讀不到的項目既不該加分，也不該扣分。
        var dma = new SecurityScoreEngine.DmaFacts(
            HvciEnabled: true, VbsRunning: null, IommuAvailable: null,
            DmaProtection: null, CredentialGuard: null,
            BitLockerEnabled: null, BitLockerDmaProtection: null,
            ThunderboltSecurityLevel: null, DmaEnforced: null);
        var fw = new SecurityScoreEngine.FirmwareFacts(
            null, null, null, null, null, null, null, null, null, null, null);
        var cpu = new SecurityScoreEngine.CpuFacts(
            null, null, null, null, null, null, null, null, null, null);
        var stor = new SecurityScoreEngine.StorageFacts(null, null, null, null, null);
        var drv = new SecurityScoreEngine.DriverFacts(
            0, 0, 0, null, null, null, null, null, null);

        var result = SecurityScoreEngine.Evaluate(dma, fw, cpu, stor, drv, NullSurface());
        var dmaLine = result.Categories.First(c => c.Id == "dma");

        Assert.True(dmaLine.AssessedWeight > 0);
        Assert.Equal(100, dmaLine.Score);   // 量到的那一項是好的 → 在量得到的範圍內滿分
        Assert.True(result.CoveragePercent <= 40,
            $"只量到一項時覆蓋率應該很低，實際 {result.CoveragePercent}%");
    }

    [Fact]
    public void WeightsSumTo100()
    {
        var dma = new SecurityScoreEngine.DmaFacts(
            true, true, true, true, true, true, true, null, true);
        var fw = new SecurityScoreEngine.FirmwareFacts(
            true, false, false, true, false, true, null,
            true, "2.0", true, null);
        var cpu = new SecurityScoreEngine.CpuFacts(
            true, true, true, true, true, true, true, true, true, true);
        var stor = new SecurityScoreEngine.StorageFacts(
            true, true, true, true, "XTS-AES-256");
        var drv = new SecurityScoreEngine.DriverFacts(
            50, 0, 0, true, true, true, true, true, true);

        var result = SecurityScoreEngine.Evaluate(dma, fw, cpu, stor, drv, GoodSurface());
        int weightSum = 0;
        foreach (var c in result.Categories)
            weightSum += c.Weight;
        Assert.Equal(100, weightSum);
    }

    [Fact]
    public void SixDefenseLines()
    {
        var dma = new SecurityScoreEngine.DmaFacts(
            true, true, true, true, true, true, true, null, true);
        var fw = new SecurityScoreEngine.FirmwareFacts(
            true, false, false, null, null, null, null,
            true, "2.0", true, null);
        var cpu = new SecurityScoreEngine.CpuFacts(
            true, true, true, null, null, null, null, null, null, true);
        var stor = new SecurityScoreEngine.StorageFacts(
            null, null, true, null, null);
        var drv = new SecurityScoreEngine.DriverFacts(
            50, 0, 0, true, null, null, null, null, null);

        var result = SecurityScoreEngine.Evaluate(dma, fw, cpu, stor, drv, GoodSurface());
        Assert.Equal(6, result.Categories.Count);

        // Verify line ids
        var ids = new HashSet<string>();
        foreach (var c in result.Categories)
            ids.Add(c.Id);
        Assert.Contains("dma", ids);
        Assert.Contains("firmware", ids);
        Assert.Contains("cpu", ids);
        Assert.Contains("storage", ids);
        Assert.Contains("drivers", ids);
        Assert.Contains("surface", ids);
    }

    [Fact]
    public void SurfaceLine_HasWeight30()
    {
        var dma = new SecurityScoreEngine.DmaFacts(
            true, true, true, true, true, true, true, null, true);
        var fw = new SecurityScoreEngine.FirmwareFacts(
            true, false, false, null, null, null, null,
            true, "2.0", true, null);
        var cpu = new SecurityScoreEngine.CpuFacts(
            true, true, true, null, null, null, null, null, null, true);
        var stor = new SecurityScoreEngine.StorageFacts(
            null, null, true, null, null);
        var drv = new SecurityScoreEngine.DriverFacts(
            50, 0, 0, true, null, null, null, null, null);

        var result = SecurityScoreEngine.Evaluate(dma, fw, cpu, stor, drv, GoodSurface());
        SecurityCategoryScore? surface = null;
        foreach (var c in result.Categories)
            if (c.Id == "surface") surface = c;
        Assert.NotNull(surface);
        Assert.Equal(30, surface.Weight);
    }

    [Fact]
    public void NullChecks_AreExcludedFromDenominator()
    {
        // 一條防線全部讀不到時：該防線 AssessedWeight=0（不是「50 分」），
        // 也不參與總分的加權。
        var dma = new SecurityScoreEngine.DmaFacts(
            null, null, null, null, null, null, null, null, null);
        var fw = new SecurityScoreEngine.FirmwareFacts(
            true, false, false, true, false, true, null,
            true, "2.0", true, true);
        var cpu = new SecurityScoreEngine.CpuFacts(
            true, true, true, true, true, true, true, true, true, true);
        var stor = new SecurityScoreEngine.StorageFacts(
            true, true, true, true, "XTS-AES-256");
        var drv = new SecurityScoreEngine.DriverFacts(
            50, 0, 0, true, true, true, true, true, true);

        var result = SecurityScoreEngine.Evaluate(dma, fw, cpu, stor, drv, GoodSurface());
        var dmaLine = result.Categories.First(c => c.Id == "dma");
        // DMA 只剩「有沒有 Thunderbolt」這一項是量得到的（沒有控制器＝已確認），
        // 其餘八項讀不到，不進分母。
        Assert.Equal(10, dmaLine.AssessedWeight);
        // 其他五條防線都是滿的，所以總分應該是高分，而不是被 null 拖下來
        Assert.InRange(result.TotalScore, 80, 100);
        Assert.True(result.CoveragePercent >= 80);
    }
}
