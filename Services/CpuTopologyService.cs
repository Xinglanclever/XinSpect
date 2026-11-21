using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 以 Win32 原生 API 查詢 CPU 的實體拓撲與快取階層：
/// <c>GetLogicalProcessorInformationEx</c>（封裝/核心/快取/NUMA 關係）、
/// <c>GetActiveProcessorGroupCount</c>、<c>IsProcessorFeaturePresent</c>（平台能力旗標）。
/// 全部為作業系統即時回報的真實資料，不依賴 CPU-Z 報告，故任何機器都可讀到。
/// </summary>
public static class CpuTopologyService
{
    private enum RelKind { ProcessorCore = 0, NumaNode = 1, Cache = 2, ProcessorPackage = 3, Group = 4, All = 0xffff }
    private enum CacheType { Unified = 0, Instruction = 1, Data = 2, Trace = 3 }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(RelKind relationship, nint buffer, ref uint returnedLength);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern bool IsProcessorFeaturePresent(uint feature);

    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    public static CpuTopology Build()
    {
        var t = new CpuTopology { LogicalProcessors = Environment.ProcessorCount };
        try
        {
            t.ProcessorGroups = GetActiveProcessorGroupCount();
            ReadFeatures(t);
            ReadTopology(t);
            t.Loaded = t.PhysicalCores > 0 || t.Caches.Count > 0 || t.Features.Count > 0;
        }
        catch { /* 原生查詢失敗：維持 Loaded=false，UI 隱藏此卡 */ }
        return t;
    }

    // 逐筆走訪變長的 SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX 緩衝區（每筆前 8 位元組為 Relationship+Size）
    private static void ReadTopology(CpuTopology t)
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(RelKind.All, 0, ref len);
        if (!WindowsTopologyBuffer.IsWithinBudget(len) || Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER) return;

        uint capacity = len;
        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelKind.All, buf, ref len) || len > capacity) return;

            if (len > WindowsTopologyBuffer.MaxBufferBytes) return;
            var bytes = new byte[(int)len];
            Marshal.Copy(buf, bytes, 0, bytes.Length);
            var parsed = ParseTopology(bytes);
            if (parsed is null) return;
            t.PhysicalPackages = parsed.PhysicalPackages;
            t.PhysicalCores = parsed.PhysicalCores;
            t.NumaNodes = parsed.NumaNodes;
            t.Smt = parsed.Smt;
            t.Caches.AddRange(parsed.Caches);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    internal static CpuTopology? ParseTopology(byte[] bytes)
    {
        var records = WindowsTopologyBuffer.Parse(bytes);
        if (records is null) return null;
        var t = new CpuTopology();
        var nodes = new HashSet<int>();
        var caches = new Dictionary<(int Level, int Type, uint Size, int Assoc, int Line, int Shared), int>();
        foreach (var rec in records)
        {
            if (rec.Relationship == 3) t.PhysicalPackages++;
            if (rec.Relationship is 1 or 6) nodes.Add(rec.Aux);
            if (rec.Relationship == 0) { t.PhysicalCores++; t.Smt |= (rec.Data[8] & 1) != 0; }
            if (rec.Relationship != 2) continue;
            var data = rec.Data;
            var key = ((int)data[8], BitConverter.ToInt32(data, 16), BitConverter.ToUInt32(data, 12),
                (int)data[9], (int)BitConverter.ToUInt16(data, 10),
                rec.Affinities.Sum(a => System.Numerics.BitOperations.PopCount(a.Mask)));
            caches[key] = caches.GetValueOrDefault(key) + 1;
        }
        t.NumaNodes = nodes.Count;
        foreach (var (key, count) in caches.OrderBy(k => k.Key.Level).ThenBy(k => k.Key.Type))
            t.Caches.Add(new CpuCacheRow { Label = CacheLabel(key.Level, (CacheType)key.Type),
                Detail = CacheDetail(count, key.Size, key.Assoc, key.Line, key.Shared) });
        return t;
    }

    private static string CacheLabel(int level, CacheType type) => type switch
    {
        CacheType.Data => $"L{level} 資料快取",
        CacheType.Instruction => $"L{level} 指令快取",
        CacheType.Trace => $"L{level} 追蹤快取",
        _ => $"L{level} 快取"
    };

    private static string CacheDetail(int count, long sizeBytes, int assoc, int line, int shared)
    {
        string per = SizeText(sizeBytes);
        string total = count > 1 ? $"{count} × {per}（合計 {SizeText((long)sizeBytes * count)}）" : per;
        string assocText = assoc == 0xFF ? "全關聯" : assoc > 0 ? $"{assoc}-way 組關聯" : "—";
        string sharedText = shared > 1 ? $"每 {shared} 執行緒共用" : "每核心獨立";
        return $"{total} ・ {assocText} ・ {line} B 快取行 ・ {sharedText}";
    }

    private static string SizeText(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.##} KB";
        return $"{bytes} B";
    }

    private static int PopCount(long v)
    {
        int c = 0; ulong u = (ulong)v;
        while (u != 0) { c += (int)(u & 1); u >>= 1; }
        return c;
    }

    // IsProcessorFeaturePresent 的常見 x86/x64 旗標 → 繁中名稱（僅列出回報為支援者）
    private static void ReadFeatures(CpuTopology t)
    {
        (uint id, string name)[] map =
        {
            (3,  "MMX"), (6, "SSE"), (10, "SSE2"), (13, "SSE3"),
            (7,  "3DNow!"), (14, "CMPXCHG16B"), (8, "RDTSC"), (32, "RDTSCP"),
            (28, "RDRAND"), (9, "PAE"), (12, "NX / DEP 資料防護"), (17, "XSAVE"),
            (20, "二階位址轉譯 SLAT"), (21, "韌體虛擬化"), (22, "FSGSBASE"), (23, "FastFail"),
        };
        foreach (var (id, name) in map)
        {
            try { if (IsProcessorFeaturePresent(id)) t.Features.Add(name); }
            catch (Exception ex) { Diag.Swallow("查詢處理器功能位元", ex, $"「{name}」未列入功能清單（不代表這台機器沒有）"); }
        }
    }
}

