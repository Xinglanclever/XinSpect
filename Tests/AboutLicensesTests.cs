using System;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 關於頁「第三方授權」清單（U10-UI-0034）的解析器守門。
///
/// <para>
/// <see cref="ThirdPartyNoticesCatalog"/> 從 THIRD-PARTY-NOTICES.md 解析「元件 × 版本 × 授權」，
/// 讀取順序「資源優先、檔案其次、皆無＝如實標缺」。本組測試釘三件事：
/// ① 解析器對合成內容能抽出正確數量的套件 × 授權（表頭與分隔列不算）；
/// ② 檔案與資源皆缺時回「讀不到」加原因——<b>不丟例外、也不拿空清單冒充</b>；
/// ③ 倉庫裡的實檔解析得出條目（解析器沒有跟清單格式脫鉤——格式一改這條就紅）。
/// </para>
/// </summary>
public class AboutLicensesTests
{
    /// <summary>合成 NOTICE：兩張表（欄序都是元件｜版本｜授權｜…），含表頭、分隔列與正文敘述。</summary>
    private const string SyntheticNotices = """
        # 合成清單（測試用）

        > 專案授權聲明段落——沒有管線列，不該被吃進條目。

        ## 執行期元件（隨程式分發）

        | 元件 | 版本 | 授權 | 義務 | 來源 |
        |---|---|---|---|---|
        | FakeLib | 1.2.3 | MPL-2.0 | 附授權文本連結 | https://example.com/fake |
        | FakeTwo | 0.9（Bridge 0.8） | MIT；內含 Nested（LGPL） | 附兩層來源連結 | https://example.com/two |
        | FakeThree | 2.* | MIT | 附授權文本連結 | https://example.com/three |

        ## 建置期／測試期元件（不隨程式分發）

        | 元件 | 版本 | 授權 | 用途 |
        |---|---|---|---|
        | FakeTestKit | 2.9 | Apache-2.0 | 測試框架 |
        | FakeMutator | 5.x | BSD-3-Clause | 突變測試 |

        ## 本專案自身

        專案自身的授權見 LICENSE。
        """;

    [Fact]
    public void 解析器對合成內容抽出套件與授權()
    {
        var entries = ThirdPartyNoticesCatalog.Parse(SyntheticNotices);

        Assert.Equal(5, entries.Count);
        Assert.Equal("FakeLib", entries[0].Component);
        Assert.Equal("1.2.3", entries[0].Version);
        Assert.Equal("MPL-2.0", entries[0].License);

        // 兩張表的第三欄都是授權；「MIT；內含 Nested（LGPL）」這種多層授權照原文抽出，不在解析器裡改寫。
        Assert.Equal("MIT；內含 Nested（LGPL）", entries[1].License);
        Assert.Equal("Apache-2.0", entries[3].License);
        Assert.Equal("BSD-3-Clause", entries[4].License);
        // 元件名不含表頭與分隔列。
        Assert.DoesNotContain(entries, e => e.Component == "元件");
        Assert.DoesNotContain(entries, e => e.Component.StartsWith('-'));
    }

    [Fact]
    public void 解析器對空內容與雜訊不丟例外()
    {
        Assert.Empty(ThirdPartyNoticesCatalog.Parse(""));
        Assert.Empty(ThirdPartyNoticesCatalog.Parse("完全沒有表格的文件"));
        // 只有表頭沒有資料列：抽不出條目（Load 會把這種如實標成「解析不出」，見下一條）。
        Assert.Empty(ThirdPartyNoticesCatalog.Parse("| 元件 | 版本 | 授權 |\n|---|---|---|\n"));
    }

    [Fact]
    public void 資源與檔案皆缺時如實標缺不冒充()
    {
        // 兩個讀取來源都回 null＝單檔發佈沒帶資源、磁碟也沒有檔案。
        var report = ThirdPartyNoticesCatalog.Load(() => null, _ => null);

        Assert.False(report.Available);
        Assert.Empty(report.Entries);
        Assert.False(string.IsNullOrWhiteSpace(report.UnavailableReason),
            "讀不到時要說出原因——不能用沒有原因的失敗或空清單冒充「沒有第三方元件」");
        Assert.Equal(ThirdPartyNoticesCatalog.MissingReason, report.UnavailableReason);
    }

    [Fact]
    public void 讀到了但抽不出內容也要如實說不是空清單()
    {
        // 檔案在、但內容是雜訊（格式變了）：同樣標不可用，且原因要跟「整檔讀不到」分得開。
        var report = ThirdPartyNoticesCatalog.Load(() => null, _ => "沒有表格的雜訊內容");

        Assert.False(report.Available);
        Assert.Empty(report.Entries);
        Assert.NotNull(report.UnavailableReason);
        Assert.NotEqual(ThirdPartyNoticesCatalog.MissingReason, report.UnavailableReason);
    }

    [Fact]
    public void 倉庫實檔解析得出條目()
    {
        // 走真的 Load（此時資源未內嵌於測試組件——但倉庫根的實檔存在，走磁碟退路）；
        // 這條釘住「解析器與清單格式沒有脫鉤」：清單格式一改、這裡先紅。
        var report = ThirdPartyNoticesCatalog.Load();

        // 2026-10-25 實測：執行期 7 筆 ＋ 建置期 4 筆 ＝ 11 筆。下限 11 給增補留餘地，
        // 實際筆數會印在失敗訊息裡。
        Assert.True(report.Available,
            $"倉庫的 THIRD-PARTY-NOTICES.md 讀不出清單：{report.UnavailableReason}");
        Assert.True(report.Entries.Count >= 11,
            $"清單只抽出 {report.Entries.Count} 筆（納管時實測 11）——解析器或清單格式壞了。" +
            $"實際抽出：{string.Join("、", report.Entries.Take(3).Select(e => e.Component))}…");

        // 首筆應是執行期表的第一列；授權欄不可空。
        Assert.Contains(report.Entries, e => e.Component.Contains("LibreHardwareMonitorLib"));
        Assert.DoesNotContain(report.Entries, e => string.IsNullOrWhiteSpace(e.License));
    }
}
