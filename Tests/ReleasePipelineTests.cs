using System;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 發版流水線的守門（v2.54）：Tools/release.ps1 把「預檢→全綠→commit→tag→push→publish→
/// 上傳→驗證→位元組數 docs commit」收成一支腳本，取代十一個版本累積下來的手動十二步——
/// 手動流程真的漂移過（v2.53 的 lightweight tag 沒被 --follow-tags 推上去、三份 README
/// 徽章漏同步一次）。這裡釘住的是腳本的<b>存在與關鍵閘門</b>：刪掉任何一道閘門就紅，
/// 因為那正是歷史上出過事的地方。
/// </summary>
public class ReleasePipelineTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(dir.FullName + "/XinSpect.csproj"))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, Path.Combine(parts));
    }

    private static string ReadAll(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(b, 3, b.Length - 3);
        return System.Text.Encoding.UTF8.GetString(b);
    }

    [Fact]
    public void release腳本存在且帶BOM且每一道歷史出過事的閘門都在()
    {
        string p = RepoFile("Tools", "release.ps1");
        Assert.True(File.Exists(p), "Tools/release.ps1 不見了——發版回到手動十二步等於回到會漏 tag 的狀態");

        byte[] head = File.ReadAllBytes(p)[..3];
        Assert.True(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF,
            "release.ps1 必須是 UTF-8 BOM（PS 5.1 無 BOM 讀 UTF-8 會炸中文）");

        string s = ReadAll(p);
        // 每一道標記對應一次真實的疏失或教訓：
        Assert.Contains("trx;LogFileName", s, StringComparison.Ordinal);   // 全綠才准發（以 trx 計數器為準）
        Assert.Contains("ls-remote --tags", s, StringComparison.Ordinal);  // v2.53：--follow-tags 不推 lightweight tag
        Assert.Contains("push origin \"v$Version\"", s, StringComparison.Ordinal); // tag 一律顯式推
        Assert.Contains("PublishSingleFile=true", s, StringComparison.Ordinal);    // 資產必須是單檔版
        Assert.Contains("publish_release.py", s, StringComparison.Ordinal);       // gh 已死，走 REST
        Assert.Contains("verify-release.ps1", s, StringComparison.Ordinal);       // 發佈後驗證
        Assert.Contains("docs：README 主程式位元組數", s, StringComparison.Ordinal); // 位元組數 docs commit
        Assert.Contains("RELEASE-FAIL", s, StringComparison.Ordinal);      // 任一步失敗立即中止
    }

    [Fact]
    public void 上傳腳本入庫且token不回顯且逐資產驗證大小()
    {
        string p = RepoFile("Tools", "publish_release.py");
        Assert.True(File.Exists(p), "Tools/publish_release.py 不見了——上傳退回手工 curl 等於回到會漏傳的狀態");
        string s = ReadAll(p);
        Assert.Contains("git\", \"credential", s, StringComparison.Ordinal);        // token 來自 GCM
        Assert.Contains("capture_output=True", s, StringComparison.Ordinal);        // token 走記憶體管道
        Assert.DoesNotContain("print(token", s, StringComparison.Ordinal);          // 而且絕不回顯
        Assert.Contains("uploads.github.com", s, StringComparison.Ordinal);       // 資產上傳走 uploads 主機
        Assert.Contains("up[\"size\"] == len(blob)", s, StringComparison.Ordinal); // 逐資產大小對帳（曾發生靜默漏傳）
        Assert.Contains("--tag", s, StringComparison.Ordinal);
    }

    [Fact]
    public void 三份README的主程式列都是腳本正則能命中的形狀()
    {
        // 位元組數自動同步依賴這個形狀：zh 兩份是「N,NNN bytes」、en 是概數 MB。
        // 形狀一變，腳本就會靜默漏改——所以形狀本身也要有測試釘著。
        string zh = ReadAll(RepoFile("README.md"));
        string zhCn = ReadAll(RepoFile("README.zh-CN.md"));
        foreach ((string name, string text) in new[] { ("README.md", zh), ("README.zh-CN.md", zhCn) })
        {
            var row = text.Split('\n').First(l => l.Contains("/XinSpect.exe) |", StringComparison.Ordinal));
            Assert.Matches(@"XinSpect\.exe\)\s*\|\s*\d{1,3}(?:,\d{3})+\s+bytes\s*\|", row);
        }
        string en = ReadAll(RepoFile("README.en.md"));
        Assert.Contains("/XinSpect.exe) | 30", en, StringComparison.Ordinal);   // 概數列仍在
    }
}
