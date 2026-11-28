using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Budget and race regressions. Only local temporary fixtures are used; no driver APIs/export run.</summary>
public sealed class DriverStoreBoundaryTests
{
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XinSpect-DriverStore-boundaries-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void EmptyInfRowsConsumeTheWmiRowBudget()
    {
        DriverStoreBinding[] rows = [new("", "A", ""), new("", "B", ""), new("", "C", "")];
        Assert.Throws<InvalidDataException>(() => DriverStoreNativeBackend.CollectBindings(rows,
            new DriverStoreBindingReadBudget(maxRows: 2, elapsed: () => TimeSpan.Zero), CancellationToken.None));
        Assert.Empty(DriverStoreNativeBackend.CollectBindings(rows[..2],
            new DriverStoreBindingReadBudget(maxRows: 2, elapsed: () => TimeSpan.Zero), CancellationToken.None));
    }

    [Fact]
    public void DeadlineIncludesTimeSpentInsideMoveNextAndRejectsLatePartialResults()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        IEnumerable<DriverStoreBinding> Rows()
        {
            yield return new("oem1.inf", "A", "Device A");
            elapsed = TimeSpan.FromSeconds(31); // Simulates a provider call returning after the overall deadline.
            yield return new("", "B", "");
        }
        Assert.Throws<TimeoutException>(() => DriverStoreNativeBackend.CollectBindings(Rows(),
            new DriverStoreBindingReadBudget(elapsed: () => elapsed), CancellationToken.None));
    }

    [Fact]
    public void EvenLateEndOfEnumerationCannotPublishEarlierBindings()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        IEnumerable<DriverStoreBinding> Rows()
        {
            yield return new("oem1.inf", "A", "Device A");
            elapsed = TimeSpan.FromSeconds(30);
        }
        Assert.Throws<TimeoutException>(() => DriverStoreNativeBackend.CollectBindings(Rows(),
            new DriverStoreBindingReadBudget(elapsed: () => elapsed), CancellationToken.None));
    }

    [Fact]
    public void CancellationAfterAnEmptyInfRowIsNotIgnored()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<DriverStoreBinding> Rows()
        {
            yield return new("", "A", "");
            cancellation.Cancel();
            yield return new("oem1.inf", "B", "Device B");
        }
        Assert.Throws<OperationCanceledException>(() => DriverStoreNativeBackend.CollectBindings(Rows(),
            new DriverStoreBindingReadBudget(elapsed: () => TimeSpan.Zero), cancellation.Token));
    }

    [Fact]
    public void HandleInformationMatchesWindowsByHandleFileInformationAbi()
    {
        Assert.Equal(52, Marshal.SizeOf<DriverStoreService.NativeFileInformation>());
        Assert.Equal(40, Marshal.OffsetOf<DriverStoreService.NativeFileInformation>(nameof(DriverStoreService.NativeFileInformation.NumberOfLinks)).ToInt32());
    }

    [Fact]
    public async Task NestedDirectoriesAndOpenedFilesCannotBeReplacedDuringHashing()
    {
        using var temp = new TempFolder();
        string nested = System.IO.Path.Combine(temp.Path, "nested");
        Directory.CreateDirectory(nested);
        string inf = System.IO.Path.Combine(nested, "driver.inf");
        await File.WriteAllTextAsync(inf, "known fixture");
        bool directoryChecked = false, fileChecked = false;
        var result = await DriverStoreService.HashOutputAsync(temp.Path, CancellationToken.None, observe: (stage, path) =>
        {
            if (stage == DriverStoreService.HashStage.DirectoryPinned && string.Equals(path, nested, StringComparison.OrdinalIgnoreCase))
            {
                Assert.ThrowsAny<IOException>(() => Directory.Move(nested, nested + "-moved"));
                directoryChecked = true;
            }
            if (stage == DriverStoreService.HashStage.FileHandleValidated)
            {
                Assert.ThrowsAny<IOException>(() => File.Move(path, path + "-moved"));
                Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "replacement"));
                fileChecked = true;
            }
        });
        Assert.True(directoryChecked); Assert.True(fileChecked);
        Assert.Equal(System.IO.Path.Combine("nested", "driver.inf"), Assert.Single(result).RelativePath);
    }

    [Fact]
    public async Task JunctionSwapAfterMetadataWorksAgainstLegacyPathsButHandleWalkRejectsIt()
    {
        using var temp = new TempFolder(); using var outside = new TempFolder();
        string nested = System.IO.Path.Combine(temp.Path, "nested");
        string saved = nested + "-original";
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(System.IO.Path.Combine(nested, "safe.inf"), "inside");
        await File.WriteAllTextAsync(System.IO.Path.Combine(outside.Path, "outside.inf"), "outside fixture");
        void ReplaceWithJunction()
        {
            Directory.Move(nested, saved);
            CreateJunction(nested, outside.Path);
        }
        try
        {
            // Negative control: the original attribute check + path-based open follows this replacement.
            Assert.Equal((FileAttributes)0, File.GetAttributes(nested) & FileAttributes.ReparsePoint);
            ReplaceWithJunction();
            Assert.Equal("outside fixture", await File.ReadAllTextAsync(System.IO.Path.Combine(nested, "outside.inf")));
            Directory.Delete(nested); Directory.Move(saved, nested);

            bool attempted = false, replaced = false;
            var error = await Record.ExceptionAsync(() => DriverStoreService.HashOutputAsync(temp.Path, CancellationToken.None,
                observe: (stage, path) =>
                {
                    if (!replaced && stage == DriverStoreService.HashStage.EntryAttributesRead && string.Equals(path, nested, StringComparison.OrdinalIgnoreCase))
                    { attempted = true; ReplaceWithJunction(); replaced = true; }
                }));
            Assert.True(attempted); Assert.NotNull(error);
            if (replaced) Assert.Contains("reparse point", Assert.IsType<IOException>(error).Message);
            else Assert.True(IsSharingViolation(error), error.ToString());
            Assert.Equal("outside fixture", await File.ReadAllTextAsync(System.IO.Path.Combine(outside.Path, "outside.inf")));
        }
        finally
        {
            if (Directory.Exists(nested) && (File.GetAttributes(nested) & FileAttributes.ReparsePoint) != 0) Directory.Delete(nested);
        }
    }

    [Fact]
    public async Task LeafHardLinkSwapAfterMetadataWorksAgainstLegacyOpenButValidatedHandleRejectsIt()
    {
        using var temp = new TempFolder(); using var outside = new TempFolder();
        string leaf = System.IO.Path.Combine(temp.Path, "driver.inf");
        string other = System.IO.Path.Combine(outside.Path, "outside.inf");
        await File.WriteAllTextAsync(other, "outside fixture");
        await File.WriteAllTextAsync(leaf, "inside");
        void ReplaceWithHardLink()
        {
            File.Delete(leaf);
            if (!Native.CreateHardLink(leaf, other, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        Assert.Equal((FileAttributes)0, File.GetAttributes(leaf) & FileAttributes.ReparsePoint);
        ReplaceWithHardLink();
        Assert.Equal("outside fixture", await File.ReadAllTextAsync(leaf)); // Legacy path-open negative control.
        File.Delete(leaf); await File.WriteAllTextAsync(leaf, "inside");
        bool attempted = false, replaced = false;
        var error = await Record.ExceptionAsync(() => DriverStoreService.HashOutputAsync(temp.Path, CancellationToken.None,
            observe: (stage, path) =>
            {
                if (!replaced && stage == DriverStoreService.HashStage.EntryAttributesRead && string.Equals(path, leaf, StringComparison.OrdinalIgnoreCase))
                { attempted = true; ReplaceWithHardLink(); replaced = true; }
            }));
        Assert.True(attempted); Assert.NotNull(error);
        if (replaced) Assert.Contains("hard link", Assert.IsType<IOException>(error).Message);
        else Assert.True(IsSharingViolation(error), error.ToString());
        Assert.Equal("outside fixture", await File.ReadAllTextAsync(other));
    }

    [Fact]
    public async Task ReparseDirectoryHandleIsRejectedEvenWhenEarlierMetadataOmitsItsTag()
    {
        using var temp = new TempFolder(); using var outside = new TempFolder();
        string link = System.IO.Path.Combine(temp.Path, "link");
        await File.WriteAllTextAsync(System.IO.Path.Combine(outside.Path, "outside.inf"), "outside fixture");
        CreateJunction(link, outside.Path);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => DriverStoreService.HashOutputAsync(temp.Path, CancellationToken.None,
                readAttributes: path => File.GetAttributes(path) & ~FileAttributes.ReparsePoint));
            Assert.Contains("reparse point", error.Message);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task RealHardLinkMustBeRejectedByTheOpenedHandleLinkCount()
    {
        using var temp = new TempFolder(); using var outside = new TempFolder();
        string other = System.IO.Path.Combine(outside.Path, "outside.inf");
        await File.WriteAllTextAsync(other, "outside fixture");
        if (!Native.CreateHardLink(System.IO.Path.Combine(temp.Path, "driver.inf"), other, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var error = await Assert.ThrowsAsync<IOException>(() => DriverStoreService.HashOutputAsync(temp.Path, CancellationToken.None));
        Assert.Contains("hard link", error.Message);
    }

    private static bool IsSharingViolation(Exception error) =>
        error is Win32Exception { NativeErrorCode: 32 } || error is IOException io && (io.HResult & 0xFFFF) == 32;

    // A junction uses the mount-point tag and does not require the symbolic-link creation privilege.
    // All endpoints here are newly-created temp directories on the same local test volume.
    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + System.IO.Path.GetFullPath(target).TrimEnd('\\'));
        byte[] print = Encoding.Unicode.GetBytes(System.IO.Path.GetFullPath(target).TrimEnd('\\'));
        int dataLength = 8 + substitute.Length + 2 + print.Length + 2;
        byte[] buffer = new byte[8 + dataLength];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0); // IO_REPARSE_TAG_MOUNT_POINT
        BitConverter.GetBytes(checked((ushort)dataLength)).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 16 + substitute.Length + 2);
        using var handle = Native.CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Native.DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLink(string name, string existing, IntPtr security);
    }
}
