using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace XinSpect;

/// <summary>
/// headless 的 SMART 屬性列舉（第 274 輪）：把 <c>ata.smart.attr.lookup</c> 從「鍵已登記、值待接入」
/// 接到真值——回答「這一輪實際讀到哪幾顆碟的哪些屬性列」。
///
/// <para>
/// <b>為什麼不直接用 <see cref="StorageSmartService.Rows"/>：</b>那是 UI 的實例狀態
/// （<see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>，跟著儲存頁的生命週期走），
/// 收集鏈在 headless 的 <c>--json</c>／CLI／匯出路徑上根本沒有那個實例；
/// 而第 271 輪留下的另一條顧忌是「同步直讀有卡死史」。這一支把兩件事一起解掉：
/// 它<b>自己列舉、自己讀、自己收工</b>，不碰任何 UI 物件。
/// </para>
///
/// <para>
/// <b>三條硬性約束（本機實測踩過的坑，違反會讓整條收集鏈卡死）：</b>
/// </para>
/// <list type="number">
/// <item><b>絕不讀 NVMe 日誌頁。</b>本機 <c>PhysicalDrive 2／4</c> 的 NVMe log 讀取會卡在
/// 核心不可中斷等待，連提權後的 <c>taskkill /F</c> 都殺不掉（第 263 輪實證）。
/// 所以這裡只走 ATA 通道（<see cref="StorageSmartService.ChannelOf"/>＝
/// <see cref="StorageSmartService.StorageBusChannel.Ata"/>），NVMe 一律略過並寫明原因。</item>
/// <item><b>外層預算＋專屬執行緒。</b>照第 263 輪的形狀：每次讀取都跑在一條
/// <b>專屬執行緒</b>上（不是執行緒集區——2026-10-14 的舊傷就是集區被卡死的查詢吃光，
/// 整個行程連一行輸出都印不出來），最多等 <see cref="DefaultBudgetMs"/>；
/// 逾時就放棄並如實標「逾時」，絕不阻塞收集。</item>
/// <item><b>唯讀、零網路、不寫任何硬體狀態。</b>只送 SMART READ DATA（0xB0／0xD0），
/// 沒有任何 SEND、沒有 SMART ENABLE／DISABLE。</item>
/// </list>
///
/// <para>
/// <b>時間預算的分層（老實說清楚每一層在哪）：</b>內層是既有的 <see cref="DiskIo"/> 看門狗
/// （<c>DefaultTimeoutMs</c>＝3 秒，逾時會 <see cref="StorageProbeBreaker.Trip"/>），
/// 它仍然借執行緒集區；外層這裡的 6 秒專屬執行緒是<b>保險</b>——防的是內層看門狗以外的東西卡住
/// （例如匯流排型別查詢或列舉本身）。兩層都逾時時，呼叫端一定拿得回控制權。
/// </para>
///
/// <para>
/// <b>執行緒洩漏的代價（沿 <see cref="StorageProbeBreaker"/> 的既有斷路器文字）：</b>
/// 卡住的執行緒中止不了，只能留在原地。這裡的界線是「一顆碟、一條查詢、一個冷卻期內最多洩一條」——
/// 逾時會記進斷路器，冷卻期間（10 分鐘）整條查詢直接跳過，所以第二輪起不會再為同一顆卡死的碟
/// 多付一條執行緒。交換的是「一條背景執行緒永遠不回來」換「呼叫端一定拿得回控制權」，
/// 而後者才是「讀不到」能被誠實回報的前提。
/// </para>
/// </summary>
public static class SmartLookupProbe
{
    /// <summary>
    /// 整條探針的外層預算（毫秒）。照第 263 輪的形狀：6 秒。
    /// 這<b>不是</b>每顆碟各 6 秒——是整條探針共用一個截止時間，
    /// 否則三顆卡死的碟就會讓一次收集多等 18 秒（而「收集不能被單一裝置拖住」正是這個預算要防的事）。
    /// </summary>
    public const int DefaultBudgetMs = 6000;

