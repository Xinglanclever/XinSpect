using System.ComponentModel;
using System.IO;
using System.Security;
using System.Text.Json;
namespace XinSpect;

/// <summary>Single-worker bounded DFS. No startup scan, MFT access, deletion, modification or network upload.</summary>
public sealed class DiskSpaceScanService(IDiskSpaceFileSystem? fileSystem = null, TimeProvider? timeProvider = null)
{
    private readonly IDiskSpaceFileSystem _fileSystem = fileSystem ?? new DiskSpaceWindowsFileSystem();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<DiskSpaceScanResult> ScanAsync(string directory, DiskSpaceScanOptions? options = null,
        IProgress<DiskSpaceProgress>? progress = null, CancellationToken cancellationToken = default)
        // Do not pass the token to Task.Run: a pre-cancelled scan still returns a labelled partial result.
        => Task.Run(() => Scan(directory, options, progress, cancellationToken));

    public DiskSpaceScanResult Scan(string directory, DiskSpaceScanOptions? options = null,
        IProgress<DiskSpaceProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); options.Validate();
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("請指定完整的目錄路徑。", nameof(directory));
        // The explorer accepts filesystem paths, not raw-device or GLOBALROOT namespaces.
        if (directory.StartsWith(@"\\?\", StringComparison.Ordinal) || directory.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("請選擇一般磁碟/UNC 目錄；不接受裝置命名空間。", nameof(directory));
        string normalized = Path.GetFullPath(directory);
        // Windows also normalizes slash aliases such as //?/GLOBALROOT and //./PhysicalDrive0.
        // Checking only the original spelling is not a device-namespace boundary.
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) || normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("請選擇一般磁碟/UNC 目錄；不接受裝置命名空間。", nameof(directory));
        if (normalized.Length > 32_767) throw new PathTooLongException("目錄路徑超過本掃描器的 32,767 字元上限。");
        string rootPath = Path.TrimEndingDirectorySeparator(normalized);
        var started = _time.GetUtcNow(); long stamp = _time.GetTimestamp();
        TimeSpan Elapsed() => _time.GetElapsedTime(stamp);
        var issues = new List<DiskSpaceIssue>(); long issueCount = 0, entries = 0, files = 0, logical = 0, hardlinks = 0;
        var fileIds = new HashSet<DiskSpaceFileIdentity>(); var directoryIds = new HashSet<DiskSpaceFileIdentity>();
        var types = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        var biggest = new PriorityQueue<DiskSpaceNode, long>();
        bool stopped = false, cancelled = false, memoryLimited = false; TimeSpan lastProgress = TimeSpan.MinValue;
        // Includes final diagnostics/container slack; the emergency MemoryLimit issue is prepaid here.
        long estimatedMetadataBytes = 64L * 1024 + options.MaxBigFiles * 64L;

        void MemoryLimit()
        {
            if (memoryLimited) return;
            memoryLimited = stopped = true; issueCount++;
            var issue = new DiskSpaceIssue(rootPath, DiskSpaceIssueKind.MemoryLimit,
                "達到保留中繼資料的估算預算；已停止讀取/保留後續項目，結果不完整。此預算不是 CLR heap 硬上限。");
            if (issues.Count < options.MaxIssues) issues.Add(issue);
            else issues[^1] = issue; // Keep the stop reason even when the ordinary issue list is full.
        }
        bool Reserve(long bytes)
        {
            if (bytes > options.MaxRetainedMetadataBytes - estimatedMetadataBytes) { MemoryLimit(); return false; }
            estimatedMetadataBytes += bytes; return true;
        }
        static string BoundReason(string text) => text.Length <= 1024 ? text : text[..1019] + " [截斷]";

        void AddIssue(string path, DiskSpaceIssueKind kind, string reason, int? nativeError = null)
        {
            issueCount++;
            if (issues.Count >= options.MaxIssues) return;
            reason = BoundReason(reason);
            if (Reserve(256L + 2L * (path.Length + reason.Length))) issues.Add(new(path, kind, reason, nativeError));
        }
        bool Stop(string path, bool checkEntries = true)
        {
            if (stopped) return true;
            if (cancellationToken.IsCancellationRequested)
            { cancelled = stopped = true; AddIssue(path, DiskSpaceIssueKind.Cancelled, "使用者要求取消；保留已完成的統計。"); }
            else if (Elapsed() >= options.MaxDuration)
            { stopped = true; AddIssue(path, DiskSpaceIssueKind.TimeLimit, "達到時間預算；未列舉的項目數未知。"); }
            else if (checkEntries && entries >= options.MaxEntries)
            { stopped = true; AddIssue(path, DiskSpaceIssueKind.EntryLimit, "達到項目預算；未列舉的項目數未知。"); }
            return stopped;
        }
        void Publish(string path, bool force = false)
        {
            var elapsed = Elapsed();
            if (force || lastProgress == TimeSpan.MinValue || elapsed - lastProgress >= TimeSpan.FromMilliseconds(150))
            { lastProgress = elapsed; progress?.Report(new(entries, files, logical, elapsed, path, estimatedMetadataBytes)); }
        }
        long Add(long a, long b, string path)
        {
            try { return checked(a + b); }
            catch (OverflowException) { AddIssue(path, DiskSpaceIssueKind.SizeOverflow, "位元組加總超出 Int64；此欄已飽和，不能視為精確值。"); return long.MaxValue; }
        }
        void Error(DiskSpaceNode node, Exception ex)
        {
            int? code = ex is Win32Exception win ? win.NativeErrorCode : null;
            bool denied = ex is UnauthorizedAccessException or SecurityException || code == 5;
            node.IsPartial = true; node.Note = BoundReason((denied ? "權限不足：" : "讀取失敗：") + ex.Message);
            AddIssue(node.Path, denied ? DiskSpaceIssueKind.AccessDenied : DiskSpaceIssueKind.ReadError, node.Note, code);
        }

        DiskSpaceNode? Visit(string path, int depth, bool root = false)
        {
            if (!Reserve(4096L + 4L * path.Length)) return null;
            var node = new DiskSpaceNode(path, root);
            if (Stop(path)) { node.IsPartial = true; node.Note = "本次預算/取消後未讀取。"; return node; }
            entries++; Publish(path);
            try
            {
                using var opened = _fileSystem.Open(path);
                var metadata = opened.Metadata;
                node = new(path, metadata.IsDirectory);
                if (metadata.IsReparsePoint)
                {
                    node.IsPartial = true; node.Note = "重解析點未追蹤（包含符號連結、junction、掛載點及雲端預留位置）。";
                    AddIssue(path, DiskSpaceIssueKind.ReparsePoint, node.Note); return node;
                }
                if (root && !metadata.IsDirectory) throw new IOException("所選根路徑不是目錄。");
                if (!metadata.IsDirectory)
                {
                    if (metadata.LogicalBytes is not { } size || size < 0 || metadata.AllocatedBytes is < 0)
                        throw new IOException("檔案長度未知或格式無效，未當成 0 納入。");
                    node.LogicalBytes = size; node.FileCount = 1; files++; logical = Add(logical, size, path);
                    node.ReportedAllocatedBytes = metadata.AllocatedBytes;
                    if (metadata.Identity is null || metadata.AllocatedBytes is null)
                    {
                        node.UnknownAllocationFiles = 1; node.IsPartial = true;
                        node.Note = BoundReason(metadata.IdentityError ?? "缺少檔案身分或配置大小；未加入唯一配置總數。");
                        AddIssue(path, DiskSpaceIssueKind.IdentityUnavailable, node.Note);
                    }
                    else if (fileIds.Add(metadata.Identity)) node.UniqueAllocatedBytes = metadata.AllocatedBytes.Value;
                    else { node.IsHardLinkDuplicate = true; hardlinks++; node.Note = "同一 volume + file ID 已計入另一檔名；本路徑的唯一配置貢獻為 0。"; }
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension.Length == 0) extension = "（無副檔名）";
                    if (!types.TryGetValue(extension, out var sums)) types[extension] = sums = new long[4];
                    sums[0]++; sums[1] = Add(sums[1], size, path); sums[2] = Add(sums[2], node.UniqueAllocatedBytes, path); sums[3] += node.UnknownAllocationFiles;
                    biggest.Enqueue(node, size); if (biggest.Count > options.MaxBigFiles) biggest.Dequeue();
                    return node;
                }
                if (metadata.Identity is { } directoryId && !directoryIds.Add(directoryId))
                {
                    node.IsPartial = true; node.Note = "目錄身分重複，拒絕重複遍歷。";
                    AddIssue(path, DiskSpaceIssueKind.DuplicateDirectory, node.Note); return node;
                }
                if (depth >= options.MaxDepth)
                {
                    node.IsPartial = true; node.Note = "已達深度上限，未列舉此目錄內容。";
                    AddIssue(path, DiskSpaceIssueKind.DepthLimit, node.Note); return node;
                }
                // Directory handles remain alive throughout this recursive scope. Enumerators and handles
                // are disposed on errors, cancellation and all budget exits.
                using var enumerator = opened.EnumerateChildren().GetEnumerator();
                while (!Stop(path, checkEntries: false))
                {
                    if (!enumerator.MoveNext()) break;
                    if (Stop(path)) break;
                    string rawChildPath = enumerator.Current;
                    if (rawChildPath.Length > 32_767) throw new PathTooLongException("子項路徑超過本掃描器的 32,767 字元上限。");
                    string childPath = Path.GetFullPath(rawChildPath);
                    if (!string.Equals(Path.GetDirectoryName(childPath), path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("列舉器回報非直接子項目，已拒絕越界遍歷。");
                    var child = Visit(childPath, depth + 1);
                    if (child is null) break;
                    child.Parent = node; node.Children.Add(child);
                    node.LogicalBytes = Add(node.LogicalBytes, child.LogicalBytes, path);
                    node.UniqueAllocatedBytes = Add(node.UniqueAllocatedBytes, child.UniqueAllocatedBytes, path);
                    node.FileCount += child.FileCount; node.UnknownAllocationFiles += child.UnknownAllocationFiles;
                    node.IsPartial |= child.IsPartial;
                }
                if (stopped) node.IsPartial = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or NotSupportedException or ArgumentException)
            { Error(node, ex); }
            return node;
        }

        // Validation's 1 MiB minimum is sufficient for the maximum-length root plus all fixed reserves.
        var rootNode = Visit(rootPath, 0, root: true) ?? throw new InvalidOperationException("根目錄模型預留失敗。");
        // Check cancellation/time even if the last metadata call completed after the budget.
        Stop(rootPath, checkEntries: false); if (stopped || issueCount > 0) rootNode.IsPartial = true;
        Publish(rootPath, force: true);
        var result = new DiskSpaceScanResult(rootPath, started, Elapsed(), cancelled ? DiskSpaceScanState.Cancelled : rootNode.IsPartial ? DiskSpaceScanState.Partial : DiskSpaceScanState.Complete,
            options, rootNode, entries, hardlinks,
            types.Select(kv => new DiskSpaceTypeRow(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3])).OrderByDescending(x => x.LogicalBytes).ToArray(),
            biggest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.LogicalBytes).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(), issues, issueCount, estimatedMetadataBytes);
        return result;
    }

    /// <summary>In-memory report only. Caller must obtain an explicit user-selected output path before writing.</summary>
    public static string CreateJsonReport(DiskSpaceScanResult result) => JsonSerializer.Serialize(new
    {
        schema = "xinspect.disk-space.v1", result.RootPath, result.StartedAtUtc, elapsedSeconds = result.Elapsed.TotalSeconds,
        state = result.State.ToString(), result.Options, method = DiskSpaceScanResult.Method, limits = DiskSpaceScanResult.Limits,
        result.EstimatedRetainedMetadataBytes, memoryAccounting = DiskSpaceScanResult.MemoryAccounting,
        result.EntriesVisited, result.HardLinkDuplicates, result.TotalIssues, result.FileTypes,
        summary = new { result.Root.LogicalBytes, result.Root.UniqueAllocatedBytes, result.Root.UnknownAllocationFiles, result.Root.FileCount },
        biggestFiles = result.BiggestFiles.Select(n => new { n.Path, n.LogicalBytes, n.ReportedAllocatedBytes, n.UniqueAllocatedBytes, n.IsHardLinkDuplicate, n.UnknownAllocationFiles }),
        issues = result.Issues.Select(i => new { i.Path, kind = i.Kind.ToString(), i.Reason, i.NativeError })
    }, new JsonSerializerOptions { WriteIndented = true });
}
