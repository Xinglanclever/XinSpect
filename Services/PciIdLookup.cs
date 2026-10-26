using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>
/// PCI 名稱查詢的<b>兩層</b>：先問本專案背書過的手工表，再問上游登錄檔的快照。
///
/// <para>
/// <b>為什麼要分兩層而不是合成一張表：</b>這兩個來源的<b>擔保強度不同</b>。
/// <see cref="PciKnowledge"/> 的 15 個廠商是「本專案逐條核對過、寫得出出處」的；
/// <see cref="PciIdDatabase"/> 的 2,519 個廠商與 21,619 個裝置是「上游登錄檔這麼寫」的
/// （社群維護、接受投稿、錯誤率不是零）。合成一張表會讓兩者看起來一樣可信，
/// 而<b>那個差別正是讀者需要知道的</b>——所以值裡永遠標明這一段名字來自哪一層。
/// </para>
///
/// <para>
/// <b>查詢順序固定：</b>手工表 → 上游快照 → 未收錄。手工表優先不是因為它比較準，
/// 而是因為它的名字是中文語境下慣用的短名（「Intel」而不是「Intel Corporation」），
/// 而畫面空間有限。
/// </para>
/// </summary>
public static class PciIdLookup
{
    /// <summary>名稱的來源層。</summary>
    public enum NameOrigin
    {
        /// <summary>本專案的手工表（<see cref="PciKnowledge"/>）。</summary>
        Curated,

        /// <summary>上游登錄檔快照（<see cref="PciIdDatabase"/>）。</summary>
        Registry,

        /// <summary>兩層都沒有。</summary>
        Unlisted,
    }

    /// <summary>一個查詢結果：名稱、來源層，以及（登錄檔層的）快照版本。</summary>
    /// <param name="Name">名稱；<see cref="NameOrigin.Unlisted"/> 時為 null。</param>
    /// <param name="Origin">這一格名字是誰說的。</param>
    /// <param name="Snapshot">登錄檔層專屬：快照版本字串（手工層為 null）。</param>
    public readonly record struct Hit(string? Name, NameOrigin Origin, string? Snapshot)
    {
        /// <summary>是否查到。</summary>
        public bool Found => Origin != NameOrigin.Unlisted;
    }

    /// <summary>廠商 ID → 名稱（先手工表、再登錄檔快照）。</summary>
    public static Hit Vendor(ushort vendorId)
    {
        if (PciKnowledge.VendorName(vendorId) is { } curated)
            return new Hit(curated, NameOrigin.Curated, null);
        if (PciIdDatabase.VendorName(vendorId) is { } registered)
            return new Hit(registered, NameOrigin.Registry, PciIdDatabase.SnapshotVersion);
        return new Hit(null, NameOrigin.Unlisted, null);
    }

    /// <summary>裝置 ID → 名稱。<b>只有登錄檔層</b>——手工表從來沒有收錄裝置型號。</summary>
    public static Hit Device(ushort vendorId, ushort deviceId)
        => PciIdDatabase.DeviceName(vendorId, deviceId) is { } registered
            ? new Hit(registered, NameOrigin.Registry, PciIdDatabase.SnapshotVersion)
            : new Hit(null, NameOrigin.Unlisted, null);

    /// <summary>
    /// 名稱出處段落的<strong>固定開頭</strong>——統計回收靠它切段。
    ///
    /// <para>
    /// <b>為什麼要有這個常數，而不是讓回收端比對整句中文：</b>第 283 輪之前
    /// <c>KnowledgeLookupCollector</c> 是用「值裡有沒有出現『未收錄』」來判斷命中，
    /// 於是「廠商查到了、裝置沒收錄」和「兩個都沒收錄」被算成同一種，
    /// 而「登錄檔給的名字」與「本專案核對過的名字」更是完全分不出來。
    /// 那條路徑能成立只因為當時兩層的輸出剛好長那樣——<b>相鄰的字串不是契約</b>。
    /// 現在值裡有一段固定形狀的出處說明，產生端與回收端共用這個常數。
    /// </para>
    /// </summary>
    public const string OriginClausePrefix = "名稱出處：";

    /// <summary>出處代號：本專案手工表。</summary>
    public const string CuratedToken = "手工表";

    /// <summary>出處代號：上游登錄檔快照（後面接快照版本）。</summary>
    public const string RegistryToken = "登錄檔";

    /// <summary>出處代號：兩層都沒有。</summary>
    public const string UnlistedToken = "未收錄";

    /// <summary>一段名稱的出處寫成值裡的字串（`手工表`／`登錄檔 2026.10.01`／`未收錄`）。</summary>
    public static string OriginText(Hit hit) => hit.Origin switch
    {
        NameOrigin.Curated => CuratedToken,
        NameOrigin.Registry => $"{RegistryToken} {hit.Snapshot}",
        _ => UnlistedToken,
    };

    /// <summary>
    /// 一行裝置描述：角色（類別碼知識）＋廠商＋裝置名＋原始 ID，
    /// 末尾附一段<strong>名稱出處</strong>說明兩段名字各是誰說的。
    ///
    /// <para>
    /// <b>出處為什麼搬到句尾而不是逐段括號：</b>逐段標會讓裝置名後面什麼都沒有
    /// （裝置只有登錄檔一層），看起來像「這一段不需要標」——而它其實更需要標。
    /// 兩段並列在同一句，讀者一眼看到「廠商＝手工表、裝置＝登錄檔」，也知道差別在哪。
    /// </para>
    /// </summary>
    public static string Describe((ushort VendorId, ushort DeviceId) ids,
        (byte BaseClass, byte SubClass, byte ProgIf, byte Revision) cls)
    {
        string role = PciKnowledge.BaseClassName(cls.BaseClass) is { } baseName
            ? PciKnowledge.SubClassName(cls.BaseClass, cls.SubClass) is { } subName
                ? $"{baseName}／{subName}"
                : baseName
            : "類別碼未收錄";

        var vendor = Vendor(ids.VendorId);
        string vendorText = vendor.Found ? vendor.Name! : $"Vendor 0x{ids.VendorId:X4}（未收錄）";

        var device = Device(ids.VendorId, ids.DeviceId);
        string deviceText = device.Found ? device.Name! : $"Device 0x{ids.DeviceId:X4}（未收錄）";

        return $"{role} ・ {vendorText} {deviceText}（0x{ids.VendorId:X4}:0x{ids.DeviceId:X4}）"
             + $" ・ 類別碼 0x{cls.BaseClass:X2}{cls.SubClass:X2}/progIF 0x{cls.ProgIf:X2} rev 0x{cls.Revision:X2}"
             + $" ・ {OriginClausePrefix}廠商={OriginText(vendor)}、裝置={OriginText(device)}";
    }

    /// <summary>一句話說明這張表有多大、多新、誰擔保——給事實層用。</summary>
    public static string Coverage()
    {
        var db = PciIdDatabase.Database;
        if (db.Failure is { Length: > 0 } failure)
            return "登錄檔快照載入失敗：" + failure;

        return $"手工表 15 個廠商（本專案核對）；上游登錄檔快照 {PciIdDatabase.SnapshotVersion}："
             + $"{db.VendorCount:N0} 個廠商、{db.DeviceCount:N0} 個裝置（社群維護）。"
             + "查不到只代表這份快照沒有那一格，不代表裝置有問題。";
    }
}