    /// <summary>
    /// NVMe 略過的成文原因。<b>不是「不明原因跳過」</b>——原因、實證輪次與後果都要寫出來，
    /// 否則下一個人會以為只是忘了接，然後把它「順手補上」。
    /// </summary>
    public const string NvmeSkipReason =
        "本專案不在收集時讀 NVMe 日誌頁——實測會卡死（第 263 輪：本機 PhysicalDrive 2／4 的 NVMe log 讀取"
        + "停在核心不可中斷等待，連 taskkill /F 都殺不掉；headless --json 跑 35 分鐘沒有輸出）。"
        + "健康值請看儲存頁的 NVMe 段（UI 實例狀態），這裡不重讀";

    /// <summary>探針的結論。前三態是「讀不到的三種不同原因」，<see cref="Ok"/> 是「讀到了」。</summary>
    public enum ProbeStatus
    {
        /// <summary>讀到至少一筆屬性列。</summary>
        Ok,

        /// <summary>沒有可讀的 ATA 通道碟（沒有碟、或全都是 NVMe／USB／RAID／虛擬碟）——環境事實，不是錯誤。</summary>
        NotApplicable,

        /// <summary>有 ATA 碟，但一顆都讀不到（逾時、驅動拒絕、列舉失敗）。</summary>
        ReadError,

        /// <summary>碟回應了，但屬性表是空的——「讀到 0 筆」與「讀不到」是不同的斷言。</summary>
        Unknown,
    }

    /// <summary>一顆被略過的碟與<b>成文的原因</b>。<see cref="Index"/> 為 <c>-1</c>＝來源沒給編號。</summary>
    public sealed record DiskSkip(int Index, string Model, string Reason);

    /// <summary>
    /// 一次探針的結果。<see cref="Rows"/> 只含真正讀到的屬性列（不摻任何推測值）；
    /// <see cref="Skipped"/> 逐顆記錄「為什麼沒讀」。
    /// </summary>
    public sealed record Result(
        ProbeStatus Status,
        IReadOnlyList<SmartRow> Rows,
        IReadOnlyList<int> ReadIndexes,
        IReadOnlyList<DiskSkip> Skipped,
        int DisksEnumerated,
        string? Reason)
    {
        /// <summary>讀到了幾筆屬性列。</summary>
        public int AttributeCount => Rows.Count;

        /// <summary>
        /// 覆蓋說明（一句話，寫進事實的值裡）：幾顆碟、讀了哪幾顆、幾筆屬性、略過幾顆與為什麼。
        /// <b>略過的原因逐字保留</b>——它不是給人略過的藉口，是給下一個人判斷要不要補的依據。
        /// </summary>
        public string CoverageNote
        {
            get
            {
                var parts = new List<string> { $"碟列舉 {DisksEnumerated} 顆" };
                parts.Add(ReadIndexes.Count > 0
                    ? $"已讀 ATA {ReadIndexes.Count} 顆（屬性 {Rows.Count} 筆）"
                    : "未讀任何 ATA 碟");
                if (Skipped.Count > 0)
                {
                    string groups = string.Join("；", Skipped
                        .GroupBy(s => s.Reason, StringComparer.Ordinal)
                        .Select(g => $"{g.Count()} 顆＝{g.Key}"));
                    parts.Add($"略過 {Skipped.Count} 顆：{groups}");
                }
                return string.Join("；", parts);
            }
        }
    }

