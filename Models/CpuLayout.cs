namespace XinSpect;

/// <summary>一次遷移落在哪一層。層級越靠後，丟掉的快取越多、代價越大。</summary>
public enum MigrationHop
{
    /// <summary>沒有換核心（同一顆邏輯處理器上被切回來）——不算遷移。</summary>
    Same = 0,
    /// <summary>同一顆實體核心的兩條 SMT 執行緒之間。L1／L2 都還在，代價最小。</summary>
    SmtSibling = 1,
    /// <summary>同一片末級快取（L3）內換核。L1／L2 丟了，L3 還在。</summary>
    SameLlc = 2,
    /// <summary>跨末級快取（跨 CCX／跨 die／跨 mesh 分片）。連 L3 都得重新拉。</summary>
    CrossLlc = 3,
    /// <summary>跨 NUMA 節點。記憶體變成遠端存取，代價最大。</summary>
    CrossNuma = 4,
}

/// <summary>
/// 邏輯處理器的層級歸屬：每顆 CPU 屬於哪顆實體核心、哪片末級快取、哪個 NUMA 節點。
/// 純資料 ＋ 純函式，硬體讀取在 <see cref="ThreadMigrationService"/>。
/// </summary>
/// <remarks>
/// 這一層存在的唯一理由是把「執行緒從 3 號核跑到 11 號核」翻譯成「這一跳丟掉了什麼」。
/// 沒有拓樸，遷移次數就只是一個沒有量綱的數字：在 SMT 兄弟之間跳一萬次和跨 NUMA 跳一萬次
/// 是完全不同的兩件事，混成一個「每秒遷移數」等於把最重要的資訊丟掉。
/// </remarks>
public sealed class CpuLayout
{
    /// <summary>索引為邏輯處理器編號；值為 −1 代表該項讀不到。</summary>
    public CpuLayout(int[] coreOf, int[] llcOf, int[] numaOf)
    {
        CoreOf = coreOf;
        LlcOf = llcOf;
        NumaOf = numaOf;
    }

    public int[] CoreOf { get; }
    public int[] LlcOf { get; }
    public int[] NumaOf { get; }

    public int Count => CoreOf.Length;

    /// <summary>拓樸完整到足以分層（至少認得出實體核心）。</summary>
    public bool IsKnown => Count > 0 && CoreOf.Any(c => c >= 0);

    /// <summary>末級快取的分片數；1 代表全機共用一片（或讀不到分片資訊）。</summary>
    public int LlcCount => LlcOf.Where(v => v >= 0).Distinct().Count();

    /// <summary>NUMA 節點數；讀不到時回 1。</summary>
    public int NumaCount => Math.Max(1, NumaOf.Where(v => v >= 0).Distinct().Count());

    /// <summary>一無所知時的空拓樸：分層會全部回 <see cref="MigrationHop.CrossLlc"/> 之外的「未知」處理。</summary>
    public static CpuLayout Empty { get; } = new([], [], []);

    /// <summary>
    /// 這一跳屬於哪一層。由外而內判斷（NUMA → LLC → 實體核心），因為外層的代價蓋過內層。
    /// 任一端超出範圍就當作同一顆處理（回 <see cref="MigrationHop.Same"/>），不猜。
    /// </summary>
    public MigrationHop Classify(int from, int to)
    {
        if (from == to) return MigrationHop.Same;
        if (from < 0 || to < 0 || from >= Count || to >= Count) return MigrationHop.Same;

        if (NumaOf[from] >= 0 && NumaOf[to] >= 0 && NumaOf[from] != NumaOf[to]) return MigrationHop.CrossNuma;
        if (LlcOf[from] >= 0 && LlcOf[to] >= 0 && LlcOf[from] != LlcOf[to]) return MigrationHop.CrossLlc;
        if (CoreOf[from] >= 0 && CoreOf[to] >= 0 && CoreOf[from] == CoreOf[to]) return MigrationHop.SmtSibling;
        return MigrationHop.SameLlc;
    }

    /// <summary>層級的顯示名稱。</summary>
    public static string HopName(MigrationHop hop) => hop switch
    {
        MigrationHop.SmtSibling => "同實體核心（SMT 兄弟）",
        MigrationHop.SameLlc => "同末級快取內換核",
        MigrationHop.CrossLlc => "跨末級快取",
        MigrationHop.CrossNuma => "跨 NUMA 節點",
        _ => "未換核",
    };

    /// <summary>這一層丟掉了什麼——這才是使用者要知道的事。</summary>
    public static string HopCost(MigrationHop hop) => hop switch
    {
        MigrationHop.SmtSibling => "L1／L2 都還在（同一顆核心的兩條執行緒共用），代價最小。",
        MigrationHop.SameLlc => "L1／L2 的內容留在原本那顆核上，得重新拉；L3 還命中。",
        MigrationHop.CrossLlc => "連 L3 都要重新拉——跨 CCX、跨 die 或跨 mesh 分片時就是這一層。",
        MigrationHop.CrossNuma => "記憶體變成遠端存取，延遲與頻寬雙重打折，代價最大。",
        _ => "",
    };
}
