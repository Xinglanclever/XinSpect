namespace XinSpect;

/// <summary>
/// VBS 安全服務的執行清單（U10 第三圈 SE-0004，第 320 輪）：哪幾個服務被要求、哪幾個真的在跑，逐服務對照。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼既有事實不夠：</b><c>sec.vbs.state</c> 把
/// <c>SecurityServicesConfigured</c> 與 <c>SecurityServicesRunning</c> 兩張清單**寫成一句話**
/// （「已設定：2（記憶體完整性）；執行中的安全服務：2（記憶體完整性）」）。
/// 那句話對人可讀，但**不能查**：想知道「Credential Guard 有沒有在跑」得自己去那串字裡找，
/// 而字串比對正是這個專案最不信任的東西。這一支把兩張清單拆成逐服務的鍵。
/// </para>
/// <para>
/// <b>代碼 0 是哨兵，不是服務：</b>Win32_DeviceGuard 用 0 表示「無服務」。
/// 它會出現在清單裡（例如「執行中的安全服務：0（無服務）」），但它**不是一個服務**——
/// 逐服務出鍵會得到一把 <c>sec.vbs.svc.0</c>，那是在替「沒有」造一個東西。
/// 所以 0 只寫在總覽的一句話裡，不進逐服務家族。
/// </para>
/// <para>
/// <b>兩張清單缺一張就只出還讀得到的那一半，並在每一筆上寫明另一半讀不到：</b>
/// 不拿「設定」的缺席去代言「執行」，也不因為少一張清單就整組不出——
/// 但總覽的數字只放讀得到的那一個（<c>Running</c> 讀不到就不給執行數，轉 Unknown）。
/// </para>
/// <para>
/// 純函式：輸入是同一輪 <see cref="VbsStateFactsService"/> 的快照，這支不碰 WMI。
/// </para>
/// </remarks>
public static class VbsServiceFactsService
{
    /// <summary>服務清單總覽（靜態鍵）。</summary>
    public const string SummaryKey = "sec.vbs.services.summary";

    /// <summary>逐服務家族：<c>sec.vbs.svc.{代碼}</c>（成員＝這一台機器真的被設定或執行中的服務）。</summary>
    /// <remarks>
    /// 常數名不含 <c>Key</c>：事實鍵掃描器把「名字含 <c>Key</c> 的字串常數」當成事實鍵，
    /// 家族前綴若叫 <c>...Key</c> 會被當成一把不存在的鍵，讓未覆蓋數虛增（第 312 輪的教訓）。
    /// </remarks>
    public const string ServicePrefix = "sec.vbs.svc.";

    private const string Category = "安全";
    private const string Source =
        "WMI Win32_DeviceGuard 的 SecurityServicesConfigured／SecurityServicesRunning（唯讀）；值表＝Microsoft Learn 對準版";

