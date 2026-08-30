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
        if (len == 0 || Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER) return;

        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelKind.All, buf, ref len)) return;

            // 快取以 (階層, 型態) 匯總：實例數、大小、關聯度、快取行、共用執行緒數
            var caches = new Dictionary<(int lvl, CacheType type), (int count, int size, int assoc, int line, int shared)>();

            int off = 0;
            while (off + 8 <= (int)len)
            {
                nint rec = buf + off;
                int rel = Marshal.ReadInt32(rec);
                int size = Marshal.ReadInt32(rec + 4);
                if (size <= 0) break;
                nint pl = rec + 8;   // payload 起點

                switch ((RelKind)rel)
                {
                    case RelKind.ProcessorPackage:
                        t.PhysicalPackages++;
                        break;

                    case RelKind.NumaNode:
                        t.NumaNodes++;
                        break;

                    case RelKind.ProcessorCore:
                    {
                        t.PhysicalCores++;
                        byte flags = Marshal.ReadByte(pl);        // bit0 = LTP_PC_SMT（此核心含多個邏輯處理器）
                        if ((flags & 0x1) != 0) t.Smt = true;
                        break;
                    }

                    case RelKind.Cache:
                    {
                        byte level = Marshal.ReadByte(pl);          // +0
                        byte assoc = Marshal.ReadByte(pl + 1);      // +1（0xFF=全關聯）
                        ushort line = (ushort)Marshal.ReadInt16(pl + 2);  // +2
                        int cacheSize = Marshal.ReadInt32(pl + 4);  // +4（位元組）
                        var type = (CacheType)Marshal.ReadInt32(pl + 8);  // +8
                        long mask = Marshal.ReadIntPtr(pl + 32).ToInt64(); // GROUP_AFFINITY.Mask（+32）
                        int shared = PopCount(mask);
                        var key = (level, type);
                        if (caches.TryGetValue(key, out var cur))
                            caches[key] = (cur.count + 1, cacheSize, assoc, line, shared);
                        else
                            caches[key] = (1, cacheSize, assoc, line, shared);
                        break;
                    }
                }
                off += size;
            }

            foreach (var kv in caches
                         .OrderBy(k => k.Key.lvl)
                         .ThenBy(k => k.Key.type == CacheType.Instruction ? 0 : 1))
            {
                var (lvl, type) = kv.Key;
                var (count, sz, assoc, line, shared) = kv.Value;
                t.Caches.Add(new CpuCacheRow { Label = CacheLabel(lvl, type), Detail = CacheDetail(count, sz, assoc, line, shared) });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string CacheLabel(int level, CacheType type) => type switch
    {
        CacheType.Data => $"L{level} 資料快取",
        CacheType.Instruction => $"L{level} 指令快取",
        CacheType.Trace => $"L{level} 追蹤快取",
        _ => $"L{level} 快取"
    };

    private static string CacheDetail(int count, int sizeBytes, int assoc, int line, int shared)
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