    /// <summary>
    /// 列舉碟並讀 ATA SMART 屬性列。
    /// <b>預設參數就是生產路徑</b>（既有列舉 <see cref="PeripheralFirmwareFactsService.ReadInventory"/>、
    /// 既有 <see cref="StorageSmartService.TryGetBusType"/>／<see cref="StorageSmartService.TryReadAtaAttributes"/>）；
    /// 測試全部注入假件，<b>不真的讀碟</b>。
    /// </summary>
    /// <param name="inventory">
    /// 碟列舉。<b>刻意不新增第二份列舉</b>——預設直接重用周邊韌體盤點那一次
    /// <c>Win32_DiskDrive</c> 查詢（它已經帶回 <c>Index</c> 與型號，型號是知識表補強路徑的輸入）。
    /// 回 <c>null</c>＝列舉本身失敗（與「沒有碟」不同）。
    /// </param>
    /// <param name="busOf">匯流排型別（STORAGE_BUS_TYPE）。通道判定一律交給 <see cref="StorageSmartService.ChannelOf"/>。</param>
    /// <param name="readAttributes">讀一顆碟的屬性列；<c>null</c>＝讀不到（逾時或驅動拒絕），<b>不回空清單</b>。</param>
    /// <param name="budgetMs">整條探針的外層預算；測試用小的值驗逾時。</param>
    public static Result Run(
        Func<PeripheralFirmwareFactsService.PeripheralInventory?>? inventory = null,
        Func<int, uint>? busOf = null,
        Func<int, string?, IReadOnlyList<SmartRow>?>? readAttributes = null,
        int budgetMs = DefaultBudgetMs)
    {
        inventory ??= PeripheralFirmwareFactsService.ReadInventory;
        busOf ??= static index => StorageSmartService.TryGetBusType(index, out _);
        readAttributes ??= static (index, model) => StorageSmartService.TryReadAtaAttributes(index, model);
        if (budgetMs <= 0) budgetMs = DefaultBudgetMs;

        PeripheralFirmwareFactsService.PeripheralInventory? inv;
        try { inv = inventory(); }
        catch (Exception ex)
        {
            // 列舉自己拋例外＝「列舉失敗」，不是「沒有碟」。
            return new Result(ProbeStatus.ReadError, [], [], [], 0,
                "碟列舉失敗（Win32_DiskDrive 查詢拋例外）：" + ex.Message);
        }

        if (inv is null)
            return new Result(ProbeStatus.ReadError, [], [], [], 0,
                "碟列舉失敗（來源回 null：類別不存在或查詢被拒）——「讀不到」與「這台機器沒有碟」是不同的斷言");

        if (inv.Disks.Count == 0)
            return new Result(ProbeStatus.NotApplicable, [], [], [], 0,
                "沒有列舉到實體硬碟（來源可用、查詢成功，只是回空——虛擬機與純 USB 開機碟可能不出現在實體列舉裡）");

        var rows = new List<SmartRow>();
        var readIndexes = new List<int>();
        var skipped = new List<DiskSkip>();
        var failures = new List<string>();
        int ataCandidates = 0;
        int skippedByBudgetOrBreaker = 0;

        var sw = Stopwatch.StartNew();
        foreach (var disk in inv.Disks)
        {
            if (disk.Index < 0)
            {
                skipped.Add(new DiskSkip(disk.Index, disk.Model,
                    "來源沒有給 Index（Win32_DiskDrive.Index），無法定址 \\\\.\\PHYSICALDRIVEn"));
                continue;
            }

            uint bus = busOf(disk.Index);
            switch (StorageSmartService.ChannelOf(bus))
            {
                case StorageSmartService.StorageBusChannel.Nvme:
                    skipped.Add(new DiskSkip(disk.Index, disk.Model, NvmeSkipReason));
                    continue;
                case StorageSmartService.StorageBusChannel.None:
                    skipped.Add(new DiskSkip(disk.Index, disk.Model,
                        $"匯流排 {StorageSmartService.BusTypeName(bus)} 沒有可用的唯讀直讀通道"
                        + "（USB 外接盒／RAID 虛擬碟／虛擬磁碟多半不轉發 ATA SMART），不猜"));
                    continue;
            }

            ataCandidates++;

            if (StorageProbeBreaker.ShouldSkip(disk.Index, StorageSmartService.AtaAttributesOp))
            {
                skippedByBudgetOrBreaker++;
                skipped.Add(new DiskSkip(disk.Index, disk.Model,
                    "斷路器冷卻中：上一輪這顆碟的 ATA 屬性查詢逾時，本輪不再問"
                    + $"（冷卻 {StorageProbeBreaker.CooldownMs / 60000} 分鐘；見 StorageProbeBreaker）"));
                continue;
            }

            int remaining = budgetMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0)
            {
                skippedByBudgetOrBreaker++;
                skipped.Add(new DiskSkip(disk.Index, disk.Model,
                    $"外層 {budgetMs} 毫秒預算已用盡（前幾顆碟吃完了），本輪未讀"));
                continue;
            }

            var (list, timedOut) = OnOwnThread(() => readAttributes(disk.Index, disk.Model), remaining);
            if (timedOut)
            {
                failures.Add($"PhysicalDrive {disk.Index}：外層 {budgetMs} 毫秒預算內沒回來（逾時，已放棄）");
                continue;
            }
            if (list is null)
            {
                failures.Add($"PhysicalDrive {disk.Index}：驅動拒絕或未回應 ATA SMART READ DATA（讀不到，不代填）");
                continue;
            }

            readIndexes.Add(disk.Index);
            rows.AddRange(list);
        }

