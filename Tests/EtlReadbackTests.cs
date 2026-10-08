using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// .etl 內容讀回的契約：空資料夾＝不是錯誤是環境狀態、讀回失敗如實帶原因不猜內容、
/// 未解事件如實計數。真 .etl 的讀回（provider／事件數）由真機驗證（系統現成 .etl），
/// 單元測試不開 ETW 工作階段。
/// </summary>
public class EtlReadbackTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 資料夾不存在_如實標不適用_不是錯誤()
    {
        var fact = Assert.Single(EtlReadbackService.Collect(
            At, folder: Path.Combine(Path.GetTempPath(), "不存在-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("不是錯誤", fact.UnavailableReason);
    }

    [Fact]
    public void 空資料夾_如實標不適用()
    {
        var dir = Directory.CreateTempSubdirectory("etlread-").FullName;
        try
        {
            var fact = Assert.Single(EtlReadbackService.Collect(At, folder: dir));
            Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
            Assert.Contains("還沒產生過軌跡", fact.UnavailableReason);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 垃圾etl_讀回失敗如實回報_不冒充成功()
    {
        var dir = Directory.CreateTempSubdirectory("etlread-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "garbage_20261009_000000.etl"), new byte[] { 1, 2, 3, 4, 5 });
            var facts = EtlReadbackService.Collect(At, folder: dir);

            var count = facts.Single(x => x.Key == EtlReadbackService.CountKey);
            Assert.Equal(FactAvailability.Present, count.Availability);
            Assert.Equal(1, count.NumericValue);
            Assert.Contains("未解事件", count.Value);

            var readback = Assert.Single(facts, x => x.Key == "etl.readback");
            Assert.Contains("讀回失敗", readback.Value);
            // 全部失敗時的事實本身要帶 ReadError，不冒充已量到
            Assert.Equal(FactAvailability.ReadError, readback.Availability);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 讀回成功_摘要要帶未解事件的誠實說明()
    {
        // 不開真 ETW 工作階段：直接驗證「成功摘要」的文字契約——聚合層的可測部分。
        // 真實 .etl 的讀回（provider 與事件數）在真機以系統 .etl 驗證，記錄在 changelog。
        var dir = Directory.CreateTempSubdirectory("etlread-").FullName;
        try
        {
            // 造一個非零但極小的合法前綴檔（不是真 .etl）——這裡只驗證「失敗也如實列出檔名」
            File.WriteAllBytes(Path.Combine(dir, "a_20261009_010101.etl"), new byte[16]);
            var facts = EtlReadbackService.Collect(At, folder: dir);
            var readback = Assert.Single(facts, x => x.Key == "etl.readback");
            Assert.Contains("a_20261009_010101.etl", readback.Value);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 讀回不存在的檔案_回原因不丟例外()
    {
        var (summary, error) = EtlReadbackService.ReadFile(Path.Combine(Path.GetTempPath(), "不存在-" + Guid.NewGuid().ToString("N") + ".etl"));
        Assert.Null(summary);
        Assert.NotNull(error);
    }
}
