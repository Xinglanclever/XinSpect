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

    [Fact]
    public void PublicAmdPciAdapterRejectsWritesBeforeOpeningHardware()
    {
        HardwareOperationGate.Bind(new SettingsService(loadFromDisk: false));
        WriteGate.Reset();
        try
        {
            Assert.False(new AmdSmuService.WinRing0PciAccess().WritePci(0, 0, 0, 4, uint.MaxValue));
            Assert.False(new AmdSmuService.WinRing0PciAccess().WritePci(0, 0, 0, 0xB8, 0x59800));
            Assert.Equal(2, WriteGate.Session.Count);
            Assert.All(WriteGate.Session, entry => Assert.False(entry.Succeeded));
            Assert.True(AmdSmuService.IsReadOnlySmnIndex(0xB8, 0x59800));
            Assert.True(AmdSmuService.IsReadOnlySmnIndex(0x60, 0x50000));
            Assert.False(AmdSmuService.IsReadOnlySmnIndex(0xBC, 0x59800));
            Assert.False(AmdSmuService.IsReadOnlySmnIndex(0xB8, 0x12345678));
        }
        finally { WriteGate.Reset(); HardwareOperationGate.Bind(null); }
    }

    [Fact]
    public async Task PersistedStableProfileRequiresCurrentAuthorization()
    {
        HardwareOperationGate.Bind(new SettingsService(loadFromDisk: false));
        WriteGate.Reset();
        try
        {
            using var service = new OverclockService(new OcSettings());
            await service.ApplyLastStable();
            Assert.Contains("第二級", service.ActionStatus);
            Assert.False(HardwareOperationGate.CanWriteHardware);
            Assert.False(Assert.Single(WriteGate.Session).Succeeded);
            using (HardwareOperationGate.BeginRecoveryOperation())
            {
                await service.ApplyLastStable();
                Assert.Contains("第二級", service.ActionStatus);
                Assert.Equal(2, WriteGate.Session.Count);
                Assert.All(WriteGate.Session, entry => Assert.False(entry.Succeeded));
            }
        }
        finally { WriteGate.Reset(); HardwareOperationGate.Bind(null); }
    }

    [Fact]
    public void DramSessionRestoresSelectorsAndCounterValues()
    {
        WithHardwareMode(settings =>
        {
            var msr = new FakeDramMsr();
            var originals = new Dictionary<uint, ulong>(msr.Values);
            var result = DramTrafficService.MeasureCounters(msr, () =>
            {
                msr.Values[0xC1] = 12;
                msr.Values[0xC2] = 24;
                return 1024;
            }, cpuidProbe: IntelPmu);
            Assert.Equal(12UL, result.Misses);
            Assert.Equal(24UL, result.References);
            Assert.All(originals, item => Assert.Equal(item.Value, msr.Values[item.Key]));
        });
    }

    [Fact]
    public void RevocationDuringSetupAllowsOnlyRestoration()
    {
        WithHardwareMode(settings =>
        {
            var msr = new FakeDramMsr();
            var originals = new Dictionary<uint, ulong>(msr.Values);
            bool workloadRan = false;
            msr.AfterWrite = () => settings.AdvancedDiagnosticsEnabled = false;
            Assert.Throws<OperationCanceledException>(() => DramTrafficService.MeasureCounters(msr, () =>
            {
                workloadRan = true;
                return 1024;
            }, cpuidProbe: IntelPmu));
            Assert.False(workloadRan);
            Assert.Equal((0x38Fu, originals[0x38F] & ~3UL), msr.Writes[0]);
            Assert.Equal(7, msr.Writes.Count); // One setup attempt, then the complete six-write restoration.
            Assert.All(originals, item => Assert.Equal(item.Value, msr.Values[item.Key]));
            Assert.False(HardwareOperationGate.CanWriteHardware);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingBaselineOrBusyCountersNeverStartWrites(bool missingBaseline)
    {
        WithHardwareMode(settings =>
        {
            var msr = new FakeDramMsr();
            if (missingBaseline) msr.Values.Remove(0xC2);
            else msr.Values[0x38F] |= 1;
            Assert.Throws<InvalidOperationException>(() => DramTrafficService.MeasureCounters(msr, () => 1024,
                cpuidProbe: IntelPmu));
            Assert.Empty(msr.Writes);
        });
    }

    [Fact]
    public void CancelledDramSessionNeverStartsWrites()
    {
        WithHardwareMode(settings =>
        {
            var msr = new FakeDramMsr();
            Assert.Throws<OperationCanceledException>(() => DramTrafficService.MeasureCounters(msr,
                () => 1024, new CancellationToken(canceled: true), IntelPmu));
            Assert.Empty(msr.Writes);
        });
    }

    [Fact]
    public void FailedRestorationCannotProduceSuccessfulMeasurement()
    {
        WithHardwareMode(settings =>
        {
            var msr = new FakeDramMsr();
            Assert.Throws<InvalidOperationException>(() => DramTrafficService.MeasureCounters(msr, () =>
            {
                msr.FailWrites = true;
                return 1024;
            }, cpuidProbe: IntelPmu));
            Assert.Equal(12, msr.Writes.Count);
        });
    }

    [Theory]
    [InlineData("vendor")]
    [InlineData("maxLeaf")]
    [InlineData("version")]
    [InlineData("counters")]
    [InlineData("narrow")]
    [InlineData("wide")]
    [InlineData("eventVector")]
    [InlineData("reference")]
    [InlineData("miss")]
    public void UnsupportedDramPmuNeverAccessesMsrs(string unsupported)
    {
        WithHardwareMode(settings =>
        {
            var msr = new FakeDramMsr();
            (uint Eax, uint Ebx, uint Ecx, uint Edx) Probe(uint leaf)
            {
                var value = IntelPmu(leaf);
                if (leaf == 0)
                {
                    if (unsupported == "vendor") value.Ebx = 0x68747541;
                    if (unsupported == "maxLeaf") value.Eax = 9;
                }
                else if (leaf == 0xA)
                {
                    value.Eax = unsupported switch
                    {
                        "version" => (value.Eax & ~0xFFu) | 1,
                        "counters" => (value.Eax & ~0xFF00u) | (1u << 8),
                        "narrow" => (value.Eax & ~0xFF0000u) | (31u << 16),
                        "wide" => (value.Eax & ~0xFF0000u) | (65u << 16),
                        "eventVector" => (value.Eax & 0xFFFFFFu) | (4u << 24),
                        _ => value.Eax,
                    };
                    if (unsupported == "reference") value.Ebx |= 1u << 3;
                    if (unsupported == "miss") value.Ebx |= 1u << 4;
                }
                return value;
            }
            Assert.Throws<NotSupportedException>(() => DramTrafficService.MeasureCounters(msr,
                () => throw new Xunit.Sdk.XunitException("Unsupported PMU ran workload."), cpuidProbe: Probe));
            Assert.Equal(0, msr.ReadCount);
            Assert.Empty(msr.Writes);
        });
    }

    [Theory]
    [InlineData(0x186u, 1UL << 22)]
    [InlineData(0x187u, 1UL << 22)]
    [InlineData(0xC1u, 37UL)]
    [InlineData(0xC2u, 41UL)]
    public void UnownedDramCounterStateNeverStartsWrites(uint register, ulong value)
    {
        WithHardwareMode(settings =>
        {
            var msr = new FakeDramMsr();
            msr.Values[register] = value;
            Assert.Throws<InvalidOperationException>(() => DramTrafficService.MeasureCounters(msr,
                () => 1024, cpuidProbe: IntelPmu));
            Assert.Empty(msr.Writes);
            Assert.Equal(value, msr.Values[register]);
        });
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("workload")]
    [InlineData("cancel")]
    public void DramPrimaryFailureAndCleanupFailureAreBothPreserved(string phase)
    {
        WithHardwareMode(settings =>
        {
            using var cancellation = new CancellationTokenSource();
            Exception primary = phase == "cancel"
                ? new OperationCanceledException(cancellation.Token)
                : new InvalidOperationException("primary " + phase);
            var cleanup = new InvalidOperationException("cleanup write provider error");
            var cleanupRead = new InvalidOperationException("cleanup read provider error");
            var msr = new FakeDramMsr();
            int setupWrites = phase == "setup" ? 1 : 6;
            bool workloadRan = false;
            msr.BeforeWrite = (_, _) =>
            {
                if (phase == "setup" && msr.Writes.Count == 1) throw primary;
                if (msr.Writes.Count == setupWrites + 1) throw cleanup;
            };
            msr.BeforeRead = _ =>
            {
                if (msr.Writes.Count > setupWrites && msr.ReadCount == 6) throw cleanupRead;
            };
            var actual = Assert.Throws<AggregateException>(() => DramTrafficService.MeasureCounters(msr, () =>
            {
                workloadRan = true;
                if (phase == "cancel") cancellation.Cancel();
                throw primary;
            }, cancellation.Token, IntelPmu));
            Assert.Same(primary, actual.InnerExceptions[0]);
            Assert.Contains(cleanup, actual.InnerExceptions);
            Assert.Contains(cleanupRead, actual.InnerExceptions);
            Assert.Equal(phase != "setup", workloadRan);
            // One complete six-write recovery and all five readbacks still execute after failures.
            Assert.Equal(setupWrites + 6, msr.Writes.Count);
            Assert.Equal(10, msr.ReadCount);
            Assert.Equal(new uint[] { 0x38F, 0x186, 0x187, 0xC1, 0xC2, 0x38F },
                msr.Writes.Skip(setupWrites).Select(write => write.Item1).ToArray());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DramSuccessfulCleanupRethrowsOriginalFailureAndCancellation(bool cancel)
    {
        WithHardwareMode(settings =>
        {
            using var cancellation = new CancellationTokenSource();
            Exception expected = cancel ? new OperationCanceledException(cancellation.Token)
                : new InvalidOperationException("original workload error");
            var msr = new FakeDramMsr();
            var originals = new Dictionary<uint, ulong>(msr.Values);
            var actual = Record.Exception(() => DramTrafficService.MeasureCounters(msr, () =>
            {
                if (cancel) cancellation.Cancel();
                throw expected;
            }, cancellation.Token, IntelPmu));
            Assert.Same(expected, actual);
            if (cancel) Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
            Assert.All(originals, entry => Assert.Equal(entry.Value, msr.Values[entry.Key]));
            Assert.Equal(12, msr.Writes.Count);
            Assert.Equal(10, msr.ReadCount);
        });
    }

    private static (uint Eax, uint Ebx, uint Ecx, uint Edx) IntelPmu(uint leaf) => leaf switch
    {
        0 => (0xA, 0x756E6547, 0x6C65746E, 0x49656E69),
        0xA => (2 | (2u << 8) | (48u << 16) | (7u << 24), 0, 0, 0),
        _ => default,
    };

    private static void WithHardwareMode(Action<SettingsService> action)
    {
        var settings = new SettingsService(loadFromDisk: false) { AdvancedDiagnosticsEnabled = true };
        settings.HighRiskHardwareOperationsEnabled = true;
        HardwareOperationGate.Bind(settings);
        try { action(settings); }
        finally { HardwareOperationGate.Bind(null); }
    }

    private sealed class FakeDramMsr : IPmuMsrAccess
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public Dictionary<uint, ulong> Values { get; } = new()
        {
            [0x38F] = 0x100, [0x186] = 0x51, [0x187] = 0x52, [0xC1] = 0, [0xC2] = 0
        };
        public List<(uint, ulong)> Writes { get; } = [];
        public Action? AfterWrite { get; set; }
        public Action<uint, ulong>? BeforeWrite { get; set; }
        public Action<uint>? BeforeRead { get; set; }
        public bool FailWrites { get; set; }
        public int ReadCount { get; private set; }
        public ulong? ReadMsr(uint index)
        {
            ReadCount++;
            BeforeRead?.Invoke(index);
            return Values.TryGetValue(index, out var value) ? value : null;
        }
        public bool WriteMsr(uint index, ulong value)
        {
            Writes.Add((index, value));
            BeforeWrite?.Invoke(index, value);
            if (FailWrites) return false;
            Values[index] = value;
            AfterWrite?.Invoke();
            return true;
        }
    }
}
