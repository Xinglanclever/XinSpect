using Xunit;

namespace XinSpect.Tests;

public sealed class KernelBackendHostTests
{
    [Fact]
    public void 模式矩陣_只有明確允許時才建立WinRing0並提供路由()
    {
        var factoryCalls = 0;
        var fake = new FakeUniversalBackend();
        KernelBackendHost.SetWinRing0FactoryForTests(() =>
        {
            factoryCalls++;
            return (fake, string.Empty);
        });

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
            KernelBackendHost.SetWinRing0FactoryForTests(null);
        }
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
