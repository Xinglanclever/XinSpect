namespace XinSpect;

/// <summary>
/// 動態鍵前綴目錄——「哪些鍵家族会在執行期出现、但成员编译期数不出来」的显式申报。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼需要這一份：</b><see cref="FactKeyCatalog"/> 把全鍵固定在編譯期，是為了解決
/// 「發佈版掃不到原始碼、把 0 讀成全覆蓋」（v2.36 缺陷）。但有一類鍵的<b>成員</b>由機器決定：
/// 匯流排上有幾張卡、SuperIO 探到哪個埠、PMU 有幾個固定計數器——全鍵目錄列不完，也不該假裝列得完。
/// 2026-10-10 的執行期對帳（FactKeyRuntimeReconcileTests）掃出 137 把這種鍵：它們遊蕩在
/// 「目錄說沒有、執行期天天生產」的縫隙裡，覆蓋申報完全看不見。
/// </para>
/// <para>
/// <b>规矩：</b>執行期出現的鍵要嘛是目錄全鍵、要嘛命中這裡的某個前綴，否則對帳紅燈。
/// 前綴本身必須在原始碼掃得到（<c>FactKeyCatalogTests</c> 守：登記了卻沒人生產＝殭屍前綴，紅燈），
/// 每筆要寫「為什麼编译期数不出成員」與「成員由什麼決定」——這是一份申報，不是一個免死金牌。
/// </para>
/// </remarks>
public static class FactKeyDynamicCatalog
{
    /// <summary>前綴 → （為什麼是動態家族＋成員由什麼決定）。前綴一律以「.」結尾，除非末段是埠號前綴。</summary>
    public static IReadOnlyDictionary<string, string> Families { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pci.dev."] =
                "bus 0 盤點每張存在的裝置一筆（成員＝裝置號 dev.fn）——有哪些裝置是機器的事實，编译期列不完",
            ["pci.res."] =
                "PCI 擴充資源（BAR）每張有資源的裝置一筆——同上，成員由匯流排枚舉決定",
            ["sio.0x"] =
                "Super I/O 探測結果按實際探到的指標埠命名（0x2E／0x4E，可能还有别的）——埠號是平台事實，不是编译期常量",
            ["sio.hwm.0x"] =
                "SuperIO HWM 感測（風扇／溫度／電壓／輸入電壓）按探測到的埠分組——同 sio.0x，埠號由探測決定",
            ["pmu.fixed."] =
                "固定功能計數器逐個一筆（pmu.fixed.0…），數量由 CPUID 0xA 的報告決定（2／3／4 個的架構都存在），不寫死成員",
            ["reconcile."] =
                "交叉對帳的結論鍵，每條規則一筆（鍵由規則 id 決定）——結論不是被對帳的事實，刻意不進覆蓋申報的分母，但它是真實的執行期輸出，在這裡如實登記",
            ["audio.latency."] =
                "音訊端點逐端點一筆（端點名來自 MMDevice 枚舉）——這台機器裝了什麼音訊裝置由使用者環境決定",
            ["audio.endpoint."] =
                "音訊端點逐一個一筆（MMDevice 列舉的索引）——端點數量由機器的音訊裝置決定，编译期数不出來",
            ["drvinsp."] =
                "驅動靜態檢視逐檔一筆（索引＋欄位名，如 0.summary）——盘點到幾個可疑驅動是掃描結果，不是编译期常量",
            ["storage.reliability."] =
                "單碟可靠性計數器逐碟逐欄一筆（碟序 × 19 欄）——有幾顆碟、哪幾欄提供者沒給，都是機器與驅動器韌體的事實",
            ["asset.field."] =
                "SMBIOS 資產欄位逐一筆（欄名來自表定義：系統製造商／主機板序號…）——欄位集是規範的性質不是常數，且缺失欄也要有地方標缺",
            ["sa.ifeo."] =
                "IFEO 逐映像一筆（映像名來自登錄檔枚舉，任意字串）——哪些映像掛了 Debugger 由機器的事實決定；成員是『掛了東西』的映像，沒掛的不產鍵",
            ["sa.lsp."] =
                "Winsock LSP 逐條一筆（條目號）——裝了哪些分層服務提供者是機器的安裝事實；sa.lsp.count 是固定全鍵，在目錄",
            ["display.adapter."] =
                "顯示轉接器逐張一筆（名稱來自 WMI）——有幾張卡、叫什麼名是機器的事實",
            ["mon."] =
                "顯示器連線逐台一筆（索引）——接了幾台螢幕由環境決定",
            ["monitor."] =
                "顯示器實體逐台一筆（實例名 DesktopMonitorN…）——名稱與數量由 WMI 枚舉決定；monitor.summary 是固定全鍵，在目錄",
            ["numa.node."] =
                "NUMA 節點逐個一筆（節點號）——節點數由 GetNumaHighestNodeNumber 回報，單節點到 8 節點的平台都存在",
            ["role.installed."] =
                "Windows 伺服器角色／功能逐一筆（角色名）——裝了哪些是这台機器的安裝事實，一台 Core 機能有幾十個角色；name 含大寫與連字號，本就不符合全鍵目錄的格式規定，必須走家族",
        };

    /// <summary>執行期鍵是否命中某個已登記的動態家族。</summary>
    public static bool Matches(string key)
    {
        foreach (var prefix in Families.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }
}
