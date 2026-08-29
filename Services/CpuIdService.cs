using System.Collections.ObjectModel;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>單一快取層的 CPUID 解碼結果（leaf 0x04 子葉）。</summary>
public sealed class CpuIdCacheRow
{
    public CpuIdCacheRow(string level, string capacity, string ways, string line, string sets, string shared, string inclusive)
    {
        Level = level; Capacity = capacity; Ways = ways; Line = line; Sets = sets; Shared = shared; Inclusive = inclusive;
    }
    public string Level { get; }
    public string Capacity { get; }
    public string Ways { get; }
    public string Line { get; }
    public string Sets { get; }
    public string Shared { get; }
    public string Inclusive { get; }
}

/// <summary>單一指令集／功能位元。</summary>
public sealed class CpuIdFeatureChip
{
    public CpuIdFeatureChip(string name, string source) { Name = name; Source = source; }
    public string Name { get; }
    public string Source { get; }   // 來源葉位（如「0x7/0 EBX.5」），誠實交代出處
}

/// <summary>單一拓樸層級（leaf 0x0B／0x1F 子葉）。</summary>
public sealed class CpuIdTopologyRow
{
    public CpuIdTopologyRow(string leaf, string level, string count, string shift)
    { Leaf = leaf; Level = level; Count = count; Shift = shift; }
    public string Leaf { get; }
    public string Level { get; }
    public string Count { get; }
    public string Shift { get; }
}

/// <summary>鍵值資訊列（品牌、供應商、位址位元等）。</summary>
public sealed class CpuIdInfoRow
{
    public CpuIdInfoRow(string key, string value) { Key = key; Value = value; }
    public string Key { get; }
    public string Value { get; }
}

/// <summary>
/// CPUID 晶片直讀：以 .NET 內建的 <see cref="X86Base.CpuId"/> 直接問矽晶片——
/// 不需要 native code、驅動或管理員權限。展開快取幾何（leaf 0x04 子葉）、指令集位元、
/// 標稱頻率（0x15／0x16）、擴充拓樸（0x0B／0x1F）、混合架構標記（0x1A）與位址位元（0x80000008）。
/// </summary>
/// <remarks>誠實界線：讀不到的葉位一律顯示「—（未實作）」——超出 CPUID 最大葉位就絕不讀取，
/// 因為超出範圍的葉會回傳無意義值或最後一個有效葉的內容。解碼函式全為純函式（見 <see cref="Decoder"/>）。</remarks>
public sealed class CpuIdService
{
    public bool Supported { get; }

    public string Vendor { get; } = "—";
    public string Brand { get; } = "—";
    public string MaxLeafText { get; } = "—";
    public string AddressBitsText { get; } = "—";
    public string FrequencyText { get; } = "—";
    public string HybridText { get; } = "—";

    public ObservableCollection<CpuIdInfoRow> Info { get; } = [];
    public ObservableCollection<CpuIdCacheRow> Caches { get; } = [];
    public ObservableCollection<CpuIdFeatureChip> Features { get; } = [];
    public ObservableCollection<CpuIdTopologyRow> Topology { get; } = [];

