using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace XinSpect;

/// <summary>Inventory and explicit export only. Never installs, deletes, restores, or changes driver protection.</summary>
public sealed class DriverStoreService
{
    private readonly IDriverStoreBackend _backend;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public DriverStoreService() : this(new DriverStoreNativeBackend()) { }
    public DriverStoreService(IDriverStoreBackend backend) => _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<DriverStoreInventory> ReadInventoryAsync(CancellationToken token = default)
    {
        if (!await _operationGate.WaitAsync(0, token).ConfigureAwait(false))
            return new() { Availability = FactAvailability.ReadError, Reason = "已有盤點或匯出正在執行。" };
        try
        {
            return await Task.Run(() => _backend.ReadInventory(token), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new() { Cancelled = true, Availability = FactAvailability.Unknown, Reason = "盤點已取消；沒有發布部分／過期清單。" }; }
        catch (Exception ex) { return new() { Availability = ClassifyException(ex), Reason = ex.Message }; }
        finally { _operationGate.Release(); }
    }

    public async Task<DriverStoreExportResult> ExportAsync(IReadOnlyList<DriverStorePackage> selected, string destination,
        IProgress<DriverStoreExportProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (!await _operationGate.WaitAsync(0, token).ConfigureAwait(false))
            return new(FactAvailability.ReadError, "Failed", "已有盤點或匯出正在執行。");
        string? runDirectory = null, manifestPath = null;
        var directoryPins = new List<SafeFileHandle>();
        var manifest = new DriverStoreExportManifest();
        FactAvailability availability = FactAvailability.ReadError;
        try
        {
            token.ThrowIfCancellationRequested();
            if (selected.Count == 0 || selected.Count > 32768) throw new ArgumentException("請選擇至少一個且不超過 32768 個套件。");
            var requested = selected.ToArray(); // Freeze the UI selection before any await.
            if (requested.Any(p => !p.CanExport) || requested.Select(p => p.PublishedName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
                throw new ArgumentException("僅可匯出無重複、已確認的第三方 oem#.inf 套件；Windows 隨附套件不支援此匯出介面。");
            string root = ValidateDestination(destination);
            // Hold every directory ancestor without write/delete sharing during an elevated export.
            // A junction/rename replacement must fail, rather than redirecting PnPUtil to another path.
            for (DirectoryInfo? d = new(root); d != null; d = d.Parent) directoryPins.Add(PinDirectory(d.FullName));
            root = ValidateDestination(root);
            progress?.Report(new(0, requested.Length, "重新核對已選套件身分…"));
            var fresh = await Task.Run(() => _backend.ReadInventory(token), CancellationToken.None).ConfigureAwait(false);
            if (fresh.Availability != FactAvailability.Present || fresh.Cancelled)
                return new(fresh.Availability, fresh.Cancelled ? "Cancelled" : "Failed", "匯出前重新盤點失敗：" + fresh.Reason);
            foreach (var p in requested)
            {
                var matches = fresh.Packages.Where(n => string.Equals(n.PublishedName, p.PublishedName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !matches[0].CanExport || !SameIdentity(p, matches[0]))
                    throw new InvalidDataException($"{p.PublishedName} 已變更或不再存在；請重新盤點及選取。");
                manifest.Packages.Add(new() { Package = matches[0], Directory = p.PublishedName });
            }
            token.ThrowIfCancellationRequested();
            // Never reuse an existing output, even an empty directory. CreateDirectoryW provides exclusive creation.
            ValidateDestination(root);
            runDirectory = Path.Combine(root, "XinSpect-drivers-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..12]);
            CreateNewDirectory(runDirectory);
            directoryPins.Add(PinDirectory(runDirectory));
            manifestPath = Path.Combine(runDirectory, "manifest.json");
            int completed = 0;
            foreach (var entry in manifest.Packages)
            {
                token.ThrowIfCancellationRequested();
                EnsureNoReparseAncestors(runDirectory);
                string packageDirectory = Path.Combine(runDirectory, entry.Directory);
                CreateNewDirectory(packageDirectory);
                directoryPins.Add(PinDirectory(packageDirectory));
                entry.Status = "Exporting";
                progress?.Report(new(completed, requested.Length, "正在匯出 " + entry.Package.PublishedName));
                try
                {
                    var result = await _backend.ExportPackageAsync(entry.Package.PublishedName, packageDirectory, token).ConfigureAwait(false);
                    entry.ExitCode = result.ExitCode;
                    // Localized output is retained as diagnostics only, never parsed for success or metadata.
                    entry.Detail = result.Output.Length > 32768 ? result.Output[..32768] : result.Output;
                    if (result.ExitCode != 0)
                        throw new Win32Exception(result.ExitCode, $"PnPUtil 結束碼 {result.ExitCode}；部分輸出保留，未列為成功備份。");
                    entry.Files = await HashOutputAsync(packageDirectory, token).ConfigureAwait(false);
                    if (!entry.Files.Any(f => string.Equals(Path.GetExtension(f.RelativePath), ".inf", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("PnPUtil 回報成功，但輸出中沒有 INF；未列為成功備份。");
                    entry.Status = "Exported";
                    completed++;
                    progress?.Report(new(completed, requested.Length, "已匯出並計算 SHA-256：" + entry.Package.PublishedName));
                }
                catch (OperationCanceledException) { entry.Status = "Cancelled"; entry.Detail = "使用者取消；此目錄可能含部分檔案。"; throw; }
                catch (Exception ex) { entry.Status = "Failed"; entry.Detail += "\n" + ex.Message; throw; }
            }
            availability = FactAvailability.Present;
            manifest.Status = "Completed";
            manifest.Detail = $"已匯出 {completed} 個套件；manifest 記錄實際輸出及 SHA-256。此備份未經還原驗證。";
        }
        catch (OperationCanceledException)
        {
            availability = FactAvailability.Unknown; manifest.Status = "Cancelled";
            manifest.Detail = "已取消；未完成目錄保留供檢查，請勿當成完整備份。";
        }
        catch (Exception ex)
        {
            availability = ClassifyException(ex); manifest.Status = "Failed"; manifest.Detail = ex.Message;
        }
        finally
        {
            manifest.CompletedAtUtc = DateTimeOffset.UtcNow;
            foreach (var pending in manifest.Packages.Where(p => p.Status == "Pending")) pending.Status = "NotAttempted";
            if (runDirectory != null)
            {
                try
                {
                    EnsureNoReparseAncestors(runDirectory);
                    // No cancellation here: record the final cancellation/failure after the OS exporter has stopped.
                    await using var file = new FileStream(manifestPath!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await JsonSerializer.SerializeAsync(file, manifest, JsonOptions, CancellationToken.None).ConfigureAwait(false);
                    await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    availability = FactAvailability.ReadError; manifest.Status = "Failed";
                    manifest.Detail += " manifest 寫入失敗：" + ex.Message; manifestPath = null;
                }
            }
            foreach (var pin in directoryPins) pin.Dispose();
            _operationGate.Release();
        }
        return new(availability, manifest.Status, manifest.Detail, runDirectory, manifestPath, manifest);
    }

    internal static bool SameIdentity(DriverStorePackage a, DriverStorePackage b) =>
        string.Equals(a.OriginalFileName, b.OriginalFileName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Provider, b.Provider, StringComparison.Ordinal) && a.Version == b.Version &&
        a.DriverDate == b.DriverDate && a.InBox == b.InBox;

    public static bool IsPublishedOemInf(string? value) => value != null &&
        Regex.IsMatch(value, @"\Aoem[0-9]{1,10}\.inf\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static string ValidateDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination) ||
            destination.StartsWith(@"\\", StringComparison.Ordinal) || destination.IndexOf(':', 2) >= 0)
            throw new ArgumentException("請選擇本機磁碟的完整資料夾路徑（不支援 UNC、裝置路徑及 ADS）。");
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 2 && root[1] == ':') root += Path.DirectorySeparatorChar;
        if (root.Length > 170 || !Directory.Exists(root)) throw new ArgumentException("目的地必須是現有資料夾且完整路徑不超過 170 字元。");
        EnsureNoReparseAncestors(root);
        // Resolve 8.3 aliases and Win32 trailing-dot/space normalization before comparing system paths.
        using (var handle = PinDirectory(root)) root = CanonicalPath(handle);
        foreach (var system in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (system.Length != 0 && IsWithin(root, Path.GetFullPath(system)))
                throw new ArgumentException("請選擇 Windows 與 Program Files 系統目錄以外的備份目的地。");
        EnsureNoReparseAncestors(root);
        return root;
    }

    private static bool IsWithin(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void EnsureNoReparseAncestors(string path)
    {
        for (DirectoryInfo? d = new(path); d != null; d = d.Parent)
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("備份目的地不得經過 junction、符號連結或其他 reparse point。");
    }

    internal enum HashStage { EntryAttributesRead, DirectoryPinned, FileHandleValidated }

    internal static async Task<IReadOnlyList<DriverStoreFileHash>> HashOutputAsync(string directory, CancellationToken token,
        Func<string, FileAttributes>? readAttributes = null, Action<HashStage, string>? observe = null)
    {
        var hashes = new List<DriverStoreFileHash>();
        var ancestorPins = new List<SafeFileHandle>();
        long total = 0;
        int entries = 0;
        try
        {
            // Open root-to-leaf. Once an ancestor is verified and pinned, its next child cannot be
            // replaced or redirected while opening it. This also protects standalone hash callers.
            var ancestors = new Stack<string>();
            for (DirectoryInfo? d = new(Path.GetFullPath(directory)); d != null; d = d.Parent) ancestors.Push(d.FullName);
            while (ancestors.Count != 0) ancestorPins.Add(PinDirectory(ancestors.Pop()));
            string canonicalRoot = CanonicalPath(ancestorPins[^1]);
            observe?.Invoke(HashStage.DirectoryPinned, canonicalRoot);
            await WalkAsync(canonicalRoot, 0).ConfigureAwait(false);
            return hashes.OrderBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();

            async Task WalkAsync(string current, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 64) throw new IOException("單套件目錄深度超過 64 層預算。");
                foreach (string item in Directory.EnumerateFileSystemEntries(current))
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > 100000) throw new IOException("單套件輸出超過 100000 個檔案及目錄預算。");
                    var attributes = readAttributes is null ? File.GetAttributes(item) : readAttributes(item);
                    observe?.Invoke(HashStage.EntryAttributesRead, item);
                    // This is only an early rejection. Security decisions below use the actual opened handle.
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("輸出含 reparse point，拒絕遞迴或計算雜湊。");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        using var childPin = PinDirectory(item);
                        string childPath = CanonicalPath(childPin);
                        RequireContained(childPath, canonicalRoot);
                        observe?.Invoke(HashStage.DirectoryPinned, childPath);
                        await WalkAsync(childPath, depth + 1).ConfigureAwait(false);
                        continue;
                    }

                    // OPEN_REPARSE_POINT prevents leaf-link following. Validate attributes and canonical
                    // containment on this same handle, then hash from it: never reopen the pathname.
                    using var handle = OpenHashFile(item);
                    var info = ReadFileInformation(handle);
                    if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0)
                        throw new IOException("輸出檔案 handle 是 reparse point 或目錄，拒絕雜湊。");
                    // A hard link can name an outside file without a reparse tag. Exported copies must be standalone.
                    if (info.NumberOfLinks != 1) throw new IOException("輸出檔案具有 hard link 或無法確認連結數，拒絕雜湊。");
                    string actualPath = CanonicalPath(handle);
                    RequireContained(actualPath, canonicalRoot);
                    observe?.Invoke(HashStage.FileHandleValidated, actualPath);
                    await using var stream = new FileStream(handle, FileAccess.Read, 65536, isAsync: true);
                    total = checked(total + stream.Length);
                    if (total > 32L * 1024 * 1024 * 1024) throw new IOException("單套件輸出超過 32 GiB 雜湊預算。");
                    var digest = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                    hashes.Add(new(Path.GetRelativePath(canonicalRoot, actualPath), stream.Length, Convert.ToHexString(digest)));
                }
            }
        }
        finally { foreach (var pin in ancestorPins) pin.Dispose(); }
    }

    private static void RequireContained(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || !IsWithin(path, root))
            throw new IOException("輸出 handle 的實體路徑逸出備份目錄。");
    }

    private static SafeFileHandle OpenHashFile(string path)
    {
        // GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING,
        // FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OVERLAPPED | FILE_FLAG_SEQUENTIAL_SCAN.
        var handle = Native.CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x48200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new Win32Exception(error, "無法以不跟隨連結的 handle 開啟輸出檔案。");
    }

    private static NativeFileInformation ReadFileInformation(SafeFileHandle handle)
    {
        if (!Native.GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "無法讀取輸出 handle 屬性。");
        return information;
    }

    internal static FactAvailability ClassifyException(Exception ex)
    {
        int code = ex is Win32Exception w ? w.NativeErrorCode : ex.HResult & 0xFFFF;
        return ex switch
        {
            UnauthorizedAccessException => FactAvailability.InsufficientPrivilege,
            PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException => FactAvailability.NotSupported,
            _ when code is 5 or 740 or 1314 => FactAvailability.InsufficientPrivilege,
            _ => FactAvailability.ReadError,
        };
    }

    internal static void CreateNewDirectory(string path)
    {
        if (!Native.CreateDirectory(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "無法建立全新的備份目錄（不會覆寫既有輸出）。");
    }
    private static SafeFileHandle PinDirectory(string path)
    {
        // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, FILE_SHARE_READ, OPEN_EXISTING,
        // FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT.
        // Attribute-only handles do not enforce these share semantics (negative-control temp probe).
        var handle = Native.CreateFile(path, 0x81, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(error, "無法鎖定備份資料夾；請確認權限或是否被其他程式開啟寫入。");
        }
        try
        {
            var information = ReadFileInformation(handle);
            if ((information.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException("備份目錄含 reparse point，拒絕匯出。");
            if ((information.Attributes & (uint)FileAttributes.Directory) == 0)
                throw new IOException("預期備份目錄，但實際 handle 不是目錄。");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    private static string CanonicalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint length = Native.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "無法解析備份資料夾的真實路徑。");
        string path = buffer.ToString();
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("備份目的地須為本機磁碟。");
        return path;
    }
    // BY_HANDLE_FILE_INFORMATION (fileapi.h): 13 DWORDs; FILETIME is two DWORDs.
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    private static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateDirectory(string path, IntPtr securityAttributes);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation information);
    }
}
