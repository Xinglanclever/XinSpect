using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Xunit;
namespace XinSpect.Tests;

public sealed class DiskSpaceScanServiceTests
{
    private const string Root = @"C:\disk-space-fixture";
    private static string P(string name) => Path.Combine(Root, name);
    private static DiskSpaceFileMetadata Dir(ulong id = 1) => new(true, false, null, null, new(5, id, 0));
    private static DiskSpaceFileMetadata File(long bytes, long allocation, ulong id = 2, ulong volume = 5, ulong high = 0)
        => new(false, false, bytes, allocation, new(volume, id, high));

    [Fact]
    public void HardLinksCountLogicalPerNameButAllocationOnceAcrossDirectories()
    {
        var fs = new FakeFs();
        fs.Add(Root, Dir(), P("a"), P("b")); fs.Add(P("a"), Dir(10), P(@"a\first.bin")); fs.Add(P("b"), Dir(11), P(@"b\linked.dat"));
        fs.Add(P(@"a\first.bin"), File(150, 4096)); fs.Add(P(@"b\linked.dat"), File(150, 4096));
        var result = new DiskSpaceScanService(fs).Scan(Root);
        Assert.Equal(DiskSpaceScanState.Complete, result.State);
        Assert.Equal(300, result.Root.LogicalBytes); Assert.Equal(4096, result.Root.UniqueAllocatedBytes);
        Assert.Equal(2, result.Root.FileCount); Assert.Equal(1, result.HardLinkDuplicates);
        Assert.Equal(0, result.Root.Children[1].UniqueAllocatedBytes);
        Assert.True(result.BiggestFiles.Single(n => n.Path.EndsWith("linked.dat")).IsHardLinkDuplicate);
        Assert.Equal(4096, result.FileTypes.Sum(t => t.UniqueAllocatedBytes));
        Assert.Equal(fs.OpenCount, fs.DisposeCount); Assert.Equal(0, fs.ActiveCount);
        Assert.True(fs.ParentWasOpenForEveryChild);
    }