    /// <summary>
    /// 把同一輪的快照攤成「總覽＋逐服務」。
    /// </summary>
    /// <param name="snapshot">同一輪 <see cref="VbsStateFactsService"/> 的快照；<c>null</c>＝執行面不可用。</param>
    /// <param name="upstreamAvailability">快照為 <c>null</c> 時沿用的上游三態——不自己重新分類。</param>
    /// <param name="upstreamReason">上游的不可用理由（讀取端知道細節，事實層不編）。</param>
    /// <param name="at">量測時刻。</param>
    public static IReadOnlyList<HardwareFact> Collect(
        VbsStateFactsService.VbsSnapshot? snapshot,
        FactAvailability upstreamAvailability,
        string? upstreamReason,
        DateTimeOffset at)
    {
        if (snapshot is null)
        {
            // 執行面整份不可用：沿用它自己的三態與理由。這裡**不重新判斷**
            // 「是沒有這個命名空間還是查詢失敗」——那是上游已經回答過的問題，
            // 兩支各自猜一次就會有兩個答案。
            string reason = "Win32_DeviceGuard 不可用，沒有服務清單可列"
                            + (string.IsNullOrWhiteSpace(upstreamReason) ? "。" : "：" + upstreamReason);
            return
            [
                new HardwareFact(SummaryKey, Category, "VBS 安全服務清單", "", "個", Source,
                    FactTrustLevel.Unknown, false, at, null, upstreamAvailability, reason),
            ];
        }

        var configured = snapshot.Configured;
        var running = snapshot.Running;

        if (configured is null && running is null)
        {
            return
            [
                new HardwareFact(SummaryKey, Category, "VBS 安全服務清單", "", "個", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                    "SecurityServicesConfigured 與 SecurityServicesRunning 兩個欄位都缺席——"
                    + "讀不到清單，不是「沒有任何服務」。"),
            ];
        }

        // 代碼 0 是「無服務」的哨兵值，不是服務：只出現在總覽的說明裡。
        var configuredReal = configured?.Where(static c => c != 0).OrderBy(static c => c).ToList();
        var runningReal = running?.Where(static c => c != 0).OrderBy(static c => c).ToList();
        bool sentinelSeen = configured?.Contains(0u) == true || running?.Contains(0u) == true;

        var members = (configuredReal ?? []).Union(runningReal ?? []).Distinct().OrderBy(static c => c).ToList();
        var facts = new List<HardwareFact>(members.Count + 1);

        foreach (uint code in members)
        {
            bool? isConfigured = configuredReal?.Contains(code);
            bool? isRunning = runningReal?.Contains(code);
            facts.Add(ServiceFact(code, isConfigured, isRunning, at));
        }

        facts.Add(SummaryFact(configuredReal, runningReal, sentinelSeen, at));
        return facts.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    private static HardwareFact ServiceFact(uint code, bool? configured, bool? running, DateTimeOffset at)
    {
        string Face(bool? v) => v switch { true => "是", false => "否", null => "讀不到（欄位缺席）" };

        string note = (configured, running) switch
        {
            (true, false) => "。**已設定但沒在跑**：這是待查的狀態，不是「已啟用」"
                             + "（可能是不支援、或設定後尚未重開機）",
            (false, true) => "。**在跑但設定清單裡沒有**：兩個欄位互相矛盾，並列不仲裁",
            _ => "",
        };

        return new HardwareFact($"{ServicePrefix}{code}", Category, $"VBS 服務 {code}",
            $"{VbsStateFactsService.ServiceName(code)}；設定：{Face(configured)}；執行：{Face(running)}"
            + "。設定是政策，執行才是生效" + note,
            "", Source, FactTrustLevel.Reported, false, at);
    }

    private static HardwareFact SummaryFact(
        List<uint>? configured, List<uint>? running, bool sentinelSeen, DateTimeOffset at)
    {
        static string Names(IReadOnlyList<uint>? list) => list is null
            ? "讀不到"
            : list.Count == 0 ? "（空清單）" : string.Join("、", list.Select(VbsStateFactsService.ServiceName));

        string body;
        if (running is null)
        {
            body = $"執行讀不到（SecurityServicesRunning 欄位缺席）——不知道在跑哪幾個，因此**不給執行數**；"
                   + $"設定 {Names(configured)}";
        }
        else if (running.Count == 0 && configured is { Count: 0 })
        {
            // 兩張清單都讀到而且都是空的：這是「沒有任何安全服務被設定或執行」，
            // 與「讀不到」是兩個斷言——所以這句話要說出來，不能只寫「0 個」。
            body = "執行 0 個（空清單）；設定 0 個（空清單）——"
                   + "兩張清單都讀得到且都是空的，這是「沒有任何安全服務被設定或執行」，不是讀不到";
        }
        else
        {
            body = $"執行 {running.Count} 個：{Names(running)}；"
                   + (configured is null
                       ? "設定讀不到（SecurityServicesConfigured 欄位缺席）"
                       : $"設定 {configured.Count} 個：{Names(configured)}");
        }

        string notes =
            (sentinelSeen ? " 清單含代碼 0（無服務）——那是哨兵值不是服務，不逐服務出鍵。" : "")
            + " 設定是政策，執行才是生效；兩者不一致時逐服務鍵會寫明是哪一邊。"
            + " 逐服務的成員是兩張清單的聯集（只設定沒跑、只跑沒設定都會列出來）。";

        // 執行數是這把鍵的數值；執行清單讀不到時不給數字（非 Present 不得帶數字的同一條道理）。
        if (running is null)
            return new HardwareFact(SummaryKey, Category, "VBS 安全服務清單", body + notes, "個", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                "SecurityServicesRunning 欄位缺席——讀不到執行清單，不是「沒有服務在跑」。" + body + notes);

        return new HardwareFact(SummaryKey, Category, "VBS 安全服務清單", body + notes, "個", Source,
            FactTrustLevel.Derived, false, at, running.Count, FactAvailability.Present, null);
    }
}
