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
    /// 一行裝置描述：角色（類別碼知識）＋廠商＋裝置名＋原始 ID。
    /// <b>每一段都標明它的來源層</b>；未收錄的段落如實標「未收錄」，不猜。
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
        string vendorText = vendor.Origin switch
        {
            NameOrigin.Curated => vendor.Name!,
            NameOrigin.Registry => $"{vendor.Name}（登錄檔 {vendor.Snapshot}）",
            _ => $"Vendor 0x{ids.VendorId:X4}（未收錄）",
        };

        var device = Device(ids.VendorId, ids.DeviceId);
        string deviceText = device.Found
            ? device.Name!
            : $"Device 0x{ids.DeviceId:X4}（未收錄）";

        return $"{role} ・ {vendorText} {deviceText}（0x{ids.VendorId:X4}:0x{ids.DeviceId:X4}）"
             + $" ・ 類別碼 0x{cls.BaseClass:X2}{cls.SubClass:X2}/progIF 0x{cls.ProgIf:X2} rev 0x{cls.Revision:X2}";
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
