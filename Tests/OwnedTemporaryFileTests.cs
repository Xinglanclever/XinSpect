using System.IO;
using Xunit;

namespace XinSpect.Tests;

public sealed class OwnedTemporaryFileTests
{
    [Fact]
    public async Task GauntletFailureNeverRecursivelyDeletesSharedDirectory()
    {
        string? sentinel = null;
        try
        {
            var engine = new FailingGauntletEngine(path => sentinel = path);
            var service = new GauntletMultiDomainService(engine);
            var context = new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick,
                new Progress<DeepBenchProgress>());
            DeepBenchTestResult result = await service.RunAsync(context, CancellationToken.None);
            Assert.Equal(DeepBenchFailureKind.PlatformError, result.FailureKind);
            Assert.NotNull(sentinel);
            Assert.Equal(new byte[] { 61 }, File.ReadAllBytes(sentinel));
        }
        finally { if (sentinel is not null) File.Delete(sentinel); }
    }

    [Fact]
    public void CollisionPreservesExistingBytes()
    {
        string root = NewFixtureDirectory();
        string path = System.IO.Path.Combine(root, "existing.tmp");
        try
        {
            File.WriteAllBytes(path, [17, 23, 42]);
            Assert.Throws<IOException>(() => OwnedTemporaryFile.CreateAtPath(path));
            Assert.Equal(new byte[] { 17, 23, 42 }, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TwoLeasesKeepIndependentObjectsAndExistingSibling()
    {
        string root = NewFixtureDirectory();
        string sibling = System.IO.Path.Combine(root, "pipeline.tmp");
        try
        {
            File.WriteAllBytes(sibling, [91]);
            using var first = OwnedTemporaryFile.Create(root, "pipeline");
            using var second = OwnedTemporaryFile.Create(root, "pipeline");
            Assert.NotEqual(first.Path, second.Path);
            RandomAccess.Write(first.Handle, new byte[] { 1 }, 0);
            RandomAccess.Write(second.Handle, new byte[] { 2 }, 0);
            first.Dispose();
            Assert.False(File.Exists(first.Path));
            byte[] read = new byte[1];
            Assert.Equal(1, RandomAccess.Read(second.Handle, read, 0));
            Assert.Equal(2, read[0]);
            Assert.Equal(new byte[] { 91 }, File.ReadAllBytes(sibling));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExceptionUnwindClosesOwnedFileWithoutDeletingSibling()
    {
        string root = NewFixtureDirectory();
        string? owned = null;
        try
        {
            Assert.Throws<OperationCanceledException>((Action)(() =>
            {
                using var temp = OwnedTemporaryFile.Create(root, "cancel");
                owned = temp.Path;
                throw new OperationCanceledException();
            }));
            Assert.NotNull(owned);
            Assert.False(File.Exists(owned));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewFixtureDirectory()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XinSpect-owned-tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FailingGauntletEngine(Action<string> capture) : IGauntletMultiDomainEngine
    {
        public Task<GauntletMultiDomainMeasurement> MeasureAsync(GauntletMultiDomainContext context,
            CancellationToken cancellationToken)
        {
            string path = System.IO.Path.Combine(context.TempDirPath, "unowned." + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, [61]);
            capture(path);
            throw new IOException("fake workload failure");
        }
    }
}