// Parses copied native records only; no native pointers escape this boundary.
internal static class WindowsTopologyBuffer
{
    // A bounded snapshot supports hundreds of thousands of records without native-size allocations.
    internal const int MaxBufferBytes = 16 * 1024 * 1024;
    internal static bool IsWithinBudget(uint length) => length > 0 && length <= MaxBufferBytes;
    internal sealed record Record(int Relationship, int Aux, byte[] Data,
        IReadOnlyList<(ulong Mask, ushort Group)> Affinities);

    internal static List<Record>? Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBufferBytes) return null;
        var result = new List<Record>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            int remaining = bytes.Length - offset;
            if (remaining < 8) return null;
            int rel = BitConverter.ToInt32(bytes, offset);
            int size = BitConverter.ToInt32(bytes, offset + 4);
            if (size < 8 || size > remaining) return null;
            int maskOffset = rel == 2 ? 40 : 32;
            int count = 0;
            if (rel is 0 or 3 or 5 or 7) {
                if (size < 32) return null;
                count = BitConverter.ToUInt16(bytes, offset + 30);
                if (count == 0) return null;
            }
            else if (rel is 1 or 6 or 2) {
                if (size < maskOffset) return null;
                count = BitConverter.ToUInt16(bytes, offset + maskOffset - 2);
                // Older NUMA/cache records use a single GroupMask and reserved zero count.
                if (count == 0) count = 1;
            }
            if (count > 0 && count > (size - maskOffset) / 16) return null;
            var affinities = new List<(ulong, ushort)>();
            var groups = new HashSet<ushort>();
            for (int i = 0; i < count; i++) {
                int start = offset + maskOffset + i * 16;
                ulong mask = BitConverter.ToUInt64(bytes, start);
                ushort group = BitConverter.ToUInt16(bytes, start + 8);
                if (!groups.Add(group)) return null;
                // NUMA nodes may be memory-only; processor/cache identities require actual processors.
                if (mask == 0 && rel is 0 or 2 or 3 or 5 or 7) return null;
                affinities.Add((mask, group));
            }
            result.Add(new Record(rel, size >= 12 ? BitConverter.ToInt32(bytes, offset + 8) : 0,
                bytes.AsSpan(offset, size).ToArray(), affinities));
            offset += size;
        }
        return result;
    }
}
