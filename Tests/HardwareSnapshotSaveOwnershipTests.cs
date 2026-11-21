using System.Buffers.Binary;
using System.IO;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

public sealed class HardwareSnapshotSaveOwnershipTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "XinSpect-snapshot-owner-" + Guid.NewGuid().ToString("N"));
    public HardwareSnapshotSaveOwnershipTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
    private string At(string name) => Path.Combine(_directory, name);
    private static HardwareSnapshot Snapshot() => HardwareSnapshotService.Create("ownership-test",
        [new HardwareFact("test.value", "test", "value", "1", "", "fixture", FactTrustLevel.Reported,
            false, new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), 1)], false);

    [Fact]
    public void DefaultSavePreservesExistingFixedSidecarAndPublishesValidatedSnapshot()
    {
        string destination = At("saved.xinsnapshot"), oldSidecar = destination + ".tmp";
        File.WriteAllText(oldSidecar, "unrelated sidecar");
        HardwareSnapshotService.Save(destination, Snapshot());
        Assert.Equal("unrelated sidecar", File.ReadAllText(oldSidecar));
        Assert.Single(HardwareSnapshotService.Load(destination).Facts);
        Assert.Empty(Directory.GetFiles(_directory, ".xinsnapshot-*.tmp"));
    }

    [Fact]
    public void OverwriteFalsePreservesBothExistingDestinationAndFixedSidecar()
    {
        string destination = At("existing.xinsnapshot"), sidecar = destination + ".tmp";
        File.WriteAllText(destination, "original destination");
        File.WriteAllText(sidecar, "original sidecar");
        Assert.Throws<IOException>(() => HardwareSnapshotService.Save(destination, Snapshot(),
            new HardwareSnapshotSaveOptions { Overwrite = false }));
        Assert.Equal("original destination", File.ReadAllText(destination));
        Assert.Equal("original sidecar", File.ReadAllText(sidecar));
        Assert.Empty(Directory.GetFiles(_directory, ".xinsnapshot-*.tmp"));
    }

    [Fact]
    public void ExplicitStagingCollisionNeverAcquiresOrDeletesExistingFile()
    {
        string staging = At("collision.tmp");
        File.WriteAllText(staging, "other owner's bytes");
        Assert.Throws<IOException>(() => SnapshotSaveFile.CreateAtPath(staging));
        Assert.Equal("other owner's bytes", File.ReadAllText(staging));
    }

    [Fact]
    public void OwnerRetainsExclusiveIdentityUntilHandleRenameAndDoesNotDeletePublishedFile()
    {
        string staging = At("owned.tmp"), destination = At("published.xinsnapshot");
        using (var owner = SnapshotSaveFile.CreateAtPath(staging))
        {
            owner.Write([1, 2, 3]);
            owner.Flush();
            Assert.ThrowsAny<IOException>(() => File.Move(staging, At("stolen.tmp")));
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(staging, "replacement"));
            owner.Publish(destination, false);
        }
        Assert.False(File.Exists(staging));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public void FailedPublicationDeletesOwnedObjectAndPreservesDestination()
    {
        string staging = At("failure.tmp"), destination = At("target.xinsnapshot");
        File.WriteAllText(destination, "keep");
        using (var owner = SnapshotSaveFile.CreateAtPath(staging))
        {
            owner.Write([4]); owner.Flush();
            Assert.Throws<IOException>(() => owner.Publish(destination, false));
        }
        Assert.False(File.Exists(staging));
        Assert.Equal("keep", File.ReadAllText(destination));
    }

    [Fact]
    public void OverwriteTrueReplacesDestinationWithCompletedOwnedBytes()
    {
        string staging = At("replacement.tmp"), destination = At("target.xinsnapshot");
        File.WriteAllText(destination, "old");
        using (var owner = SnapshotSaveFile.CreateAtPath(staging))
        {
            owner.Write([7, 8]); owner.Flush(); owner.Publish(destination, true);
        }
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(destination));
        Assert.False(File.Exists(staging));
    }

    [Theory]
    [InlineData(4, 12)]
    [InlineData(8, 20)]
    public void RenameBufferMatchesSdkPointerAlignmentAndUtf16ByteLength(int pointerSize, int nameOffset)
    {
        string destination = At("snapshot-測試.xinsnapshot");
        byte[] buffer = SnapshotSaveFile.BuildRenameInfo(destination, true, pointerSize);
        Assert.Equal(1, buffer[0]);
        Assert.All(buffer.Skip(1).Take(nameOffset - 5), b => Assert.Equal(0, b));
        int byteCount = Encoding.Unicode.GetByteCount(destination);
        Assert.Equal((uint)byteCount, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(nameOffset - 4)));
        Assert.Equal(destination, Encoding.Unicode.GetString(buffer, nameOffset, byteCount));
        Assert.Equal(new byte[2], buffer[^2..]);
        Assert.Equal(0, SnapshotSaveFile.BuildRenameInfo(destination, false, pointerSize)[0]);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("publish")]
    public void PreparedSaveDisposesOnlyAcquiredOwnerAndPreservesOriginalFailure(string failAt)
    {
        var original = new IOException(failAt);
        var owner = new FakeOwner(failAt, original);
        var caught = Assert.Throws<IOException>(() => HardwareSnapshotService.SavePrepared(
            At("unused.xinsnapshot"), [1], false, _ => owner));
        Assert.Same(original, caught);
        Assert.Equal("dispose", owner.Events[^1]);
        Assert.Equal(failAt == "write" ? new[] { "write", "dispose" }
            : failAt == "flush" ? new[] { "write", "flush", "dispose" }
            : new[] { "write", "flush", "publish", "dispose" }, owner.Events);
    }

    private sealed class FakeOwner(string failAt, Exception failure) : ISnapshotSaveFile
    {
        public List<string> Events { get; } = [];
        private void Step(string name) { Events.Add(name); if (name == failAt) throw failure; }
        public void Write(byte[] bytes) => Step("write");
        public void Flush() => Step("flush");
        public void Publish(string destination, bool overwrite) => Step("publish");
        public void Dispose() => Events.Add("dispose");
    }
}
