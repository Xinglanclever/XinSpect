using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// iSCSI 工作階段深化（Vol1 第 ST-020 項）：<c>st.iscsi.sessions</c>、
/// <c>st.iscsi.paths</c>——把「iSCSI 服務在不在跑」（<c>ent.iscsi</c>，WP24）推進到
/// <b>「現在有幾條工作階段、走幾條路徑、有沒有走多路徑」</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>與既有能力的界線：</b><c>ent.iscsi</c> 只回答<b>發起器服務的存亡</b>；
/// 一台服務在跑但一條工作階段都沒有的機器，與一台掛著三顆 LUN 的機器，
/// 在那把鍵上長得一模一樣。本項補的就是那一層。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>未連線如實</b>——沒有工作階段是 <see cref="FactAvailability.Present"/> 的
/// 「0 條」而不是不適用；iSCSI 服務裝了但還沒接任何目標是<b>正常的中間狀態</b>；
/// ② <b>不列目標位址與 IQN</b>：IQN 常含組織網域與機器名，目標位址是儲存拓撲——
/// 那兩樣加起來就等於把內網儲存架構說出來，與「這台機器有沒有在用 iSCSI」無關；
/// ③ 多路徑計數只回答<b>一條 LUN 被幾條路徑服務</b>，不判斷哪條路徑比較好。
/// </para>
/// </remarks>
public static class IscsiSessionFactsService
{
    private const string Cat = "儲存";
    private const string Source = @"root\Microsoft\Windows\Storage（MSFT_Disk／MSFT_iSCSISession，唯讀）";

    /// <summary>ST-020 iSCSI 工作階段。</summary>
    public const string SessionsKey = "st.iscsi.sessions";
    /// <summary>ST-020 iSCSI 多路徑。</summary>
    public const string PathsKey = "st.iscsi.paths";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>一條工作階段：是否已登入、走 TCP 或 iSER（皆不含目標位址與名稱）。</summary>
    public sealed record SessionRow(bool LoggedIn, bool IsIser);

    /// <summary>（以 iSCSI 掛載的磁碟數, 有超過一條路徑的磁碟數, 路徑總數）。</summary>
    public sealed record PathState(int IscsiDisks, int MultiPathDisks, int TotalPaths);

