namespace XinSpect;

/// <summary>
/// Credential Guard 狀態鏈（U10 第三圈 SE-0005，第 319 輪）：設定面 → 政策面 → 執行面 → 硬體面，四層各自陳述。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要一條鏈而不是一個布林：</b>既有事實只有「VBS／HVCI 狀態」與「LSA／CredGuard 卡」兩句，
/// 而後者讀的是 <c>EnableVirtualizationBasedSecurity</c>——那是 <b>VBS 的總開關</b>，
/// 不是 Credential Guard 的設定（第 319 輪把那個標籤一併改正）。
/// 混成一句之後，「Credential Guard 沒開」與「VBS 沒開」永遠分不出來。
/// Credential Guard 真正的設定面是 <c>LsaCfgFlags</c>（0 停用／1 啟用含 UEFI 鎖／2 啟用不含鎖）
/// 與 <c>DeviceGuard\Scenarios\CredentialGuard\Enabled</c>；執行面是
/// <c>Win32_DeviceGuard.SecurityServicesRunning</c> 裡有沒有代碼 1。
/// </para>
/// <para>
/// <b>四層各自陳述，不替機器歸因：</b>「已設定但沒在跑」可能是 VBS 總開關沒開、
/// 硬體韌體缺 hypervisor 支援、或設定之後還沒重開機——<b>三個原因都只列出來，不選一個</b>。
/// 把「已設定但未執行」寫成「已啟用」或「不支援」，是這一項明文禁止的兩種混淆。
/// </para>
/// <para>
/// <b>三態：</b>設定讀取失敗＝<c>ReadError</c>（不是「未啟用」）；設定說已啟用而執行面讀不到＝
/// <c>Unknown</c> 並指名是哪一面不知道；<c>LsaCfgFlags</c> 出現值表未收錄的代碼＝<c>Unknown</c>
/// 並帶原值，<b>不猜它算不算啟用</b>。
/// </para>
/// <para>
/// 純函式：輸入是設定快照與同一輪的 <see cref="VbsStateFactsService.VbsSnapshot"/>，
/// 這支不碰登錄檔也不碰 WMI——所以測試可以餵任意組合。
/// </para>
/// </remarks>
public static class CredentialGuardFactsService
{
    /// <summary>狀態鏈（靜態鍵）。</summary>
    public const string ChainKey = "sec.credguard.chain";

    private const string Category = "安全";
    private const string Source =
        @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa\LsaCfgFlags＋DeviceGuard\Scenarios\CredentialGuard"
        + @"＋DeviceGuard＋Win32_DeviceGuard（唯讀）；值表＝Microsoft Learn 對準版";

    /// <summary>鏈的三級：0 未啟用、1 已設定但未執行、2 執行中。</summary>
    public const int LevelOff = 0;
    public const int LevelConfiguredNotRunning = 1;
    public const int LevelRunning = 2;

    /// <summary>
    /// 設定面快照。<c>null</c> 欄位＝該值不存在（<b>未設定</b>，不是 0）；
    /// 整份 <c>null</c>＝登錄檔讀取失敗（<c>ReadError</c>）。
    /// </summary>
    public sealed record Config(uint? LsaCfgFlags, uint? ScenarioEnabled, uint? VbsEnabled);

