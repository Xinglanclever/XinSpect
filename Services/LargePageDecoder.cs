namespace XinSpect;

/// <summary>大頁的環境事實與（若量得到的）指標追逐結果。</summary>
public sealed class LargePageFacts
{
    /// <summary>行程的權杖是否真的握有 <c>SeLockMemoryPrivilege</c>（大頁的前提）。</summary>
    public required bool PrivilegeHeld { get; init; }

    /// <summary>這個平台的大頁最小單位（位元組）；讀不到為 0。</summary>
    public required long LargePageMinimum { get; init; }

    /// <summary>試著配置一塊大頁記憶體是否成功。</summary>
    public required bool AllocationOk { get; init; }

    /// <summary>配置失敗時的 Win32 錯誤碼。</summary>
    public int AllocationError { get; init; }

    /// <summary>4 KB 頁的指標追逐平均延遲（ns）；未量測為 null。</summary>
    public double? SmallPageNs { get; init; }

    /// <summary>大頁的指標追逐平均延遲（ns）；未量測為 null。</summary>
    public double? LargePageNs { get; init; }
}

/// <summary>大頁判決。</summary>
public sealed class LargePageVerdict
{
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    public required Severity Severity { get; init; }
}

/// <summary>
/// 大頁與 TLB 走表成本的判讀（純函式）。
/// </summary>
/// <remarks>
/// 「配不出大頁」與「大頁沒有效益」是兩件完全不同的事：前者要去開權限或重開機（碎片化），
/// 後者代表工作集本來就落在 TLB 的覆蓋範圍內，什麼都不用做。混為一談會害人白忙。
/// </remarks>
public static class LargePageDecoder
{
    /// <summary>差異超過這個百分比才說「明顯較快」；以下視為量測雜訊。</summary>
    private const double MeaningfulPercent = 5;

    public static LargePageVerdict Judge(LargePageFacts f)
    {
        string size = SizeText(f.LargePageMinimum);

        if (!f.PrivilegeHeld)
            return new LargePageVerdict
            {
                Headline = "這個行程沒有大頁權限",
                Severity = Severity.Neutral,
                Detail = $"大頁需要 SeLockMemoryPrivilege（本機安全性政策裡叫「鎖定記憶體中的頁面」）。"
                       + "本程式不會自己去改這項政策——那是系統層級的權限授與，應該由你自己決定。"
                       + $"這台機器的大頁單位是 {size}。權限沒開之前，下面的比較做不了，本頁也不會拿別人的數字冒充。",
            };

        if (!f.AllocationOk)
        {
            string why = ErrorText(f.AllocationError);
            var sev = f.AllocationError == 1450 ? Severity.Warning : Severity.Neutral;
            return new LargePageVerdict
            {
                Headline = "有權限，但現在配不出大頁",
                Severity = sev,
                Detail = $"大頁必須是實體上{size}對齊且完全連續的一整塊，而開機一段時間後實體記憶體會碎片化，"
                       + $"於是「有權限也配不到」。這不代表大頁沒有效益，只代表現在拿不到——重開機後最容易成功。"
                       + $"系統回報：{why}",
            };
        }

        if (f.SmallPageNs is not { } small || f.LargePageNs is not { } large)
            return new LargePageVerdict
            {
                Headline = "尚未量測",
                Severity = Severity.Neutral,
                Detail = $"權限與配置都沒問題（大頁單位 {size}）。按下量測後，會用<b>同一個</b>存取樣式與"
                       + "同樣大小的工作集各跑一次——唯一的差別是頁面大小，所以兩者的差就是位址轉換的成本。",
            };

        double gain = small > 0 ? (small - large) / small * 100 : 0;
        if (gain < MeaningfulPercent)
            return new LargePageVerdict
            {
                Headline = "大頁在這個工作集上沒有明顯差異",
                Severity = Severity.Neutral,
                Detail = $"4 KB 頁 {small:0.0} ns、大頁 {large:0.0} ns，差 {gain:0.#}%（在量測雜訊範圍內）。"
                       + "這通常代表工作集還落在 TLB 的覆蓋範圍內——每一次存取都命中 TLB，換大頁自然省不到什麼。"
                       + "工作集再大幾倍時差距才會出來。",
            };

        return new LargePageVerdict
        {
            Headline = $"大頁快 {gain:0.#}%",
            Severity = Severity.Good,
            Detail = $"同一個存取樣式、同樣大小的工作集：4 KB 頁 {small:0.0} ns、大頁 {large:0.0} ns。"
                   + "唯一的變數是頁面大小，所以這 " + $"{gain:0.#}%" + " 就是分頁表走表（page walk）與 TLB 未命中的成本——"
                   + "資料集越大、隨機性越強，這個代價越明顯。",
        };
    }

    /// <summary>位元組 → 人看得懂的單位；0 代表讀不到。</summary>
    public static string SizeText(long bytes) => bytes switch
    {
        0 => "讀不到",
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} 位元組",
    };

    /// <summary>把常見的 Win32 錯誤翻成人話；沒收錄的一律保留代碼，不亂猜原因。</summary>
    public static string ErrorText(int code) => code switch
    {
        1314 => "1314 ERROR_PRIVILEGE_NOT_HELD——權限沒有握在手上。",
        1450 => "1450 ERROR_NO_SYSTEM_RESOURCES——找不到足夠的連續實體記憶體（碎片化）。",
        0 => "沒有錯誤碼。",
        _ => $"Win32 錯誤 {code}（未收錄的原因，不猜）。",
    };
}
