namespace XinSpect;

/// <summary>
/// ring0 MSR 直讀出來的驗機事實。這是本工具最底層的一條讀取路徑——經簽章驅動直接問矽晶片,
/// 不透過任何韌體或 OS 轉譯,故信賴度是 <see cref="FactTrust.Native"/>。
/// </summary>
/// <remarks>
/// <b>唯讀</b>:只用 <see cref="WinRing0Bridge.ReadMsrPair64"/>,絕不寫 MSR。寫 MSR(改倍頻／電壓／
/// 微碼)能讓機器當場當機或損壞,那種操作只走超頻頁的逐次風險確認,不放進自動驗機。
/// 橋接不可用(未啟用 WinRing0／非管理員)時不產出任何事實,交給引擎判「無法判定」。
/// </remarks>
public static class CpuMsrFacts
{
    private const uint MsrBiosSignId = 0x8B;   // IA32_BIOS_SIGN_ID:微碼版本在讀取後的 EDX(高 32 位元)

    /// <summary>逐實體核讀 MSR 0x8B,取微碼版本(EDX)。全等才正常;不同=載入失敗或竄改。</summary>
    public static IReadOnlyList<VerifyFact> Microcode(DateTime now)
    {
        using var bridge = WinRing0Bridge.Create();
        if (!bridge.Available) return [];

        var revs = new List<string>();
        foreach (var core in CpuAffinity.PhysicalCores(CpuAffinity.IsMultiGroup, ulong.MaxValue))
        {
            using var pin = CpuAffinity.Pinned(core.First);   // 綁到該實體核,RDMSR 才讀得到那顆核的值
            if (bridge.ReadMsrPair64(MsrBiosSignId) is { } sign)
                revs.Add("0x" + ((uint)(sign >> 32)).ToString("X"));
        }
        if (revs.Count == 0) return [];

        return
        [
            new VerifyFact(FactId.CpuMicrocodePerCore, FactCatalog.Name(FactId.CpuMicrocodePerCore),
                string.Join("|", revs), null, "", FactSource.Msr,
                "MSR 0x8B（IA32_BIOS_SIGN_ID）EDX，逐實體核綁定讀取", true, FactTrust.Native, now),
        ];
    }
}
