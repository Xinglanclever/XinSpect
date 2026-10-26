using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XinSpect;

/// <summary>
/// PCI 廠商／裝置 ID 的完整離線對照表（第 281 輪，知識包第二批）。
///
/// <para>
/// <b>出處與授權：</b>資料取自 PCI ID Project 的 <c>pci.ids</c>（本版快照 <b>2026.10.01</b>），
/// 由 <c>cdn.jsdelivr.net/gh/vcrhonek/hwdata@master/pci.ids</c> 下載後<b>只抽廠商與裝置兩段</b>
/// （丟掉子系統、類別碼、註解），存成 TSV 再 gzip 內嵌。那份檔案的檔頭自己寫明
/// 「可在 GPL-2.0-or-later <b>或</b> 3-clause BSD 之下散布」——本專案採用後者，
/// 因此這份衍生資料<b>不會讓本專案的 AGPL-3.0-or-later 擴散到整個程式</b>。
/// 授權與義務記在 <c>THIRD-PARTY-NOTICES.md</c>。
/// </para>
///
/// <para>
/// <b>為什麼要有這一份：</b>既有的 <see cref="PciKnowledge"/> 是<b>手工子集</b>（15 個廠商），
/// 它的價值在「每一條都說得出出處」；但真機上的裝置大多在子集之外，於是畫面長期顯示
/// 「Vendor 0xXXXX（未收錄）」——那句話是誠實的，卻也讓「未收錄」與「查不到」看起來一樣。
/// 這一份把廠商覆蓋率推到 2,519 條、裝置名推到 21,619 條，同時<b>保留手工表為第一順位</b>：
/// 手工表的名字是「本專案背書過的」，這一份是「上游登錄檔說的」，兩者在值裡分得開。
/// </para>
///
/// <para>
/// <b>界線（寫進事實，不藏在註解）：</b>
/// </para>
/// <list type="bullet">
/// <item><b>裝置名是上游登錄檔的字串，不是本專案驗證過的型號。</b>同一組 ID 在不同廠牌、
///   不同批次可能有不同商品名；登錄檔也接受社群投稿，錯誤率不是零。</item>
/// <item><b>查不到不代表裝置有問題</b>——只代表這份快照沒有那一格。</item>
/// <item><b>不做子系統比對</b>：那要讀 0x2C 並比對 subvendor:subdevice，本版沒有收錄子系統表，
///   所以刻意不猜。</item>
/// <item><b>快照日期寫在事實裡</b>——離線知識會過期，讀者有權知道自己看的是哪一天的資料。</item>
/// </list>
/// </summary>
public static class PciIdDatabase
{
    /// <summary>資料檔的內嵌名稱（見 <c>XinSpect.Decoders.csproj</c>）。</summary>
    private const string ResourceName = "XinSpect.Decoders.Data.pci-ids.tsv.gz";

    /// <summary>上游快照的版本字串（<c>pci.ids</c> 檔頭的 Version 欄）。</summary>
    public const string SnapshotVersion = "2026.10.01";

    /// <summary>上游專案網址。</summary>
    public const string SourceUrl = "https://pci-ids.ucw.cz/";

    /// <summary>授權（資料檔自述的雙授權，本專案採後者）。</summary>
    public const string License = "BSD-3-Clause（pci.ids 自述為 GPL-2.0-or-later 或 BSD-3-Clause 雙授權）";

    private static readonly Lazy<Index> Lazy = new(Load, isThreadSafe: true);

    /// <summary>索引本體：兩張唯讀字典＋載入失敗原因。</summary>
    public sealed class Index
    {
        internal Index(Dictionary<ushort, string> vendors,
            Dictionary<(ushort Vendor, ushort Device), string> devices, string? failure)
        {
            Vendors = vendors;
            Devices = devices;
            Failure = failure;
        }

        internal Dictionary<ushort, string> Vendors { get; }
        internal Dictionary<(ushort, ushort), string> Devices { get; }

        /// <summary>載入失敗的原因；成功時為 <c>null</c>。</summary>
        public string? Failure { get; }

        /// <summary>廠商數。</summary>
        public int VendorCount => Vendors.Count;

        /// <summary>裝置數。</summary>
        public int DeviceCount => Devices.Count;
    }

    /// <summary>載入結果（單例、只解一次；失敗不拋例外——知識表壞掉不該讓整個掃描崩潰）。</summary>
    public static Index Database => Lazy.Value;

    /// <summary>廠商 ID → 名稱。未收錄回 <c>null</c>。</summary>
    public static string? VendorName(ushort vendorId)
        => Lazy.Value.Vendors.TryGetValue(vendorId, out string? n) && n.Length > 0 ? n : null;

    /// <summary>廠商＋裝置 ID → 裝置名。未收錄回 <c>null</c>。</summary>
    public static string? DeviceName(ushort vendorId, ushort deviceId)
        => Lazy.Value.Devices.TryGetValue((vendorId, deviceId), out string? n) ? n : null;

    /// <summary>廠商數（載入失敗時為 0）。</summary>
    public static int VendorCount => Lazy.Value.VendorCount;

    /// <summary>裝置數（載入失敗時為 0）。</summary>
    public static int DeviceCount => Lazy.Value.DeviceCount;

    private static Index Load()
    {
        var vendors = new Dictionary<ushort, string>(2600);
        var devices = new Dictionary<(ushort, ushort), string>(22000);
        try
        {
            using var raw = typeof(PciIdDatabase).Assembly.GetManifestResourceStream(ResourceName);
            if (raw is null)
                return new Index(vendors, devices,
                    $"內嵌資料檔 {ResourceName} 不存在——知識表缺席；"
                    + "呼叫端要如實標「未收錄」，不能講成「沒有這個裝置」");

            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);

            // 每行四段（以定位字元分隔）：
            //   V<TAB>廠商4hex<TAB>名稱
            //   D<TAB>廠商4hex<TAB>裝置4hex<TAB>名稱
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length < 9) continue;
                char kind = line[0];
                if (line[1] != '\t') continue;
                if (!ushort.TryParse(line.AsSpan(2, 4), System.Globalization.NumberStyles.HexNumber,
                        null, out ushort vendor)) continue;

                if (kind == 'V' && line.Length > 7 && line[6] == '\t')
                {
                    vendors[vendor] = line[7..];
                }
                else if (kind == 'D' && line.Length > 12 && line[6] == '\t' && line[11] == '\t')
                {
                    if (!ushort.TryParse(line.AsSpan(7, 4), System.Globalization.NumberStyles.HexNumber,
                            null, out ushort device)) continue;
                    devices[(vendor, device)] = line[12..];
                }
            }
            return new Index(vendors, devices, null);
        }
        catch (Exception ex)
        {
            return new Index(vendors, devices, "內嵌知識表解壓失敗：" + ex.Message
                + "——知識表壞掉不該讓掃描崩潰，但也不能假裝查得到");
        }
    }
}
