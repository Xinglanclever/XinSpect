using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 韌體設定可讀項盤點（ULTIMATE-1000 階段二 P2-046）。
///
/// <para>
/// <b>為什麼需要「盤點可讀性」這一項：</b>
/// 本專案有幾十項韌體事實，每一項都可能因為權限、平台或設定而讀不到。
/// 而<b>讀不到與「沒有這個值」在使用者眼裡長得一樣</b>——都是空的。
/// 這一項把「這個來源現在讀得到嗎」本身變成一件事實，
/// 讓「為什麼那一頁是空的」有一個可以查的答案。
/// </para>
///
/// <para>
/// <b>界線：這裡檢查的是「來源可及性」，不是「值是否正確」。</b>
/// 讀得到一個值不代表那個值對——那是每一項各自要負責的事。
/// 本服務回答的是更前面的一個問題：<b>這條路現在通不通</b>。
/// </para>
///
/// <para>
/// <b>每一個探測都是唯讀的</b>，而且<b>不改變任何狀態</b>——
/// 盤點的行為本身不該影響被盤點的對象。
/// </para>
/// </summary>
public static class FirmwareSettingsReachFactsService
{
    private const string Category = "韌體";

    public const string ReadableKey = "fw.settings.readable";
    public const string BlockedKey = "fw.settings.blocked";

    /// <summary>一個來源的探測結果。</summary>
    public sealed record Probe(string Name, bool Reachable, string? Detail);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<Probe>? probes = null)
    {
        var list = probes ?? ReadAll();

        var reachable = list.Where(p => p.Reachable).ToList();
        var blocked = list.Where(p => !p.Reachable).ToList();

        return
        [
            new HardwareFact(ReadableKey, Category, "可讀的韌體設定來源",
                reachable.Count == 0
                    ? "沒有任何來源可讀"
                    : string.Join("、", reachable.Select(p => p.Name)), "個",
                "逐一探測各韌體來源的可及性（唯讀，不改變狀態）",
                FactTrustLevel.Measured, false, at, reachable.Count,
                reachable.Count == 0 ? FactAvailability.ReadError : FactAvailability.Present,
                reachable.Count == 0
                    ? "**所有來源都讀不到**——這通常代表權限不足或平台不支援，"
                      + "而不是「這台機器沒有韌體設定」。要進一步查請看 fw.settings.blocked"
                    : "這些來源現在讀得到——**讀得到不代表值對**，"
                      + "那是每一項各自要負責的事；本服務回答的是更前面的問題：這條路現在通不通"),

            new HardwareFact(BlockedKey, Category, "不可讀的韌體設定來源",
                blocked.Count == 0
                    ? "所有來源都讀得到"
                    : string.Join("；", blocked.Select(p =>
                        p.Detail is null ? p.Name : $"{p.Name}（{p.Detail}）")), "個",
                "逐一探測各韌體來源的可及性（唯讀，不改變狀態）",
                FactTrustLevel.Measured, false, at, blocked.Count,
                blocked.Count == 0 ? FactAvailability.Present : FactAvailability.Unknown,
                blocked.Count == 0
                    ? "所有探測的來源都讀得到——這是有意義的陳述："
                      + "它排除了「看不到是因為權限」這個常見解釋"
                    : "**讀不到與「沒有這個值」在使用者眼裡長得一樣——都是空的。**"
                      + "這一項把它們分開：上面列的是「這條路現在不通」，"
                      + "而不是「這裡沒有東西」"),
        ];
    }

    /// <summary>
    /// 探測各來源。<b>每一項各自 try/catch</b>——一個來源掛掉不該讓整份盤點變成空的。
    /// </summary>
    public static IReadOnlyList<Probe> ReadAll()
    {
        var probes = new List<Probe>
        {
            ProbeRegistry("UEFI 變數（SecureBoot）",
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled"),

            ProbeRegistry("BCD 設定（開機載入器）",
                @"SYSTEM\CurrentControlSet\Control\Session Manager\BootExecute", null),

            ProbeRegistry("電源設定（快速開機）",
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled"),

            ProbeRegistry("BIOS 資訊（WMI 代理）",
                @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVendor"),

            ProbeRegistry("韌體資料表（ACPI）",
                @"HARDWARE\ACPI\DSDT", null),

            ProbeRegistry("記憶體對應（mmio）",
                @"HARDWARE\RESOURCEMAP\System Resources\Physical Memory", null),
        };

        return probes;
    }

    private static Probe ProbeRegistry(string name, string subKey, string? valueName)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            if (k is null) return new Probe(name, false, "登錄機碼不存在");

            // 只要看能不能列舉——**不讀值內容**，因為盤點不該順便蒐集資料。
            _ = k.GetValueNames();
            if (valueName is not null)
            {
                object? v = k.GetValue(valueName);
                if (v is null) return new Probe(name, false, $"機碼在但沒有 {valueName}");
            }
            return new Probe(name, true, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new Probe(name, false, "權限不足");
        }
        catch (Exception ex)
        {
            Diag.Swallow("韌體來源探測", ex, $"{name} 探測失敗");
            return new Probe(name, false, ex.GetType().Name);
        }
    }
}