        if (readIndexes.Count > 0)
        {
            return rows.Count > 0
                ? new Result(ProbeStatus.Ok, rows, readIndexes, skipped, inv.Disks.Count, null)
                : new Result(ProbeStatus.Unknown, rows, readIndexes, skipped, inv.Disks.Count,
                    $"碟回應了 ATA SMART READ DATA（PhysicalDrive {string.Join("、", readIndexes)}），"
                    + "但屬性表是空的——「讀到 0 筆」與「讀不到」是不同的斷言");
        }

        if (failures.Count > 0)
            return new Result(ProbeStatus.ReadError, [], [], skipped, inv.Disks.Count,
                string.Join("；", failures));

        if (ataCandidates > 0 && skippedByBudgetOrBreaker > 0)
            return new Result(ProbeStatus.ReadError, [], [], skipped, inv.Disks.Count,
                $"有 {ataCandidates} 顆 ATA 碟，本輪一顆都沒讀（斷路器冷卻或預算用盡）");

        return new Result(ProbeStatus.NotApplicable, [], [], skipped, inv.Disks.Count,
            "沒有 ATA 通道的碟——知識表這一輪沒有可查詢的輸入（這不是讀不到，是沒有對象）");
    }

    /// <summary>
    /// 在<b>專屬</b>執行緒上跑 <paramref name="read"/>，最多等 <paramref name="budgetMs"/>；
    /// 逾時就把那條執行緒留在原地（中止不了，理由見類別說明）。
    /// </summary>
    /// <remarks>
    /// 刻意不用 <c>Task.Run</c>／<see cref="DiskIo.GuardedProbe{T}"/>：那裡借的是執行緒集區，
    /// 而集區被卡死的查詢吃光時，整個行程連一行輸出都印不出來（2026-10-14 傾印）。
    /// 讀取自己拋例外＝讀不到，但<b>不標逾時</b>——「裝置回一句不支援」與「裝置不回來」是兩件事。
    /// </remarks>
    private static (IReadOnlyList<SmartRow>? Value, bool TimedOut) OnOwnThread(
        Func<IReadOnlyList<SmartRow>?> read, int budgetMs)
    {
        IReadOnlyList<SmartRow>? result = null;
        var t = new Thread(() =>
        {
            try { result = read(); }
            catch { /* 讀取自己拋例外：不是逾時，由呼叫端記成讀不到 */ }
        })
        { IsBackground = true, Name = "XinSpect-smart-lookup-probe" };
        t.Start();
        return t.Join(budgetMs) ? (result, false) : (null, true);
    }
}
