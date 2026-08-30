using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
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

    /// <summary>熱能與電源管理能力（leaf 0x06）：這顆晶片有沒有 HWP、Turbo、PLN、MPERF/APERF 回饋。</summary>
    public ObservableCollection<CpuIdInfoRow> Power { get; } = [];

    /// <summary>架構效能監測單元（leaf 0x0A）：版本、通用／固定計數器數量與位寬。</summary>
    public ObservableCollection<CpuIdInfoRow> Pmu { get; } = [];

    /// <summary>XSAVE 狀態元件（leaf 0x0D）：每個元件的大小與在儲存區中的位移。</summary>
    public ObservableCollection<CpuIdInfoRow> XSave { get; } = [];

    public bool HasPower => Power.Count > 0;
    public bool HasPmu => Pmu.Count > 0;
    public bool HasXSave => XSave.Count > 0;

    /// <summary>CPUID 拓樸與作業系統列舉的交叉驗證結論。</summary>
    public string TopologyCheckText { get; } = "—";

    /// <summary>恆定 TSC（leaf 0x80000007 EDX 位 8）：TSC 換算時間是否成立的前提。</summary>
    public string InvariantTscText { get; } = "—";

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
        int threadsPerCore = 0, logicalPerPackage = 0;
        if (topoLeaf != 0)
        {
            for (uint sub = 0; sub < 64; sub++)
            {
                var r = X86Base.CpuId((int)topoLeaf, (int)sub);
                var row = Decoder.DecodeTopologySubleaf((uint)r.Eax, (uint)r.Ebx, (uint)r.Ecx, sub);
                if (row is null) break;   // EBX 計數為 0＝最後一層之後
                Topology.Add(row);

                // 交叉驗證用的原始數字：EBX 是「該層級以下的邏輯處理器累計數」，
                // 故 SMT 層＝每核心執行緒數，最大者＝每封裝邏輯處理器數。
                int count = (int)((uint)r.Ebx & 0xFFFF);
                if (((uint)r.Ecx >> 8 & 0xFF) == 1) threadsPerCore = count;
                if (count > logicalPerPackage) logicalPerPackage = count;
            }
        }

        // 混合架構標記（0x1A）：12 代以後才有
        if (maxStd >= 0x1A)
        {
            var r = X86Base.CpuId(0x1A, 0);
            HybridText = Decoder.DecodeHybrid((uint)r.Eax);
        }
        Info.Add(new CpuIdInfoRow("混合架構（0x1A）", HybridText));

        // 恆定 TSC（0x80000007 EDX 位 8）：頻率真相／延遲量測全靠它，讀不到就明說葉位不存在
        InvariantTscText = maxExt >= 0x80000007
            ? Decoder.DecodeInvariantTsc((uint)X86Base.CpuId(unchecked((int)0x80000007), 0).Edx)
            : "—（0x80000007 葉位不存在，未讀取）";
        Info.Add(new CpuIdInfoRow("恆定 TSC（0x80000007）", InvariantTscText));

        // 熱能與電源管理能力（0x06）
        if (maxStd >= 0x06)
        {
            var r = X86Base.CpuId(0x06, 0);
            foreach (var row in Decoder.DecodePower((uint)r.Eax, (uint)r.Ebx, (uint)r.Ecx)) Power.Add(row);
        }

        // 架構效能監測單元（0x0A）
        if (maxStd >= 0x0A)
        {
            var r = X86Base.CpuId(0x0A, 0);
            foreach (var row in Decoder.DecodePmu((uint)r.Eax, (uint)r.Ebx, (uint)r.Edx)) Pmu.Add(row);
        }

        // XSAVE 狀態元件（0x0D）：只讀 XCR0／IA32_XSS 位圖中為 1 的元件子葉。
        // 不能「讀到 0 就停」——中間有未支援的元件時，後面仍可能有支援的（AVX-512 三件就在 5–7）。
        if (maxStd >= 0x0D)
        {
            var m = X86Base.CpuId(0x0D, 0);
            var s1 = X86Base.CpuId(0x0D, 1);
            XSave.Add(Decoder.DecodeXSaveMain((uint)m.Eax, (uint)m.Edx, (uint)m.Ebx, (uint)m.Ecx));
            XSave.Add(Decoder.DecodeXSaveExtras((uint)s1.Eax, (uint)s1.Ebx, (uint)s1.Ecx));
            ulong bitmap = ((uint)m.Eax | ((ulong)(uint)m.Edx << 32)) | (uint)s1.Ecx;
            for (int i = 2; i < 63; i++)
            {
                if ((bitmap & (1UL << i)) == 0) continue;
                var r = X86Base.CpuId(0x0D, i);
                var row = Decoder.DecodeXSaveComponent(i, (uint)r.Eax, (uint)r.Ebx, (uint)r.Ecx);
                if (row is not null) XSave.Add(row);
            }
        }

        // 拓樸交叉驗證：CPUID 說的每封裝執行緒／核心數，對得上作業系統列舉的實際數量嗎
        var os = EnumerateOsTopology();
        TopologyCheckText = Decoder.CrossCheckTopology(
            threadsPerCore, logicalPerPackage, os.PhysicalCores, os.Logical);
        Info.Add(new CpuIdInfoRow("拓樸交叉驗證（CPUID vs 作業系統）", TopologyCheckText));

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint returnedLength);

    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// 作業系統看到的實體核心數與邏輯處理器數（GetLogicalProcessorInformationEx，含所有處理器群組）。
    /// 讀不到就回 (0, 0)——交叉驗證會據此說「資料不足」而不是拿 0 去比。
    /// </summary>
    private static (int PhysicalCores, int Logical) EnumerateOsTopology()
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, 0, ref len);
        if (len == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return (0, 0);

        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return (0, 0);
            int off = 0, cores = 0, logical = 0;
            while (off + 8 <= (int)len)
            {
                nint rec = buf + off;
                int size = Marshal.ReadInt32(rec + 4);
                if (size <= 0) break;
                nint pl = rec + 8;                                  // PROCESSOR_RELATIONSHIP
                int groupCount = (ushort)Marshal.ReadInt16(pl + 22);
                cores++;
                for (int g = 0; g < groupCount; g++)
                {
                    ulong mask = (ulong)Marshal.ReadInt64(pl + 24 + g * 16);   // GROUP_AFFINITY.Mask
                    logical += BitOperations.PopCount(mask);
                }
                off += size;
            }
            return (cores, logical);
        }
        finally { Marshal.FreeHGlobal(buf); }
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

        /// <summary>
        /// leaf 0x04 的原始快取幾何。
        /// <para>
        /// <b><see cref="SharedLogical"/> 數的是「可定址的邏輯處理器」，不是核心</b>（EAX 25:14＋1），
        /// 而且硬體會把它向上取到 2 的冪次：18 核 36 執行緒的 L3 會回 64。
        /// 所以這個欄位只能當上界解讀，不能當實數，更不能寫成「64 核共用」。
        /// </para>
        /// </summary>
        public readonly record struct RawCache(
            uint Type, uint Level, uint SharedLogical,
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
                // 含括性只由 EDX 位 1 決定（1＝含括下層、0＝不含括）。位 0 講的是 WBINVD／INVD
                // 對下層快取的行為，與含括性無關——1.6.2 之前把位 0 當成「非含括」的判據，於是
                // 位 0 為 0 的機器（本機 EDX＝0）每一列都顯示「—」，看起來像讀不到。
                // L1 之下沒有更低階快取，這一欄對它本來就沒有意義，故留「—」而不是硬填一個值。
                string inclusive = Level <= 1 ? "—" : (EdxFlags & 0x2) != 0 ? "含括" : "非含括";
                // 共用欄寫「最多 N 執行緒」：欄位數的是邏輯處理器且向上取 2 的冪次（見型別註解）。
                return new CpuIdCacheRow(LevelName, cap, $"{Ways} 路", $"{LineBytes} B", Sets.ToString("N0"),
                                         $"最多 {SharedLogical} 執行緒", inclusive);
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

        /// <summary>
        /// leaf 0x0B／0x1F 子葉 → 拓樸列；EBX 計數為 0 表示列舉終止（回 null）。
        /// <para>
        /// <b>EBX 15:0 數的是「該層在上一層範圍內的邏輯處理器數」，不是那一層有幾個實例。</b>
        /// 核心層在 18 核 36 執行緒的機器上回 36（每封裝的邏輯處理器數），不是 36 個核心——
        /// 1.6.2 之前寫成「核心 36 個」，那是在說謊。
        /// </para>
        /// </summary>
        public static CpuIdTopologyRow? DecodeTopologySubleaf(uint eax, uint ebx, uint ecx, uint subleaf)
        {
            uint count = ebx & 0xFFFF;
            if (count == 0) return null;
            uint levelType = (ecx >> 8) & 0xFF;
            uint shift = eax & 0x1F;
            string level = levelType switch
            {
                1 => "執行緒（SMT）層",
                2 => "核心層",
                3 => "模組層",
                4 => "Tile 層",
                5 => "Die 層",
                6 => "Die 群組層",
                _ => $"層級 {levelType}",
            };
            return new CpuIdTopologyRow($"0x1F #{subleaf}", level, $"{count} 個邏輯處理器", $"APIC ID 右移 {shift} bits");
        }

        /// <summary>leaf 0x80000007 EDX 位 8：TSC 是否恆速（不隨 P-state／C-state 變頻）。</summary>
        public static string DecodeInvariantTsc(uint edx)
            => (edx & (1u << 8)) != 0
                ? "支援（EDX 位 8＝1）：TSC 恆速，以 TSC 換算時間成立。"
                : "不支援（EDX 位 8＝0）：TSC 會隨頻率變動，任何以 TSC 換算時間的量測都不可信。";

        /// <summary>leaf 0x06 EAX 的熱能／電源管理能力位元。</summary>
        public static readonly (int Bit, string Name)[] PowerEaxFlags =
        {
            (0, "數位溫度感測器（DTS）"),
            (1, "Turbo Boost"),
            (2, "APIC 計時器恆速（ARAT）"),
            (4, "功耗限制通知（PLN）"),
            (5, "延伸時脈調變（ECMD）"),
            (6, "封裝熱管理（PTM）"),
            (7, "硬體 P-state（HWP）"),
            (8, "HWP 通知"),
            (9, "HWP 活動視窗"),
            (10, "HWP 能耗偏好"),
            (11, "HWP 封裝層級請求"),
            (13, "硬體工作週期控制（HDC）"),
            (14, "Turbo Boost Max 3.0"),
            (15, "HWP 最高效能變更通知"),
            (16, "HWP PECI 覆寫"),
            (17, "彈性 HWP"),
            (18, "IA32_HWP_REQUEST 快速存取"),
            (19, "硬體回饋介面（HFI）"),
            (20, "忽略閒置邏輯處理器的 HWP 請求"),
            (23, "Thread Director"),
        };

        public static List<CpuIdInfoRow> DecodePower(uint eax, uint ebx, uint ecx)
        {
            var rows = new List<CpuIdInfoRow>();
            var on = PowerEaxFlags.Where(f => (eax & (1u << f.Bit)) != 0).Select(f => f.Name).ToArray();
            rows.Add(new CpuIdInfoRow("已回報的能力（EAX）", on.Length == 0 ? "—（EAX 為 0）" : string.Join("、", on)));
            // HWP 缺席要明說：沒有 HWP，就沒有作業系統可讀的「硬體偏好核心」名單
            rows.Add(new CpuIdInfoRow("硬體 P-state（HWP）",
                (eax & (1u << 7)) != 0
                    ? "支援：頻率由硬體自行決定，作業系統只提供偏好值。"
                    : "不支援：頻率由作業系統的 P-state 請求決定；本平台沒有硬體偏好核心名單可讀。"));
            rows.Add(new CpuIdInfoRow("溫度中斷閾值數（EBX 位 3:0）", $"{ebx & 0xF} 個"));
            rows.Add(new CpuIdInfoRow("硬體協調回饋（ECX 位 0）",
                (ecx & 1) != 0 ? "支援：MPERF／APERF 可用，有效時脈量測成立。" : "不支援：無 MPERF／APERF，無法量測有效時脈。"));
            rows.Add(new CpuIdInfoRow("能耗偏好（ECX 位 3）",
                (ecx & 8) != 0 ? "支援 IA32_ENERGY_PERF_BIAS（EPB）" : "不支援 EPB"));
            rows.Add(new CpuIdInfoRow("原始值", $"EAX＝0x{eax:X8} ・ EBX＝0x{ebx:X8} ・ ECX＝0x{ecx:X8}"));
            return rows;
        }

        /// <summary>架構效能事件名稱（leaf 0x0A EBX 位向量的順序）。</summary>
        public static readonly string[] ArchEventNames =
            { "核心週期", "指令退休", "參考週期", "LLC 參考", "LLC 失誤", "分支指令退休", "分支預測失敗退休", "Topdown 插槽" };

        /// <summary>
        /// EBX 的位為 1 代表該架構事件<b>不可用</b>（SDM 的定義是反的；寫成「支援」就是把 0 說成 1）。
        /// </summary>
        public static string DescribeArchEvents(uint ebx, uint vectorLength)
        {
            int n = (int)Math.Min(vectorLength, (uint)ArchEventNames.Length);
            if (n == 0) return "—（EBX 位向量長度為 0）";
            var missing = Enumerable.Range(0, n).Where(i => (ebx & (1u << i)) != 0).Select(i => ArchEventNames[i]).ToArray();
            return missing.Length == 0
                ? $"前 {n} 個架構事件全部可用（EBX 的位為 1 才代表不可用）"
                : "不可用：" + string.Join("、", missing);
        }

        /// <summary>leaf 0x0A：架構效能監測單元的版本與計數器幾何。</summary>
        public static List<CpuIdInfoRow> DecodePmu(uint eax, uint ebx, uint edx)
        {
            var rows = new List<CpuIdInfoRow>();
            uint ver = eax & 0xFF;
            if (ver == 0)
            {
                rows.Add(new CpuIdInfoRow("架構 PMU", "版本 0：此處理器不提供架構效能監測（Top-down 等 PMU 卡片無法運作）。"));
                return rows;
            }
            uint gp = (eax >> 8) & 0xFF, gpWidth = (eax >> 16) & 0xFF, vecLen = (eax >> 24) & 0xFF;
            uint fixedCnt = edx & 0x1F, fixedWidth = (edx >> 5) & 0xFF;
            rows.Add(new CpuIdInfoRow("架構 PMU 版本", $"版本 {ver}"));
            rows.Add(new CpuIdInfoRow("通用計數器", $"每邏輯處理器 {gp} 個 × {gpWidth} 位元"));
            rows.Add(new CpuIdInfoRow("固定功能計數器", $"{fixedCnt} 個 × {fixedWidth} 位元"));
            rows.Add(new CpuIdInfoRow("架構事件可用性（EBX）", DescribeArchEvents(ebx, vecLen)));
            rows.Add(new CpuIdInfoRow("原始值", $"EAX＝0x{eax:X8} ・ EBX＝0x{ebx:X8} ・ EDX＝0x{edx:X8}"));
            return rows;
        }

        /// <summary>XSAVE 狀態元件（XCR0／IA32_XSS 的位索引）名稱。</summary>
        public static string XSaveComponentName(int index) => index switch
        {
            0 => "x87 FPU",
            1 => "SSE（XMM）",
            2 => "AVX（YMM 高 128 位）",
            3 => "MPX 邊界暫存器",
            4 => "MPX 邊界設定",
            5 => "AVX-512 遮罩暫存器（k0–k7）",
            6 => "AVX-512 ZMM 高 256 位（zmm0–15）",
            7 => "AVX-512 zmm16–31",
            8 => "Processor Trace（監督者狀態）",
            9 => "PKRU（保護鍵）",
            10 => "PASID",
            11 => "CET 使用者狀態",
            12 => "CET 監督者狀態",
            13 => "HDC",
            14 => "UINTR",
            15 => "LBR",
            16 => "HWP",
            17 => "AMX TILECFG",
            18 => "AMX TILEDATA",
            _ => $"元件 {index}",
        };

        /// <summary>leaf 0x0D 子葉 0：XCR0 位圖與儲存區大小。</summary>
        public static CpuIdInfoRow DecodeXSaveMain(uint eaxLow, uint edxHigh, uint ebx, uint ecx)
        {
            ulong mask = eaxLow | ((ulong)edxHigh << 32);
            if (mask == 0) return new CpuIdInfoRow("XCR0 支援的狀態元件", "—（位圖為 0）");
            var names = Enumerable.Range(0, 63).Where(i => (mask & (1UL << i)) != 0).Select(XSaveComponentName);
            return new CpuIdInfoRow("XCR0 支援的狀態元件",
                $"0x{mask:X}：{string.Join("、", names)} ・ 目前啟用需 {ebx:N0} 位元組、全部支援共 {ecx:N0} 位元組");
        }

        /// <summary>leaf 0x0D 子葉 1：XSAVE 指令變體與監督者狀態位圖。</summary>
        public static CpuIdInfoRow DecodeXSaveExtras(uint eax, uint ebx, uint ecx)
        {
            var flags = new List<string>();
            if ((eax & 0x01) != 0) flags.Add("XSAVEOPT");
            if ((eax & 0x02) != 0) flags.Add("XSAVEC");
            if ((eax & 0x04) != 0) flags.Add("XGETBV（ECX＝1）");
            if ((eax & 0x08) != 0) flags.Add("XSAVES／XRSTORS");
            if ((eax & 0x10) != 0) flags.Add("XFD（延遲功能停用）");
            string sup = ecx == 0
                ? "無"
                : string.Join("、", Enumerable.Range(0, 32).Where(i => (ecx & (1u << i)) != 0).Select(XSaveComponentName));
            return new CpuIdInfoRow("XSAVE 指令變體（0x0D/1）",
                $"{(flags.Count == 0 ? "—" : string.Join("、", flags))} ・ 含監督者狀態共需 {ebx:N0} 位元組 ・ 監督者狀態：{sup}");
        }

        /// <summary>
        /// leaf 0x0D 子葉 n≥2：EAX 元件大小、EBX 在儲存區中的位移；
        /// ECX 位 0 為 1 表示監督者狀態（位移無意義），位 1 表示需 64 位元組對齊。大小為 0 即未支援（回 null）。
        /// </summary>
        public static CpuIdInfoRow? DecodeXSaveComponent(int index, uint eax, uint ebx, uint ecx)
        {
            if (eax == 0) return null;
            bool supervisor = (ecx & 1) != 0, aligned = (ecx & 2) != 0;
            string where = supervisor ? "監督者狀態（不在使用者 XSAVE 儲存區中）" : $"位移 {ebx:N0}";
            return new CpuIdInfoRow($"{XSaveComponentName(index)}（0x0D/{index}）",
                $"{eax:N0} 位元組 ・ {where}{(aligned ? " ・ 需 64 位元組對齊" : "")}");
        }

        /// <summary>
        /// 拓樸交叉驗證：CPUID 的每封裝數字乘出來的實體核心數，對不對得上作業系統列舉的數量。
        /// 任一邊缺資料就說「不做推論」——拿 0 去比會得出「一致」這種假結論。
        /// </summary>
        public static string CrossCheckTopology(int threadsPerCore, int logicalPerPackage, int osPhysicalCores, int osLogical)
        {
            if (threadsPerCore <= 0 || logicalPerPackage <= 0 || osPhysicalCores <= 0 || osLogical <= 0)
                return "—（CPUID 拓樸葉位或作業系統列舉缺一，不做推論）";
            int coresPerPackage = logicalPerPackage / threadsPerCore;
            if (coresPerPackage <= 0)
                return "—（CPUID 的每封裝執行緒數小於每核心執行緒數，兩個數字互相矛盾，不做推論）";

            string basis = $"CPUID：每核心 {threadsPerCore} 執行緒、每封裝 {logicalPerPackage} 邏輯處理器（＝{coresPerPackage} 核）；"
                         + $"作業系統：{osPhysicalCores} 實體核心、{osLogical} 邏輯處理器。";
            if (osLogical % logicalPerPackage != 0)
                return "⚠ 不一致：" + basis + "邏輯處理器數不是每封裝數的整數倍——可能有核心被韌體停用，或行程親和性受限。";

            int packages = osLogical / logicalPerPackage;
            int expected = coresPerPackage * packages;
            return expected == osPhysicalCores
                ? $"一致（推得 {packages} 個封裝）：{basis}"
                : $"⚠ 不一致：{basis}依 CPUID 應有 {expected} 顆實體核心。";
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
