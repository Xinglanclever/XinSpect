using Xunit;

namespace XinSpect.Tests;

[CollectionDefinition("Hardware operation policy", DisableParallelization = true)]
public sealed class HardwareOperationPolicyCollection
{
    public const string Name = "Hardware operation policy";
}

[Collection(HardwareOperationPolicyCollection.Name)]
public sealed class HardwareOperationGateTests
{
    [Fact]
    public void 預設關閉且必須依序開啟兩級()
    {
        var settings = new SettingsService(loadFromDisk: false);
        HardwareOperationGate.Bind(settings);
        try
        {
            Assert.False(settings.AdvancedDiagnosticsEnabled);
            Assert.False(settings.HighRiskHardwareOperationsEnabled);
            Assert.False(HardwareOperationGate.CanRunAdvancedDiagnostics);
            Assert.False(HardwareOperationGate.CanWriteHardware);

            settings.HighRiskHardwareOperationsEnabled = true;
            Assert.False(settings.HighRiskHardwareOperationsEnabled);
            Assert.False(HardwareOperationGate.CanWriteHardware);

            settings.AdvancedDiagnosticsEnabled = true;
            Assert.True(HardwareOperationGate.CanRunAdvancedDiagnostics);
            Assert.False(HardwareOperationGate.CanWriteHardware);

            settings.HighRiskHardwareOperationsEnabled = true;
            Assert.True(HardwareOperationGate.CanWriteHardware);

            settings.AdvancedDiagnosticsEnabled = false;
            Assert.False(settings.HighRiskHardwareOperationsEnabled);
            Assert.False(HardwareOperationGate.CanWriteHardware);
        }
        finally
        {
            HardwareOperationGate.Bind(null);
        }
    }

    [Fact]
    public void 關閉模式會拒絕並稽核寫入且允許中的操作可完成還原()
    {
        var settings = new SettingsService(loadFromDisk: false);
        HardwareOperationGate.Bind(settings);
        WriteGate.Reset();
        try
        {
            Assert.False(WriteGate.TryAuthorizeHardwareWrite("MSR 0x38D", "test", "PMU setup"));
            Assert.Contains("未執行", Assert.Single(WriteGate.Session).Detail, StringComparison.Ordinal);

            settings.AdvancedDiagnosticsEnabled = true;
            settings.HighRiskHardwareOperationsEnabled = true;
            using (HardwareOperationGate.TryBeginHighRiskOperation())
            {
                settings.AdvancedDiagnosticsEnabled = false;
                Assert.False(settings.HighRiskHardwareOperationsEnabled);
                Assert.True(HardwareOperationGate.CanWriteHardware);
                Assert.True(WriteGate.TryAuthorizeHardwareWrite("MSR 0x38D", "test", "restore"));
            }

            Assert.False(HardwareOperationGate.CanWriteHardware);
        }
        finally
        {
            WriteGate.Reset();
            HardwareOperationGate.Bind(null);
        }
    }

    [Fact]
    public void 公開SMBus與iMC適配器不能啟用協定寫入豁免()
    {
        Assert.Single(typeof(WinRing0SmbusIo).GetConstructors());
        Assert.Single(typeof(WinRing0PciConfig).GetConstructors());
        Assert.Single(typeof(WinRing0SmbusIo).GetConstructors()[0].GetParameters());
        Assert.Single(typeof(WinRing0PciConfig).GetConstructors()[0].GetParameters());
    }
}
