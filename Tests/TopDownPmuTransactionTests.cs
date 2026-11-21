using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class TopDownPmuTransactionTests
{
    private sealed class Fake
    {
        public readonly Dictionary<uint, ulong> Registers = new()
        {
            [0x38F] = 1UL << 33, [0x186] = 0, [0x187] = 0, [0x188] = 0, [0x189] = 0,
            [0xC1] = 11, [0xC2] = 22, [0xC3] = 33, [0xC4] = 44,
        };
        public int Writes;
        public ulong? Read(uint index) => Registers[index];
        public bool Write(uint index, ulong value) { Writes++; Registers[index] = value; return true; }
        public TopDownPmuResult Run(Func<uint, ulong?>? read = null, Func<uint, ulong, bool>? write = null,
            Func<bool>? policy = null, Action<CancellationToken>? window = null, bool verified = true) =>
            TopDownService.SampleTransaction(read ?? Read, write ?? Write, policy ?? (() => true),
                window ?? (_ => { for (uint i = 0; i < 4; i++) Registers[0xC1 + i] = i + 100; }),
                verified, CancellationToken.None);
    }

    [Fact]
    public void UnverifiedPlatformDoesNotReadOrWrite()
    {
        var fake = new Fake();
        var result = fake.Run(read: _ => throw new Exception("must not read"), verified: false);
        Assert.Equal(FactAvailability.NotSupported, result.Availability);
        Assert.Equal(0, fake.Writes);
    }

    [Fact]
    public void UnknownBaselineNeverDefaultsToZero()
    {
        var fake = new Fake();
        var result = fake.Run(read: index => index == 0xC3 ? null : fake.Read(index));
        Assert.Equal(FactAvailability.Unknown, result.Availability);
        Assert.Equal(0, fake.Writes);
    }

    [Theory]
    [InlineData(0x38F)]
    [InlineData(0x186)]
    public void BusyCounterIsNotPreempted(int index)
    {
        var fake = new Fake();
        fake.Registers[(uint)index] |= 1;
        Assert.Equal(FactAvailability.Unknown, fake.Run().Availability);
        Assert.Equal(0, fake.Writes);
    }

    [Fact]
    public void SuccessRestoresCountersSelectorsAndGlobal()
    {
        var fake = new Fake();
        var saved = new Dictionary<uint, ulong>(fake.Registers);
        var result = fake.Run();
        Assert.Equal(FactAvailability.Present, result.Availability);
        Assert.Equal(new ulong[] { 100, 101, 102, 103 }, result.Values);
        Assert.True(result.Restored);
        foreach (var pair in saved) Assert.Equal(pair.Value, fake.Registers[pair.Key]);
    }

    [Fact]
    public void PartialSetupFalseWriteStillTriggersCleanup()
    {
        var fake = new Fake();
        var saved = new Dictionary<uint, ulong>(fake.Registers);
        bool failed = false;
        var result = fake.Run(write: (index, value) =>
        {
            fake.Write(index, value);
            if (index == 0x187 && !failed) { failed = true; return false; }
            return true;
        });
        Assert.Equal(FactAvailability.ReadError, result.Availability);
        Assert.Null(result.Values);
        foreach (var pair in saved) Assert.Equal(pair.Value, fake.Registers[pair.Key]);
    }

    [Fact]
    public void PolicyRevocationDuringSetupStopsProgrammingButAllowsRecovery()
    {
        var fake = new Fake();
        var saved = new Dictionary<uint, ulong>(fake.Registers);
        bool worked = false;
        var result = fake.Run(policy: () => fake.Writes < 3, window: _ => worked = true);
        Assert.Equal(FactAvailability.Unknown, result.Availability);
        Assert.False(worked);
        Assert.Null(result.Values);
        foreach (var pair in saved) Assert.Equal(pair.Value, fake.Registers[pair.Key]);
    }

    [Fact]
    public void RestoreReadbackFailureNeverPublishesSample()
    {
        var fake = new Fake();
        var result = fake.Run(write: (index, value) =>
        {
            if (index == 0xC1 && value == 11) return false;
            return fake.Write(index, value);
        });
        Assert.Equal(FactAvailability.ReadError, result.Availability);
        Assert.False(result.Restored);
        Assert.Null(result.Values);
        Assert.Contains("restoration", result.Reason);
    }

    [Fact]
    public void ThrowingSetupWriteStillRestoresAllBaselineRegisters()
    {
        var fake = new Fake();
        var saved = new Dictionary<uint, ulong>(fake.Registers);
        bool failed = false;
        var result = fake.Run(write: (index, value) =>
        {
            fake.Write(index, value);
            if (index == 0xC2 && !failed) { failed = true; throw new IOException(); }
            return true;
        });
        Assert.Equal(FactAvailability.ReadError, result.Availability);
        Assert.Null(result.Values);
        foreach (var pair in saved) Assert.Equal(pair.Value, fake.Registers[pair.Key]);
    }

    [Fact]
    public void RevocationDuringWindowSuppressesSampleAndRestores()
    {
        var fake = new Fake();
        var saved = new Dictionary<uint, ulong>(fake.Registers);
        bool allowed = true;
        var result = fake.Run(policy: () => allowed, window: _ => allowed = false);
        Assert.Equal(FactAvailability.Unknown, result.Availability);
        Assert.Null(result.Values);
        foreach (var pair in saved) Assert.Equal(pair.Value, fake.Registers[pair.Key]);
    }
}
