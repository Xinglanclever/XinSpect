namespace XinSpect;

/// <summary>
/// M.2／U.2 的判定規則。<b>純函式</b>：輸入是 SMBIOS Type 9 的插槽列與磁碟清單，輸出是可顯示的判定。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要分成「BIOS 標的」與「推測」兩層：</b>M.2／U.2 是<b>插槽外型</b>，而 Windows 的磁碟 API
/// 只回報介面（NVMe／SATA），不回報外型——M.2 與 U.2 都是 NVMe。唯一的第一手來源是 SMBIOS Type 9，
/// 但很多主機板把 M.2 插槽寫成一般的「PCI Express x4」，型別碼不是 M.2。
/// </para>
/// <para>
/// 所以要分開講：<b>Typed</b>＝BIOS 明講（量到的事實）；<b>Inferred</b>＝BIOS 只寫了 PCI Express x4、
/// 而型別不是 M.2——這是<b>推論</b>，必須標示出來，不能和前者混成一句「偵測到 M.2」。
/// 兩者都拿不到時，就明說「未回報」，而不是留白讓人以為程式壞了。
/// </para>
/// </remarks>
public static class M2Detection
{
    /// <summary>SMBIOS Type 9 裡代表 M.2／U.2（SFF-8639）的型別名稱片段。</summary>
    private static readonly string[] M2TypeHints = ["m.2", "sff-8639", "u.2"];

    /// <summary>BIOS 明講是 M.2／U.2 的插槽。</summary>
    public static bool IsTypedM2OrU2(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        string lower = type.ToLowerInvariant();
        foreach (string hint in M2TypeHints)
            if (lower.Contains(hint, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 「可能是 M.2」的插槽：BIOS 標成 PCI Express、寬度是 x4（M.2 NVMe 的典型通道數）。
    /// </summary>
    /// <remarks>
    /// 只收 x4 的理由：x16／x8 幾乎都是顯示卡插槽，把它們列成「可能是 M.2」只是噪音。
    /// 這一層<b>永遠是推論</b>，顯示時必須與 <see cref="IsTypedM2OrU2"/> 的結果分開列。
    /// </remarks>
    public static bool IsInferredM2(SmbiosSlotRow slot, bool isTyped)
    {
        if (isTyped) return false;
        if (!slot.Type.Contains("PCI Express", StringComparison.OrdinalIgnoreCase)) return false;
        return slot.Width.Trim().Equals("x4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>磁碟外型的說明句。<b>只在有第一手證據時說出外型</b>，其餘如實說未回報。</summary>
    /// <remarks>
    /// 型號字串裡的「SFF」是 U.2（SFF-8639）的常見標記，這是<b>廠商自述</b>，不是 Windows 的判定——
    /// 句子裡要寫出來源，否則讀者會以為是系統量到的。
    /// </remarks>
    public static string DescribeDriveForm(DiskKind kind, string? model)
    {
        if (kind != DiskKind.NvmeSsd)
            return kind == DiskKind.SataSsd ? "外型未回報（SATA 介面；2.5 吋與 M.2 SATA 都是 SATA）" : "外型未回報";

        if (!string.IsNullOrWhiteSpace(model) &&
            (model.Contains("SFF", StringComparison.OrdinalIgnoreCase)
             || model.Contains("U.2", StringComparison.OrdinalIgnoreCase)))
            return "U.2（依型號字串的 SFF／U.2 標記判定，不是 Windows 回報）";

        return "外型未回報（NVMe；Windows 不區分 M.2 與 U.2，需對照主機板手冊或插槽編號）";
    }

    /// <summary>
    /// 給人看的一段總結：先講 BIOS 標了什麼，再講推測了什麼，最後講兩者都缺時「未回報」的意思。
    /// </summary>
    public static string Summarize(
        IReadOnlyList<SmbiosSlotRow> typed, IReadOnlyList<SmbiosSlotRow> inferred, int nvmeDrives)
    {
        if (typed.Count > 0)
            return $"BIOS 標示 {typed.Count} 個 M.2／U.2 插槽"
                   + (inferred.Count > 0 ? $"；另有 {inferred.Count} 個 PCI Express x4 插槽（BIOS 未標型別，可能是 M.2）" : "")
                   + $"；本機有 {nvmeDrives} 顆 NVMe 磁碟";

        if (inferred.Count > 0)
            return "BIOS 沒有把任何插槽標成 M.2／U.2，但有 "
                   + $"{inferred.Count} 個 PCI Express x4 插槽（{string.Join("、", inferred.Select(s => s.Designation))}）"
                   + "——以通道數看極可能是 M.2，這是<b>推測</b>，不是 BIOS 的回報"
                   + $"；本機有 {nvmeDrives} 顆 NVMe 磁碟";

        return "SMBIOS 未回報 M.2／U.2 插槽（不代表沒有——部分主機板不在 Type 9 列出 M.2，或整份 Type 9 為空）"
               + $"；本機有 {nvmeDrives} 顆 NVMe 磁碟，外型無法由 Windows 判定";
    }
}