    /// <summary>把設定面與同一輪的執行面快照併成一條狀態鏈。</summary>
    /// <param name="config"><c>null</c>＝設定讀取失敗。</param>
    /// <param name="vbs">同一輪 <see cref="VbsStateFactsService"/> 的 WMI 快照；<c>null</c>＝執行面不可用。</param>
    /// <param name="at">量測時刻。</param>
    /// <param name="readFailureReason">設定讀取失敗時的理由（讀取端知道細節，事實層不編）。</param>
    public static IReadOnlyList<HardwareFact> Collect(
        Config? config, VbsStateFactsService.VbsSnapshot? vbs, DateTimeOffset at, string? readFailureReason = null)
    {
        if (config is null)
        {
            string reason = string.IsNullOrWhiteSpace(readFailureReason)
                ? "Credential Guard 設定讀取失敗——不是「未啟用」，也不是 0。"
                : "Credential Guard 設定讀取失敗（不是「未啟用」，也不是 0）：" + readFailureReason;
            return [Unavailable(at, FactAvailability.ReadError, reason)];
        }

        // 值表未收錄就不猜：0／1／2 是 Microsoft Learn 的完整值表，其餘一律不判定。
        if (config.LsaCfgFlags is { } flags && flags is not (0 or 1 or 2))
        {
            return [Unavailable(at, FactAvailability.Unknown,
                $"LsaCfgFlags＝{flags} 不在本版值表（0 停用／1 啟用含 UEFI 鎖／2 啟用不含鎖）——"
                + "不猜它算不算啟用；執行面與硬體面仍見 VBS 狀態那一把鍵。")];
        }

        bool configured = config.LsaCfgFlags is 1 or 2 || config.ScenarioEnabled == 1;
        bool explicitlyOff = !configured && (config.LsaCfgFlags == 0 || config.ScenarioEnabled == 0);
        bool? running = vbs?.Running is { } list ? list.Contains(1u) : null;

        string faces =
            $"設定面：LsaCfgFlags＝{FlagsName(config.LsaCfgFlags)}、"
            + $"Scenarios\\CredentialGuard\\Enabled＝{FlagName(config.ScenarioEnabled)}；"
            + $"政策面：VBS 總開關（EnableVirtualizationBasedSecurity）＝{FlagName(config.VbsEnabled)}；"
            + $"執行面：{RunningFace(vbs, running)}；"
            + $"硬體面：{HardwareFace(vbs)}。";

        const string boundary =
            "「已設定但未執行」可能是 VBS 總開關沒開、硬體韌體缺 hypervisor 支援、或設定後尚未重開機——"
            + "三個原因並列，本服務不替機器歸因；「未設定」「明確關閉」「不支援」是三個不同的斷言。";

        if (configured && running == true)
            return [Present(at, LevelRunning, "已設定且執行中", faces, boundary)];

        if (configured && running == false)
            return [Present(at, LevelConfiguredNotRunning,
                "已設定但未執行（政策要它跑，執行面說沒在跑——這是一個待查的矛盾，不是「已啟用」）",
                faces, boundary)];

        if (configured)
            return [Unavailable(at, FactAvailability.Unknown,
                "設定面說 Credential Guard 已啟用，但執行面讀不到（Win32_DeviceGuard 不可用）——"
                + "不能說它在跑，也不能說它沒在跑。" + faces + boundary)];

        if (explicitlyOff)
        {
            string conclusion = running == true
                ? "政策值為 0（明確關閉），而執行面說 Credential Guard 在跑——兩個獨立來源互相矛盾，並列不仲裁"
                : "明確關閉（政策值為 0）——這是政策事實，不是「不支援」";
            return [Present(at, running == true ? LevelRunning : LevelOff, conclusion, faces, boundary)];
        }

        // 沒有任何政策值：未設定。與「明確關閉」分開陳述。
        string unset = running == true
            ? "設定面看不到任何 Credential Guard 政策，而執行面說它在跑——並列不仲裁（政策可能來自本版沒有讀取的面）"
            : "未設定（LsaCfgFlags 與 Scenarios\\CredentialGuard 都沒有值）——「未設定」不等於「已關閉」，也不等於「不支援」";
        return [Present(at, running == true ? LevelRunning : LevelOff, unset, faces, boundary)];
    }

    private static HardwareFact Present(DateTimeOffset at, int level, string conclusion, string faces, string boundary) =>
        new(ChainKey, Category, "Credential Guard 狀態鏈",
            conclusion + "。" + faces + boundary, "", Source,
            FactTrustLevel.Reported, false, at, level, FactAvailability.Present, null);

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason) =>
        new(ChainKey, Category, "Credential Guard 狀態鏈", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);

    /// <summary>LsaCfgFlags 值表（Microsoft Learn 對準版）；<c>null</c>＝值不存在。</summary>
    internal static string FlagsName(uint? v) => v switch
    {
        null => "未設定（值不存在）",
        0 => "0（停用）",
        1 => "1（啟用，含 UEFI 鎖）",
        2 => "2（啟用，不含 UEFI 鎖）",
        _ => $"{v}（值表未收錄）",
    };

    /// <summary>0／1 的開關值；<c>null</c>＝值不存在。</summary>
    internal static string FlagName(uint? v) => v switch
    {
        null => "未設定（值不存在）",
        0 => "0（關）",
        1 => "1（開）",
        _ => $"{v}（值表未收錄）",
    };

    private static string RunningFace(VbsStateFactsService.VbsSnapshot? vbs, bool? running)
    {
        if (vbs is null) return "讀不到（Win32_DeviceGuard 不可用）";
        if (vbs.Running is null) return "讀不到（SecurityServicesRunning 欄位缺席）";
        if (running == true) return "SecurityServicesRunning 含代碼 1（Credential Guard）";
        string list = vbs.Running.Count == 0
            ? "清單為空"
            : "清單為 " + string.Join("、", vbs.Running.Select(VbsStateFactsService.ServiceName));
        return $"SecurityServicesRunning 不含代碼 1（{list}）";
    }

    private static string HardwareFace(VbsStateFactsService.VbsSnapshot? vbs)
    {
        if (vbs is null) return "讀不到（同執行面）";
        if (vbs.Available is null) return "讀不到（AvailableSecurityProperties 欄位缺席）";
        bool hypervisor = vbs.Available.Contains(1u);
        bool secureBoot = vbs.Available.Contains(2u);
        return $"AvailableSecurityProperties hypervisor 支援（1）{(hypervisor ? "在" : "不在")}、"
               + $"Secure Boot（2）{(secureBoot ? "在" : "不在")}——這是韌體自報的能力，不是本程式的判定";
    }
}
