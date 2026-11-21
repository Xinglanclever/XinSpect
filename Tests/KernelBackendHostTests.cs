using Xunit;

namespace XinSpect.Tests;

public sealed class KernelBackendHostTests
{
    [Fact]
    public void 模式矩陣_只有明確允許時才建立WinRing0並提供路由()
    {
        var factoryCalls = 0;
        var fake = new FakeUniversalBackend();
        KernelBackendHost.SetFactoriesForTests(() =>
        {
            factoryCalls++;
            return (fake, string.Empty);
        }, () => PawnIoBackend.Create(true, () => (null, "PawnIO service unavailable")));

        try
        {
            foreach (var (mode, expectedFactoryCalls, expectedRoute) in new[]
            {
                (KernelBackendMode.Auto, 0, false),
                (KernelBackendMode.SandboxOnly, 0, false),
                (KernelBackendMode.UniversalAllowed, 1, true),
            })
            {
                KernelBackendHost.ApplyMode(mode);
                var route = KernelBackendHost.Route(KernelCapability.MsrRead);

                Assert.Equal(expectedFactoryCalls, factoryCalls);
                Assert.Equal(expectedRoute, route.Backend is not null);
                if (expectedRoute)
                {
                    Assert.Same(fake, route.Backend);
                    Assert.Contains(KernelBackendHost.Snapshot(), entry =>
                        entry.Desc.Id == "winring0" && entry.Caps.HasFlag(KernelCapability.MsrRead));
                }
                else
                {
                    var blocked = Assert.Single(KernelBackendHost.Snapshot(), entry => entry.Desc.Id == "winring0");
                    Assert.Equal(KernelCapability.None, blocked.Caps);
                    Assert.Contains(mode == KernelBackendMode.Auto ? "自動" : "僅沙箱", blocked.Reason);
                }
            }

            Assert.Equal(1, factoryCalls);
        }
        finally
        {
            KernelBackendHost.ApplyMode(KernelBackendMode.Auto);
            KernelBackendHost.SetFactoriesForTests(null);
        }
    }

    [Fact]
    public void SandboxRoutesOnlySupportedReadsAndKeepsSessionUntilLastReaderLeaves()
    {
        var session = new FakePawnSession(0x1A00);
        KernelBackendHost.SetFactoriesForTests(() => throw new InvalidOperationException("unexpected WinRing0 load"),
            () => PawnIoBackend.Create(true, () => (session, null)));
        try
        {
            KernelBackendHost.ApplyMode(KernelBackendMode.SandboxOnly);
            using var use = KernelBackendHost.RouteUse(KernelCapability.MsrRead);
            var backend = Assert.IsType<PawnIoBackend>(use.Route.Backend);
            Assert.Equal(0x1A00UL, backend.ReadMsr(PawnIoBackend.ProbeMsr));
            Assert.Null(backend.ReadMsr(0x1B));
            Assert.DoesNotContain(0x1Bu, session.Requests);
            Assert.Null(KernelBackendHost.Route(KernelCapability.MsrWrite).Backend);
            Assert.False(backend.WriteMsr(0x198, 0, "test").Succeeded);
            Assert.Null(backend.ReadPciConfig(0, 0, 0, 0));

            KernelBackendHost.ApplyMode(KernelBackendMode.Auto);
            Assert.Equal(0, session.CloseCount);
            use.Dispose();
            Assert.Equal(1, session.CloseCount);
            Assert.Null(backend.ReadMsr(PawnIoBackend.ProbeMsr));
        }
        finally
        {
            KernelBackendHost.ApplyMode(KernelBackendMode.Auto);
            KernelBackendHost.SetFactoriesForTests(null);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(0x100000L)]
    public void PawnIoDeclinesFailedOrZeroRatioProbeAndClosesSession(long? raw)
    {
        var session = new FakePawnSession((ulong?)raw);
        using var backend = PawnIoBackend.Create(true, () => (session, null));
        Assert.Equal(KernelCapability.None, backend.Capabilities);
        Assert.Contains("PLATFORM_INFO", backend.UnavailableReason);
        Assert.Null(backend.ReadMsr(0x198));
        Assert.Equal(1, session.CloseCount);
    }

    [Fact]
    public void PawnIoSkipsNativeLibraryOnUnsupportedPlatform()
    {
        using var backend = PawnIoBackend.Create(false, () => throw new InvalidOperationException("opened on unsupported platform"));
        Assert.Equal(KernelCapability.None, backend.Capabilities);
        Assert.Contains("Windows x64 Intel", backend.UnavailableReason);
    }

    private sealed class FakePawnSession(ulong? probe) : PawnIoBackend.IMsrSession
    {
        public List<uint> Requests { get; } = [];
        public int CloseCount { get; private set; }
        public ulong? Read(uint index)
        {
            Requests.Add(index);
            return probe;
        }
        public void Dispose() => CloseCount++;
    }

    private sealed class FakeUniversalBackend : IKernelBackend
    {
        public string Id => "winring0";
        public string DisplayName => "假 WinRing0";
        public KernelTrustTier Tier => KernelTrustTier.Universal;
        public KernelCapability Capabilities => KernelCapability.MsrRead;
        public string? UnavailableReason => null;

        public ulong? ReadMsr(uint index) => 0x1234;
        public KernelWriteOutcome WriteMsr(uint index, ulong value, string caller) => new(false, "測試後端不支援寫入");
        public uint? ReadPciConfig(byte bus, byte device, byte function, uint register) => null;
        public KernelWriteOutcome WritePciConfig(byte bus, byte device, byte function, uint register, uint value, string caller) => new(false, "測試後端不支援寫入");
        public byte? ReadIoPortByte(uint port) => null;
        public KernelWriteOutcome WriteIoPortByte(uint port, byte value, string caller) => new(false, "測試後端不支援寫入");
        public byte[]? ReadMemoryBlock(ulong address, int length) => null;
        public void Dispose() { }
    }
}