    [Fact]
    public void DedupKeyIncludesVolumeAndAll128FileIdBits()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("a"), P("b"), P("c"));
        fs.Add(P("a"), File(1, 4096, 22, 5, 1)); fs.Add(P("b"), File(1, 4096, 22, 6, 1)); fs.Add(P("c"), File(1, 4096, 22, 5, 2));
        var result = new DiskSpaceScanService(fs).Scan(Root);
        Assert.Equal(12288, result.Root.UniqueAllocatedBytes); Assert.Equal(0, result.HardLinkDuplicates);
    }

    [Fact]
    public void MissingIdentityNeverPretendsAllocationIsUniqueOrZeroKnown()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("a.bin"), P("b.bin"));
        fs.Add(P("a.bin"), new(false, false, 100, 4096, null, 2, "FileIdInfo unsupported"));
        fs.Add(P("b.bin"), new(false, false, 100, 4096, null, 2, "FileIdInfo unsupported"));
        var result = new DiskSpaceScanService(fs).Scan(Root);
        Assert.Equal(DiskSpaceScanState.Partial, result.State); Assert.Equal(200, result.Root.LogicalBytes);
        Assert.Equal(0, result.Root.UniqueAllocatedBytes); Assert.Equal(2, result.Root.UnknownAllocationFiles);
        Assert.Equal(2, result.Issues.Count(i => i.Kind == DiskSpaceIssueKind.IdentityUnavailable));
        Assert.All(result.BiggestFiles, n => Assert.Equal(4096, n.ReportedAllocatedBytes));
        Assert.Contains("未知", result.Root.AllocatedDisplay);
    }

    [Fact]
    public void ReparsePointsAreNeverEnumeratedAndAreNotZeroByteSuccesses()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("junction"), P("cloud.dat"));
        fs.Add(P("junction"), new(true, true, null, null, null), Root);
        fs.Add(P("cloud.dat"), new(false, true, 999, 4096, new(5, 4, 0)));
        var result = new DiskSpaceScanService(fs).Scan(Root);
        Assert.Equal(DiskSpaceScanState.Partial, result.State); Assert.Equal(0, result.Root.FileCount);
        Assert.Equal(2, result.Issues.Count(i => i.Kind == DiskSpaceIssueKind.ReparsePoint));
        Assert.DoesNotContain(P("junction"), fs.EnumeratedPaths); Assert.All(result.Root.Children, n => Assert.True(n.IsPartial));
        Assert.Equal(0, fs.ActiveCount);
    }

    [Fact]
    public void DepthAndEntryBudgetsRetainObservedSubtreeAndExplicitReason()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("a.bin"), P("sub")); fs.Add(P("a.bin"), File(50, 4096));
        fs.Add(P("sub"), Dir(3), P(@"sub\secret.bin")); fs.Add(P(@"sub\secret.bin"), File(99, 4096, 4));
        var depth = new DiskSpaceScanService(fs).Scan(Root, new() { MaxDepth = 1 });
        Assert.Equal(50, depth.Root.LogicalBytes); Assert.Contains(depth.Issues, i => i.Kind == DiskSpaceIssueKind.DepthLimit);
        Assert.DoesNotContain(P("sub"), fs.EnumeratedPaths);
        var entries = new DiskSpaceScanService(fs).Scan(Root, new() { MaxEntries = 2 });
        Assert.Equal(2, entries.EntriesVisited); Assert.Equal(50, entries.Root.LogicalBytes);
        Assert.Contains(entries.Issues, i => i.Kind == DiskSpaceIssueKind.EntryLimit); Assert.Equal(0, fs.ActiveCount);
    }

    [Fact]
    public void ExactEntryBudgetCanStillFinishWithoutFalsePartialWhenEnumeratorEnds()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("only")); fs.Add(P("only"), File(1, 4096));
        var result = new DiskSpaceScanService(fs).Scan(Root, new() { MaxEntries = 2 });
        Assert.Equal(DiskSpaceScanState.Complete, result.State); Assert.Equal(2, result.EntriesVisited);
    }

    [Fact]
    public void PartialEnumerationRetainsEarlierFilesAndDisposesAncestors()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("first"), P("never")); fs.Add(P("first"), File(25, 4096));
        fs.ThrowAfterChildren[Root] = 1;
        var result = new DiskSpaceScanService(fs).Scan(Root);
        Assert.Equal(25, result.Root.LogicalBytes); Assert.Equal(1, result.Root.FileCount);
        Assert.Equal(DiskSpaceScanState.Partial, result.State); Assert.Contains(result.Issues, i => i.Kind == DiskSpaceIssueKind.ReadError);
        Assert.Equal(fs.OpenCount, fs.DisposeCount); Assert.Equal(fs.EnumeratorCount, fs.EnumeratorDisposeCount);
    }

    [Fact]
    public void PermissionAndDisappearedFilesHaveDistinctMeaningAndBoundedIssueRetention()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("denied"), P("gone"), P("sharing"));
        fs.Failures[P("denied")] = new UnauthorizedAccessException("fixture denied");
        fs.Failures[P("gone")] = new FileNotFoundException("fixture disappeared");
        fs.Failures[P("sharing")] = new Win32Exception(32);
        var result = new DiskSpaceScanService(fs).Scan(Root, new() { MaxIssues = 2 });
        Assert.Equal(3, result.TotalIssues); Assert.Equal(2, result.Issues.Count);
        Assert.Equal(DiskSpaceIssueKind.AccessDenied, result.Issues[0].Kind); Assert.Equal(DiskSpaceIssueKind.ReadError, result.Issues[1].Kind);
        Assert.Equal(DiskSpaceScanState.Partial, result.State); Assert.All(result.Root.Children, n => Assert.True(n.IsPartial));
    }

    [Fact]
    public void PreCancelledScanDoesNotOpenFilesystemAndReturnsLabelledResult()
    {
        var fs = new FakeFs(); using var cts = new CancellationTokenSource(); cts.Cancel();
        var result = new DiskSpaceScanService(fs).Scan(Root, cancellationToken: cts.Token);
        Assert.Equal(DiskSpaceScanState.Cancelled, result.State); Assert.Equal(0, result.EntriesVisited); Assert.Equal(0, fs.OpenCount);
        Assert.Contains(result.Issues, i => i.Kind == DiskSpaceIssueKind.Cancelled);
    }

    [Fact]
    public async Task PreCancelledAsyncCallReturnsResultRatherThanDiscardingWorkContract()
    {
        var fs = new FakeFs(); using var cts = new CancellationTokenSource(); cts.Cancel();
        var result = await new DiskSpaceScanService(fs).ScanAsync(Root, cancellationToken: cts.Token);
        Assert.Equal(DiskSpaceScanState.Cancelled, result.State); Assert.Equal(0, fs.OpenCount);
    }

    [Fact]
    public void CancellationDuringLastMetadataCallIsObservedAndAllHandlesClose()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("one")); fs.Add(P("one"), File(88, 4096)); using var cts = new CancellationTokenSource();
        fs.AfterOpen = p => { if (p == P("one")) cts.Cancel(); };
        var result = new DiskSpaceScanService(fs).Scan(Root, cancellationToken: cts.Token);
        Assert.Equal(DiskSpaceScanState.Cancelled, result.State); Assert.Equal(88, result.Root.LogicalBytes); Assert.Equal(0, fs.ActiveCount);
    }

    [Fact]
    public void TimeBudgetUsesMonotonicClockAndStopsAfterSlowMetadataReturns()
    {
        var clock = new FakeTime(); var fs = new FakeFs(); fs.Add(Root, Dir(), P("slow"), P("never")); fs.Add(P("slow"), File(7, 4096));
        fs.AfterOpen = p => { if (p == P("slow")) clock.Seconds = 5; };
        var result = new DiskSpaceScanService(fs, clock).Scan(Root, new() { MaxDuration = TimeSpan.FromSeconds(2) });
        Assert.Equal(7, result.Root.LogicalBytes); Assert.Equal(2, result.EntriesVisited);
        Assert.Contains(result.Issues, i => i.Kind == DiskSpaceIssueKind.TimeLimit); Assert.Equal(0, fs.ActiveCount);
    }

    [Fact]
    public void RootReparseFileAndMissingRootNeverReportComplete()
    {
        var fs = new FakeFs(); fs.Add(Root, new(true, true, null, null, null));
        Assert.Equal(DiskSpaceScanState.Partial, new DiskSpaceScanService(fs).Scan(Root).State);
        fs.Add(Root, File(1, 4096));
        Assert.Contains(new DiskSpaceScanService(fs).Scan(Root).Issues, i => i.Kind == DiskSpaceIssueKind.ReadError);
        fs.Failures[Root] = new DirectoryNotFoundException("fixture root disappeared");
        Assert.Equal(DiskSpaceScanState.Partial, new DiskSpaceScanService(fs).Scan(Root).State);
    }

    [Fact]
    public void DirectoryIdentityCycleIsStoppedEvenWithoutReparseAttribute()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("alias")); fs.Add(P("alias"), Dir(), P(@"alias\loop"));
        var result = new DiskSpaceScanService(fs).Scan(Root);
        Assert.Contains(result.Issues, i => i.Kind == DiskSpaceIssueKind.DuplicateDirectory);
        Assert.Equal(2, result.EntriesVisited); Assert.DoesNotContain(P("alias"), fs.EnumeratedPaths);
    }

    [Fact]
    public void EscapingEnumerationIsRejectedBeforeOpeningOtherPath()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), @"C:\outside\private.dat");
        var result = new DiskSpaceScanService(fs).Scan(Root);
        Assert.Equal(1, fs.OpenCount); Assert.Equal(DiskSpaceScanState.Partial, result.State);
        Assert.Contains(result.Issues, i => i.Reason.Contains("非直接子項目"));
    }

    [Fact]
    public void OverflowIsExplicitAndDoesNotWrapNegative()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("a"), P("b")); fs.Add(P("a"), File(long.MaxValue, 0)); fs.Add(P("b"), File(1, 0, 3));
        var result = new DiskSpaceScanService(fs).Scan(Root);
        Assert.Equal(long.MaxValue, result.Root.LogicalBytes); Assert.Equal(DiskSpaceScanState.Partial, result.State);
        Assert.Contains(result.Issues, i => i.Kind == DiskSpaceIssueKind.SizeOverflow);
    }

    [Fact]
    public void BigFileRetentionIsBoundedAndSortedAndReportContainsLimits()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("small.txt"), P("big.bin"), P("medium.txt"));
        fs.Add(P("small.txt"), File(3, 4096)); fs.Add(P("big.bin"), File(9, 4096, 3)); fs.Add(P("medium.txt"), File(6, 4096, 4));
        var result = new DiskSpaceScanService(fs).Scan(Root, new() { MaxBigFiles = 2 });
        Assert.Equal(new long[] { 9, 6 }, result.BiggestFiles.Select(n => n.LogicalBytes));
        Assert.Equal(18, result.FileTypes.Sum(n => n.LogicalBytes)); Assert.Equal(2, result.FileTypes.Count);
        using var json = JsonDocument.Parse(DiskSpaceScanService.CreateJsonReport(result));
        Assert.Equal("xinspect.disk-space.v1", json.RootElement.GetProperty("schema").GetString());
        Assert.Contains("非快照", json.RootElement.GetProperty("limits").GetString()); Assert.Equal(2, json.RootElement.GetProperty("biggestFiles").GetArrayLength());
    }

    [Theory]
    [InlineData(0, 64, 30)] [InlineData(1000001, 64, 30)] [InlineData(10, -1, 30)] [InlineData(10, 257, 30)] [InlineData(10, 64, 0)] [InlineData(10, 64, 601)]
    public void InvalidBudgetsAreRejectedBeforeAnyFilesystemAccess(int entries, int depth, int seconds)
    {
        var fs = new FakeFs();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiskSpaceScanService(fs).Scan(Root, new() { MaxEntries = entries, MaxDepth = depth, MaxDuration = TimeSpan.FromSeconds(seconds) }));
        Assert.Equal(0, fs.OpenCount);
    }

    [Theory]
    [InlineData(@"relative\folder")] [InlineData(@"\\.\PhysicalDrive0")] [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1")]
    public void RawDeviceAndRelativePathsAreNotAccepted(string path)
    {
        var fs = new FakeFs(); Assert.Throws<ArgumentException>(() => new DiskSpaceScanService(fs).Scan(path)); Assert.Equal(0, fs.OpenCount);
    }

    [Theory]
    [InlineData("//?/GLOBALROOT/Device/HarddiskVolume1/")]
    [InlineData("//./PhysicalDrive0")]
    [InlineData("//?/UNC/server/share/")]
    [InlineData(@"\\?/GLOBALROOT/Device/HarddiskVolume1/")]
    [InlineData(@"//?\GLOBALROOT\Device\HarddiskVolume1\")]
    public void SlashDeviceNamespaceAliasesAreRejectedAfterNormalizationWithoutOpeningAnything(string path)
    {
        var fs = new FakeFs();
        Assert.Throws<ArgumentException>(() => new DiskSpaceScanService(fs).Scan(path));
        Assert.Equal(0, fs.OpenCount); Assert.Empty(fs.EnumeratedPaths);
    }

    [Fact]
    public void LongRetainedPathsReachMetadataBudgetBeforeEntryBudgetAndDoNotOpenRefusedChild()
    {
        // Entirely injected long-path fixture. No such directories are created or opened on the host.
        string longRoot = @"C:\" + string.Join("\\", Enumerable.Repeat(new string('d', 240), 100));
        string[] children = Enumerable.Range(0, 100).Select(i => Path.Combine(longRoot, i.ToString("D3") + ".bin")).ToArray();
        var fs = new FakeFs(); fs.Add(longRoot, Dir(), children);
        foreach (string child in children) fs.Add(child, File(17, 4096, (ulong)(2 + Array.IndexOf(children, child))));
        var options = new DiskSpaceScanOptions { MaxRetainedMetadataBytes = 1024 * 1024, MaxEntries = 1000 };
        var result = new DiskSpaceScanService(fs).Scan(longRoot, options);
        Assert.Equal(DiskSpaceScanState.Partial, result.State);
        Assert.Contains(result.Issues, i => i.Kind == DiskSpaceIssueKind.MemoryLimit);
        Assert.DoesNotContain(result.Issues, i => i.Kind == DiskSpaceIssueKind.EntryLimit);
        Assert.InRange(result.EstimatedRetainedMetadataBytes, 1, options.MaxRetainedMetadataBytes);
        Assert.InRange(result.EntriesVisited, 2, 20);
        Assert.Equal(result.Root.FileCount + 1, result.EntriesVisited);
        Assert.Equal(result.EntriesVisited, fs.OpenCount); Assert.Equal(0, fs.ActiveCount);
        Assert.Equal(17 * result.Root.FileCount, result.Root.LogicalBytes);
    }

    [Fact]
    public void MemoryStopReasonSurvivesFullIssueListAndLargeDiagnosticsAreBounded()
    {
        var fs = new FakeFs(); string[] children = Enumerable.Range(0, 1000).Select(i => P(i.ToString("D4") + ".bin")).ToArray();
        fs.Add(Root, Dir(), children);
        string largeReason = new('x', 100_000);
        foreach (string child in children) fs.Failures[child] = new IOException(largeReason);
        var options = new DiskSpaceScanOptions { MaxIssues = 1, MaxRetainedMetadataBytes = 1024 * 1024 };
        var result = new DiskSpaceScanService(fs).Scan(Root, options);
        Assert.Equal(DiskSpaceScanState.Partial, result.State); Assert.Single(result.Issues);
        Assert.Equal(DiskSpaceIssueKind.MemoryLimit, result.Issues[0].Kind);
        Assert.True(result.TotalIssues > result.Issues.Count);
        Assert.All(result.Root.Children, n => Assert.InRange(n.Note!.Length, 1, 1024));
        Assert.True(result.EntriesVisited < children.Length);
        Assert.InRange(result.EstimatedRetainedMetadataBytes, 1, options.MaxRetainedMetadataBytes);
    }

    [Theory]
    [InlineData(0L)] [InlineData(1048575L)] [InlineData(536870913L)]
    public void InvalidRetainedMetadataBudgetsAreRejectedBeforeFilesystemAccess(long bytes)
    {
        var fs = new FakeFs();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiskSpaceScanService(fs).Scan(Root, new() { MaxRetainedMetadataBytes = bytes }));
        Assert.Equal(0, fs.OpenCount);
    }

    [Fact]
    public void ReportCarriesMetadataEstimateAndDoesNotClaimClrHeapHardLimit()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir());
        var result = new DiskSpaceScanService(fs).Scan(Root);
        Assert.Equal(128L * 1024 * 1024, result.Options.MaxRetainedMetadataBytes);
        using var json = JsonDocument.Parse(DiskSpaceScanService.CreateJsonReport(result));
        Assert.Equal(result.EstimatedRetainedMetadataBytes, json.RootElement.GetProperty("EstimatedRetainedMetadataBytes").GetInt64());
        Assert.Contains("不是 CLR/process heap", json.RootElement.GetProperty("memoryAccounting").GetString());
    }

    [Fact]
    public void TreemapPreservesAreaAndRatioAndNeverOverlapsOutsideCanvas()
    {
        var fs = new FakeFs(); fs.Add(Root, Dir(), P("a"), P("b"), P("c"));
        fs.Add(P("a"), File(60, 4096)); fs.Add(P("b"), File(30, 4096, 3)); fs.Add(P("c"), File(10, 4096, 4));
        var result = new DiskSpaceScanService(fs).Scan(Root);
        var tiles = DiskSpaceTreemap.Layout(result.Root.Children, 600, 400, false);
        Assert.Equal(3, tiles.Count); Assert.Equal(240000, tiles.Sum(t => t.Width * t.Height), 5);
        foreach (var tile in tiles)
        {
            Assert.True(tile.X >= 0 && tile.Y >= 0 && tile.X + tile.Width <= 600.00001 && tile.Y + tile.Height <= 400.00001);
            Assert.Equal(tile.Node.LogicalBytes / 100.0, tile.Width * tile.Height / 240000, 8);
        }
        for (int i = 0; i < tiles.Count; i++) for (int j = i + 1; j < tiles.Count; j++)
            Assert.True(tiles[i].X + tiles[i].Width <= tiles[j].X + 0.00001 || tiles[j].X + tiles[j].Width <= tiles[i].X + 0.00001 || tiles[i].Y + tiles[i].Height <= tiles[j].Y + 0.00001 || tiles[j].Y + tiles[j].Height <= tiles[i].Y + 0.00001);
        Assert.Empty(DiskSpaceTreemap.Layout(result.Root.Children, double.NaN, 400, false));
    }

    private sealed class FakeTime : TimeProvider
    {
        public long Seconds { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(-Seconds); // deliberately reverse wall time
    }
    private sealed class FakeFs : IDiskSpaceFileSystem
    {
        private readonly Dictionary<string, (DiskSpaceFileMetadata Metadata, string[] Children)> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ThrowAfterChildren { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> EnumeratedPaths { get; } = [];
        public int OpenCount, DisposeCount, EnumeratorCount, EnumeratorDisposeCount;
        public int ActiveCount => _active.Count;
        public bool ParentWasOpenForEveryChild = true;
        public Action<string>? AfterOpen;
        public void Add(string path, DiskSpaceFileMetadata metadata, params string[] children) => _entries[path] = (metadata, children);
        public DiskSpaceOpenNode Open(string path)
        {
            if (Failures.TryGetValue(path, out var error)) throw error;
            if (!_entries.TryGetValue(path, out var entry)) throw new FileNotFoundException("fixture not registered");
            if (path != Root && !_active.Contains(Path.GetDirectoryName(path)!)) ParentWasOpenForEveryChild = false;
            OpenCount++; _active.Add(path); AfterOpen?.Invoke(path);
            return new FakeNode(this, path, entry.Metadata, entry.Children);
        }
        private sealed class FakeNode(FakeFs fs, string path, DiskSpaceFileMetadata metadata, string[] children) : DiskSpaceOpenNode
        {
            public override DiskSpaceFileMetadata Metadata => metadata;
            public override IEnumerable<string> EnumerateChildren()
            {
                fs.EnumeratedPaths.Add(path); fs.EnumeratorCount++;
                try
                {
                    int count = 0;
                    foreach (string child in children)
                    {
                        if (fs.ThrowAfterChildren.TryGetValue(path, out int limit) && count >= limit) throw new IOException("fixture mid-enumeration error");
                        count++; yield return child;
                    }
                }
                finally { fs.EnumeratorDisposeCount++; }
            }
            public override void Dispose() { fs._active.Remove(path); fs.DisposeCount++; }
        }
    }
}

/// <summary>Actual Windows API reads are restricted to a newly created, owned temporary fixture.</summary>
public sealed class DiskSpaceWindowsFixtureTests
{
    [Fact]
    public void NativeActiveDirectoryGuardPreventsRenameUntilDisposed()
    {
        using var fixture = new TempFixture();
        string directory = Path.Combine(fixture.Path, "active"), renamed = Path.Combine(fixture.Path, "renamed");
        Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, "owned.bin"), [1]);
        using (var root = new DiskSpaceWindowsFileSystem().Open(fixture.Path))
        using (var opened = new DiskSpaceWindowsFileSystem().Open(directory))
        {
            Assert.True(opened.Metadata.IsDirectory); Assert.False(opened.Metadata.IsReparsePoint);
            bool moved = MoveFileExW(directory, renamed, 0); int error = moved ? 0 : Marshal.GetLastWin32Error();
            Assert.False(moved); Assert.Equal(32, error);
            Assert.Single(opened.EnumerateChildren());
        }
        // A failed move must be due to the live guard, rather than fixture ACLs or a permanently held handle.
        Assert.True(MoveFileExW(directory, renamed, 0), "Move after guard disposal failed. Win32=" + Marshal.GetLastWin32Error());
    }

    [Fact]
    public void NativeRegularEmptyAndNestedFilesHaveSeparateLogicalAndAllocationTotals()
    {
        using var fixture = new TempFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Path, "nested"));
        File.WriteAllBytes(Path.Combine(fixture.Path, "tiny.bin"), [1, 2, 3]); File.WriteAllBytes(Path.Combine(fixture.Path, "empty.txt"), []);
        File.WriteAllBytes(Path.Combine(fixture.Path, "nested", "data.bin"), new byte[7001]);
        var result = new DiskSpaceScanService().Scan(fixture.Path);
        Assert.Equal(DiskSpaceScanState.Complete, result.State); Assert.Equal(3, result.Root.FileCount); Assert.Equal(7004, result.Root.LogicalBytes);
        Assert.Equal(0, result.Root.UnknownAllocationFiles); Assert.True(result.Root.UniqueAllocatedBytes > 0);
        var fs = new DiskSpaceWindowsFileSystem(); long expected = 0;
        foreach (string p in Directory.EnumerateFiles(fixture.Path, "*", SearchOption.AllDirectories))
        { using var node = fs.Open(p); expected += node.Metadata.AllocatedBytes!.Value; }
        Assert.Equal(expected, result.Root.UniqueAllocatedBytes);
    }

    [Fact]
    public void NativeHardLinkWithinOwnedFixtureHasOneUniqueAllocation()
    {
        using var fixture = new TempFixture(); string original = Path.Combine(fixture.Path, "original.bin"), linked = Path.Combine(fixture.Path, "second-name.bin");
        File.WriteAllBytes(original, new byte[9001]);
        Assert.True(CreateHardLinkW(linked, original, IntPtr.Zero), "Owned temporary filesystem must support hard links for this integration case. Win32=" + Marshal.GetLastWin32Error());
        var result = new DiskSpaceScanService().Scan(fixture.Path);
        Assert.Equal(DiskSpaceScanState.Complete, result.State); Assert.Equal(18002, result.Root.LogicalBytes); Assert.Equal(1, result.HardLinkDuplicates);
        using var node = new DiskSpaceWindowsFileSystem().Open(original);
        Assert.Equal(node.Metadata.AllocatedBytes, result.Root.UniqueAllocatedBytes); Assert.Equal((uint)2, node.Metadata.LinkCount);
    }

    [Fact]
    public void NativeSparseFixturePreservesLogicalLengthWithoutInventingAllocatedClusters()
    {
        using var fixture = new TempFixture(); string path = Path.Combine(fixture.Path, "sparse.bin");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.True(DeviceIoControl(stream.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero),
                "Owned temporary filesystem must support FSCTL_SET_SPARSE for this integration case. Win32=" + Marshal.GetLastWin32Error());
            stream.SetLength(64L * 1024 * 1024);
        }
        var result = new DiskSpaceScanService().Scan(fixture.Path);
        Assert.Equal(DiskSpaceScanState.Complete, result.State); Assert.Equal(64L * 1024 * 1024, result.Root.LogicalBytes);
        Assert.Equal(0, result.Root.UnknownAllocationFiles); Assert.True(result.Root.UniqueAllocatedBytes < result.Root.LogicalBytes);
    }

    private sealed class TempFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XinSpect-disk-space-test-" + Guid.NewGuid().ToString("N"));
        public TempFixture() => Directory.CreateDirectory(Path);
        // Only the uniquely named fixture created above is ever removed; never a user-selected directory.
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);
}
