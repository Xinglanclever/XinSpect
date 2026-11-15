using Xunit;

namespace XinSpect.Tests;

public class BcdAccuracyTests
{
    // Microsoft BCDEdit reference: /enum all includes inherit objects, unlike the default ACTIVE view.
    private const string All = """
        Windows Boot Manager
        --------------------
        identifier              {bootmgr}
        default                 {current}

        Windows Boot Loader
        --------------------
        identifier              {current}
        device                  vhd=[C:]\Virtual Machines\Windows.vhdx
        osdevice                vhd=[C:]\Virtual Machines\Windows.vhdx
        path                    \Custom Boot\winload.efi
        removememory            1024
        truncatememory          0x100000000
        badmemorylist           0xDEAD

        RAM Defects
        --------------------
        identifier              {badmemory}
        badmemorylist           0x1234
                                0x5678

        Firmware Boot Manager
        --------------------
        identifier              {fwbootmgr}
        displayorder            {network}

        Firmware Application
        --------------------
        identifier              {network}
        description             UEFI Network Boot
        """;

    private static HardwareFact Get(string text, string key) =>
        BcdElementsFactsService.CollectAll(text, null, DateTimeOffset.UnixEpoch).Single(f => f.Key == key);

    [Fact]
    public void BadMemoryUsesGlobalObjectAndStatesPfaLimit()
    {
        var fact = Get(All, BcdElementsFactsService.BadMemoryKey);
        Assert.Equal(2d, fact.NumericValue);
        Assert.Contains("0x1234 0x5678", fact.Value);
        Assert.DoesNotContain("0xDEAD", fact.Value);
        Assert.Contains("WHEA\\BadPages", fact.Value);
        Assert.DoesNotContain("沒有標記任何", fact.Value);
    }

    [Fact]
    public void TruncateIsByteAddressLimitAndRemoveIsMegabytes()
    {
        string value = Get(All, BcdElementsFactsService.MemTrimKey).Value;
        Assert.Contains("removememory：1024 MB", value);
        Assert.Contains("truncatememory：0x100000000 bytes（實體位址上限）", value);
    }

    [Fact]
    public void DevicePathsKeepEmbeddedSpaces()
    {
        string value = Get(All, BcdElementsFactsService.LoaderDeviceKey).Value;
        Assert.Contains(@"vhd=[C:]\Virtual Machines\Windows.vhdx", value);
        Assert.Contains(@"\Custom Boot\winload.efi", value);
    }

    [Fact]
    public void FirmwareNamesComeFromFirmwareObjects() =>
        Assert.Contains("UEFI Network Boot", Get(All, BcdElementsFactsService.FwBootMgrKey).Value);

    [Theory]
    [InlineData("")]
    [InlineData("The boot configuration data store could not be opened.")]
    [InlineData("identifier              {unrelated}")]
    public void MissingObjectsCannotBecomeAbsentSettings(string output)
    {
        var facts = BcdElementsFactsService.CollectAll(output, null, DateTimeOffset.UnixEpoch);
        Assert.All(facts.Where(f => f.Key != BcdElementsFactsService.AccessStatementKey),
            f => Assert.Equal(FactAvailability.Unknown, f.Availability));
        Assert.Equal(3, BootOrderFactsService.Collect(output, null, DateTimeOffset.UnixEpoch).Count);
    }

    [Fact]
    public void MissingLoaderDoesNotClaimNxOrMemoryPolicy()
    {
        string missingLoader = All.Replace("identifier              {current}", "identifier              {other}");
        foreach (string key in new[] { BcdElementsFactsService.NxKey, BcdElementsFactsService.HvLaunchKey, BcdElementsFactsService.MemTrimKey })
            Assert.Equal(FactAvailability.Unknown, Get(missingLoader, key).Availability);
        Assert.Equal(FactAvailability.Present, Get(missingLoader, BcdElementsFactsService.BadMemoryKey).Availability);
    }

    [Fact]
    public void MissingFieldIsExplicitlyNotEffectivePolicy()
    {
        var fact = Get(All, BcdElementsFactsService.NxKey);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("繼承", fact.Value);
        Assert.Contains("不代表有效", fact.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(null)]
    public void FailedProcessWithStdoutIsRejected(int? exitCode)
    {
        var result = BootOrderReader.Interpret(new ProcessRunResult("Access is denied.", "", exitCode, false, null), "/enum all");
        Assert.Null(result.Output);
        Assert.Contains("Access is denied", result.Reason);
    }

    [Fact]
    public void SuccessfulProcessRetainsOutput() =>
        Assert.Equal(All, BootOrderReader.Interpret(new ProcessRunResult(All, "", 0, false, null), "/enum all").Output);
}