    public CpuIdService()
    {
        if (!X86Base.IsSupported)
        {
            Supported = false;
            Info.Add(new CpuIdInfoRow("CPUID", "此平台不支援 x86 CPUID（非 x86 或不支援內建內向函式）。"));
            return;
        }
        Supported = true;

        var maxStd = (uint)X86Base.CpuId(0, 0).Eax;
        var maxExt = (uint)(uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax;

        // 供應商：leaf 0 的 EBX＋EDX＋ECX 三段拼字串
        var r0 = X86Base.CpuId(0, 0);
        Vendor = Decoder.PackString(r0.Ebx, r0.Edx, r0.Ecx);

        // 品牌字串：0x80000002–4，12 個整數＝48 個 ASCII 字元
        if (maxExt >= 0x80000004)
        {
            var chars = new List<byte>(48);
            for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
            {
                var r = X86Base.CpuId(unchecked((int)leaf), 0);
                chars.AddRange(BitConverter.GetBytes((uint)r.Eax));
                chars.AddRange(BitConverter.GetBytes((uint)r.Ebx));
                chars.AddRange(BitConverter.GetBytes((uint)r.Ecx));
                chars.AddRange(BitConverter.GetBytes((uint)r.Edx));
            }
            Brand = Decoder.DecodeBrand(chars.ToArray());
        }

        MaxLeafText = $"標準 0x{maxStd:X} ・ 擴充 0x{maxExt:X}";

        Info.Add(new CpuIdInfoRow("供應商字串", Vendor));
        Info.Add(new CpuIdInfoRow("品牌字串（0x80000002–4）", Brand));
        Info.Add(new CpuIdInfoRow("最大葉位", MaxLeafText));

        // 快取幾何：leaf 0x04 子葉迭代，type 欄位為 0 即終止
        if (maxStd >= 0x04)
        {
            for (uint sub = 0; sub < 64; sub++)
            {
                var r = X86Base.CpuId(0x04, (int)sub);
                var row = Decoder.DecodeCacheSubleaf((uint)r.Eax, (uint)r.Ebx, (uint)r.Ecx, (uint)r.Edx);
                if (row is null) break;   // 無效子葉＝列舉終止
                Caches.Add(row);
            }
        }

        // 標稱頻率
        string freq = "—";
        if (maxStd >= 0x16)
        {
            var r = X86Base.CpuId(0x16, 0);
            freq = Decoder.DecodeFreq16((uint)r.Eax, (uint)r.Ebx, (uint)r.Ecx);
        }
        else if (maxStd >= 0x15)
        {
            var r = X86Base.CpuId(0x15, 0);
            freq = Decoder.DecodeFreq15((uint)r.Eax, (uint)r.Ebx, (uint)r.Ecx);
        }
        FrequencyText = freq;
        Info.Add(new CpuIdInfoRow("標稱頻率（0x16／0x15）", freq));

        // 位址位元
        if (maxExt >= 0x80000008)
        {
            var r = X86Base.CpuId(unchecked((int)0x80000008), 0);
            AddressBitsText = Decoder.DecodeAddressBits((uint)r.Eax);
            Info.Add(new CpuIdInfoRow("位址位元（實體／虛擬）", AddressBitsText));
        }

        // 擴充拓樸（0x1F 優先，無則 0x0B）
        uint topoLeaf = maxStd >= 0x1F ? 0x1Fu : (maxStd >= 0x0B ? 0x0Bu : 0u);
        if (topoLeaf != 0)
        {
            for (uint sub = 0; sub < 64; sub++)
            {
                var r = X86Base.CpuId((int)topoLeaf, (int)sub);
                var row = Decoder.DecodeTopologySubleaf((uint)r.Eax, (uint)r.Ebx, (uint)r.Ecx, sub);
                if (row is null) break;   // EBX 計數為 0＝最後一層之後
                Topology.Add(row);
            }
        }

        // 混合架構標記（0x1A）：12 代以後才有
        if (maxStd >= 0x1A)
        {
            var r = X86Base.CpuId(0x1A, 0);
            HybridText = Decoder.DecodeHybrid((uint)r.Eax);
        }
        Info.Add(new CpuIdInfoRow("混合架構（0x1A）", HybridText));

        // 指令集／功能位元：只列「確實支援」的（讀到為 1 才列出，來源葉位一併標明）。
        // FeatureTable 中 leaf 以負值代表擴充葉 0x8000000x；超出最大葉位者絕不讀取。
        foreach (var (leaf, sub, reg, bit, name) in Decoder.FeatureTable)
        {
            bool isExt = leaf < 0;
            uint absLeaf = (uint)Math.Abs(leaf);
            if (isExt ? absLeaf > maxExt : absLeaf > maxStd) continue;
            var r = X86Base.CpuId(leaf, sub);
            uint v = reg switch { 0 => (uint)r.Eax, 1 => (uint)r.Ebx, 2 => (uint)r.Ecx, _ => (uint)r.Edx };
            if ((v & (1u << bit)) != 0)
                Features.Add(new CpuIdFeatureChip(name, $"0x{absLeaf:X}/{sub} {(reg switch { 0 => "EAX", 1 => "EBX", 2 => "ECX", _ => "EDX" })}.{bit}"));
        }
    }

    /// <summary>CPUID 位元解碼的純函式集合（單元測試直接餘 synthetic 暫存器值）。</summary>
    public static class Decoder
    {
        /// <summary>把連續整數視為 little-endian ASCII，去除尾端空白與 NUL。</summary>
        public static string DecodeBrand(byte[] raw)
        {
            var s = System.Text.Encoding.ASCII.GetString(raw);
            s = s.TrimEnd('\0', ' ');
            return s.Length > 0 ? s : "—";
        }

        public static string PackString(int a, int b, int c)
        {
            var bytes = new List<byte>(12);
            bytes.AddRange(BitConverter.GetBytes((uint)a));
            bytes.AddRange(BitConverter.GetBytes((uint)b));
            bytes.AddRange(BitConverter.GetBytes((uint)c));
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
        }

        /// <summary>leaf 0x04 單一子葉 → 快取列；type 欄位為 0 表示子葉列舉終止（回 null）。</summary>
        public static CpuIdCacheRow? DecodeCacheSubleaf(uint eax, uint ebx, uint ecx, uint edx)
            => DecodeCacheRaw(eax, ebx, ecx, edx)?.ToRow();

        /// <summary>原始解碼（供延遲曲線的「實測 vs 宣稱」配對）；type 為 0 表示終止。</summary>
        public static RawCache? DecodeCacheRaw(uint eax, uint ebx, uint ecx, uint edx)
        {
            uint type = eax & 0x1F;
            if (type == 0) return null;
            return new RawCache(
                type, (eax >> 5) & 0x7, ((eax >> 14) & 0xFFF) + 1,
                ((ebx >> 22) & 0x3FF) + 1, ((ebx >> 12) & 0x3FF) + 1, (ebx & 0xFFF) + 1, ecx + 1, edx);
        }

        /// <summary>leaf 0x04 的原始快取幾何。</summary>
        public readonly record struct RawCache(
            uint Type, uint Level, uint SharedCores,
            uint Ways, uint Partitions, uint LineBytes, uint Sets, uint EdxFlags)
        {
            public long CapacityBytes => (long)Ways * Partitions * LineBytes * Sets;

            public string LevelName
            {
                get
                {
                    string lv = Level switch { 1 => "L1", 2 => "L2", 3 => "L3", _ => $"L{Level}" };
                    string ty = Type switch { 1 => "資料", 2 => "指令", 3 => "統一", _ => "?" };
                    return $"{lv} {ty}";
                }
            }

            public CpuIdCacheRow ToRow()
            {
                long bytes = CapacityBytes;
                string cap = bytes switch
                {
                    >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.##} MB",
                    >= 1024 => $"{bytes / 1024.0:0.#} KB",
                    _ => $"{bytes} B",
                };
                string inclusive = (EdxFlags & 0x2) != 0 ? "含括" : (EdxFlags & 0x1) != 0 ? "非含括" : "—";
                return new CpuIdCacheRow(LevelName, cap, $"{Ways} 路", $"{LineBytes} B", Sets.ToString("N0"), $"{SharedCores} 核共用", inclusive);
            }
        }

        /// <summary>leaf 0x16：base／max／bus MHz（廠商標稱值）。</summary>
        public static string DecodeFreq16(uint eax, uint ebx, uint ecx)
        {
            if (eax == 0) return "—";
            string s = $"基準 {eax} MHz";
            if (ebx > 0) s += $" ・ 最大 {ebx} MHz";
            if (ecx > 0) s += $" ・ 匯流排 {ecx} MHz";
            return s;
        }

        /// <summary>leaf 0x15：ECX(晶振 Hz) × EBX/EAX(TSC/核心 比值)＝標稱核心頻率。</summary>
        public static string DecodeFreq15(uint eax, uint ebx, uint ecx)
        {
            if (eax == 0 || ebx == 0 || ecx == 0) return "—";
            double hz = (double)ecx * ebx / eax;
            return hz >= 1e9 ? $"{hz / 1e9:0.000} GHz（由 TSC 比值推得）" : $"{hz / 1e6:0.0} MHz（由 TSC 比值推得）";
        }

        /// <summary>leaf 0x80000008 EAX：低 8 位實體位址位元、次 8 位虛擬位址位元。</summary>
        public static string DecodeAddressBits(uint eax)
        {
            uint phys = eax & 0xFF, virt = (eax >> 8) & 0xFF;
            if (phys == 0) return "—";
            return $"{phys} / {virt} bits";
        }

        /// <summary>leaf 0x0B／0x1F 子葉 → 拓樸列；EBX 計數為 0 表示列舉終止（回 null）。</summary>
        public static CpuIdTopologyRow? DecodeTopologySubleaf(uint eax, uint ebx, uint ecx, uint subleaf)
        {
            uint count = ebx & 0xFFFF;
            if (count == 0) return null;
            uint levelType = (ecx >> 8) & 0xFF;
            uint shift = eax & 0x1F;
            string level = levelType switch
            {
                1 => "執行緒（SMT）",
                2 => "核心",
                3 => "模組",
                4 => "Tile",
                5 => "Die",
                6 => "Die 群組",
                _ => $"層級 {levelType}",
            };
            return new CpuIdTopologyRow($"0x1F #{subleaf}", level, $"{count} 個", $"右移 {shift} bits");
        }

        /// <summary>leaf 0x1A：混合架構的核心類型。</summary>
        public static string DecodeHybrid(uint eax)
        {
            uint type = eax & 0xFF;
            uint nativeModel = (eax >> 8) & 0xFF;
            return type switch
            {
                0 => "—",
                0x20 => $"E-core（效率核，native model 0x{nativeModel:X2}）",
                0x40 => $"P-core（效能核，native model 0x{nativeModel:X2}）",
                _ => $"類型 0x{type:X2}",
            };
        }

        /// <summary>功能位元對照表（leaf 取負值代表擴充葉 0x8000000x）。四元組不得重複。</summary>
        public static readonly (int Leaf, int SubLeaf, int Reg, int Bit, string Name)[] FeatureTable =
        {
            // leaf 1 EDX
            (1, 0, 3, 0, "FPU"), (1, 0, 3, 4, "TSC"), (1, 0, 3, 5, "MSR"), (1, 0, 3, 6, "PAE"),
            (1, 0, 3, 13, "PGE"), (1, 0, 3, 15, "CMOV"), (1, 0, 3, 19, "CLFSH"),
            (1, 0, 3, 23, "MMX"), (1, 0, 3, 25, "SSE"), (1, 0, 3, 26, "SSE2"), (1, 0, 3, 28, "HTT"),
            // leaf 1 ECX
            (1, 0, 2, 0, "SSE3"), (1, 0, 2, 9, "SSSE3"), (1, 0, 2, 12, "FMA3"), (1, 0, 2, 13, "CX16"),
            (1, 0, 2, 19, "SSE4.1"), (1, 0, 2, 20, "SSE4.2"), (1, 0, 2, 22, "MOVBE"),
            (1, 0, 2, 23, "POPCNT"), (1, 0, 2, 25, "AES-NI"), (1, 0, 2, 28, "AVX"), (1, 0, 2, 29, "F16C"), (1, 0, 2, 30, "RDRAND"),
            // leaf 7 子葉 0 EBX
            (7, 0, 1, 0, "FSGSBASE"), (7, 0, 1, 2, "SGX"), (7, 0, 1, 3, "BMI1"), (7, 0, 1, 4, "HLE"),
            (7, 0, 1, 5, "AVX2"), (7, 0, 1, 7, "SMEP"), (7, 0, 1, 8, "BMI2"), (7, 0, 1, 9, "ERMS"),
            (7, 0, 1, 10, "INVPCID"), (7, 0, 1, 11, "RTM"), (7, 0, 1, 16, "AVX-512F"),
            (7, 0, 1, 17, "AVX-512DQ"), (7, 0, 1, 18, "RDSEED"), (7, 0, 1, 19, "ADX"), (7, 0, 1, 20, "SMAP"),
            (7, 0, 1, 21, "AVX-512IFMA"), (7, 0, 1, 23, "CLFLUSHOPT"), (7, 0, 1, 24, "CLWB"),
            (7, 0, 1, 26, "AVX-512PF"), (7, 0, 1, 27, "AVX-512ER"), (7, 0, 1, 28, "AVX-512CD"),
            (7, 0, 1, 29, "SHA"), (7, 0, 1, 30, "AVX-512BW"), (7, 0, 1, 31, "AVX-512VL"),
            // leaf 7 子葉 0 ECX
            (7, 0, 2, 1, "AVX-512VBMI"), (7, 0, 2, 2, "UMIP"), (7, 0, 2, 3, "PKU"), (7, 0, 2, 4, "OSPKE"),
            (7, 0, 2, 8, "GFNI"), (7, 0, 2, 9, "VAES"), (7, 0, 2, 10, "VPCLMULQDQ"),
            (7, 0, 2, 11, "AVX-512VNNI"), (7, 0, 2, 12, "AVX-512BITALG"), (7, 0, 2, 14, "AVX-512VPOPCNTDQ"),
            (7, 0, 2, 24, "BUS_LOCK_DETECT"), (7, 0, 2, 25, "CLDEMOTE"), (7, 0, 2, 26, "MOVDIRI"),
            (7, 0, 2, 27, "MOVDIR64B"), (7, 0, 2, 29, "ENQCMD"), (7, 0, 2, 30, "SGX_LC"),
            // leaf 7 子葉 0 EDX
            (7, 0, 3, 2, "AVX-512_4VNNIW"), (7, 0, 3, 3, "AVX-512_4FMAPS"), (7, 0, 3, 4, "FSRM"),
            (7, 0, 3, 8, "AVX-512VP2INTERSECT"), (7, 0, 3, 10, "MD_CLEAR"), (7, 0, 3, 14, "SERIALIZE"),
            (7, 0, 3, 15, "HYBRID"), (7, 0, 3, 16, "TSXLDTRK"), (7, 0, 3, 18, "PCONFIG"),
            (7, 0, 3, 20, "CET-IBT"), (7, 0, 3, 26, "IBRS"), (7, 0, 3, 27, "STIBP"), (7, 0, 3, 31, "SSBD"),
            // leaf 7 子葉 1 EAX（AMX／FP16）
            (7, 1, 0, 5, "AMX-BF16"), (7, 1, 0, 23, "AVX-512FP16"), (7, 1, 0, 24, "AMX-TILE"), (7, 1, 0, 25, "AMX-INT8"),
            // 擴充葉 0x80000001（以負值標記）
            (-1, 0, 2, 4, "LZCNT"), (-1, 0, 2, 8, "PREFETCHW"),
            (-1, 0, 3, 11, "SYSCALL"), (-1, 0, 3, 20, "NX"), (-1, 0, 3, 26, "1GB 頁"), (-1, 0, 3, 27, "RDTSCP"), (-1, 0, 3, 29, "LM（64 位元）"),
        };
    }
}