    public delegate IReadOnlyList<SessionRow>? SessionsProbe();
    public delegate PathState? PathsProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        SessionsProbe? sessions = null,
        PathsProbe? paths = null)
        =>
        [
            SessionsFact(at, sessions),
            PathsFact(at, paths),
        ];

    // ── ST-020 工作階段 ───────────────────────────────────────────────────

    public static HardwareFact SessionsFact(DateTimeOffset at, SessionsProbe? probe = null)
    {
        IReadOnlyList<SessionRow>? rows;
        try { rows = (probe ?? ReadSessions)(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, SessionsKey, "iSCSI 工作階段", FactAvailability.ReadError,
                "iSCSI 工作階段查詢失敗——讀不到就是不猜（不畫成 0 條）");

        int loggedIn = rows.Count(r => r.LoggedIn);
        int iser = rows.Count(r => r.IsIser);
        // 0 條是值：服務裝了但還沒接目標，是正常的中間狀態。
        string detail = rows.Count == 0
            ? "目前沒有任何 iSCSI 工作階段（發起器已安裝但尚未登入任何目標——這是正常的中間狀態，不是故障）"
            : $"{rows.Count} 條工作階段（已登入 {loggedIn}、未登入 {rows.Count - loggedIn}）";

        return new HardwareFact(SessionsKey, Cat, "iSCSI 工作階段",
            $"{detail}・iSER（RDMA）工作階段 {iser} 條。" +
            "<b>不列目標位址與 IQN</b>：IQN 常含組織網域與機器名、目標位址就是儲存拓撲，" +
            "兩者加起來等於把內網儲存架構說出來，與「這台機器有沒有在用 iSCSI」無關。" +
            "已登入不等於 I/O 正常——連線活著但目標無回應是另一回事。",
            "條", Source, FactTrustLevel.Reported, false, at, rows.Count);
    }

    // ── ST-020 多路徑 ─────────────────────────────────────────────────────

    public static HardwareFact PathsFact(DateTimeOffset at, PathsProbe? probe = null)
    {
        PathState? s;
        try { s = (probe ?? ReadPaths)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PathsKey, "iSCSI 多路徑", FactAvailability.ReadError,
                "iSCSI 路徑查詢失敗——讀不到就是不猜");

        var v = s;
        if (v.IscsiDisks == 0)
            return new HardwareFact(PathsKey, Cat, "iSCSI 多路徑",
                "沒有以 iSCSI 掛載的磁碟。", "顆", Source, FactTrustLevel.Reported, false, at, 0);

        // 單路徑不是缺點：單一網卡、單一交換器的環境本來就只有一條。
        string mp = v.MultiPathDisks == 0
            ? "沒有磁碟走多路徑——單路徑不是缺點（單一網卡／單一交換器的環境本來就只有一條）"
            : $"其中 {v.MultiPathDisks} 顆走多路徑";

        return new HardwareFact(PathsKey, Cat, "iSCSI 多路徑",
            $"iSCSI 磁碟 {v.IscsiDisks} 顆・{mp}・路徑總數 {v.TotalPaths}。" +
            "<b>只報「一條 LUN 被幾條路徑服務」</b>，不判斷哪條路徑比較好、" +
            "也不判斷負載平衡策略是否合理——那取決於交換器與目標端的設定，本機看不到。" +
            "多路徑的價值在於單點故障時還有一條路，但<b>有兩條路不等於兩條都健康</b>。",
            "條", Source, FactTrustLevel.Reported, false, at, v.TotalPaths);
    }

    // ── 讀取層 ────────────────────────────────────────────────────────────

    /// <summary>
    /// WMI 通路：iSCSI 工作階段。<b>只取登入狀態與協定型別</b>——
    /// 查詢刻意不含 TargetNodeAddress／InitiatorName（紅線②在程式碼上的落點）。
    /// 提供者不存在＝回 <c>null</c>（由呼叫端轉 ReadError，不冒充 0 條）。
    /// </summary>
    internal static IReadOnlyList<SessionRow>? ReadSessions()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT IsLoggedIn FROM MSFT_iSCSISession");
            var list = new List<SessionRow>();
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    bool loggedIn = false;
                    try { loggedIn = o["IsLoggedIn"] is bool b && b; } catch { /* 欄位缺席＝未登入，如實 */ }
                    // 協定型別欄位在部分版本才有；沒有就一律算 TCP（iSER 是少數）。
                    bool iser = false;
                    try { iser = o["SessionType"] is { } st && Convert.ToInt32(st) != 0; } catch { }
                    list.Add(new SessionRow(loggedIn, iser));
                }
            return list;
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// WMI 通路：以 iSCSI 掛載的磁碟與其路徑數。走 <c>MSFT_Disk</c> 的 BusType（10＝iSCSI，
    /// 與 <c>DiskInfoService</c> 同一組對照值）；路徑數取自 <c>MSFT_Disk</c> 的 PathCount
    /// （提供者不支援時回 0 並由值裡的計數看得出來）。
    /// </summary>
    internal static PathState? ReadPaths()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT BusType, PathCount FROM MSFT_Disk");
            int disks = 0, multi = 0, paths = 0;
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    int bus = o["BusType"] is { } b ? Convert.ToInt32(b) : -1;
                    if (bus != 10) continue;   // 10＝iSCSI（與 DiskInfoService 同表）
                    disks++;
                    int pc = 0;
                    try { pc = o["PathCount"] is { } p ? Convert.ToInt32(p) : 0; } catch { }
                    if (pc > 1) multi++;
                    paths += pc;
                }
            return new PathState(disks, multi, paths);
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
