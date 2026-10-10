using System.Buffers.Binary;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Shimcache 事實服務（FS-001）三態契約：登錄值不存在、讀取失敗、格式辨認不出、Win8 未實作、正常解析——
/// 每條都不得把「讀不到」畫成「沒有執行痕跡」，界線文字要寫進值裡。
/// </summary>
public class ShimcacheFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 登錄值不存在_如實說缺席不冒充沒有執行痕跡()
    {
        var facts = ShimcacheFactsService.Collect(At, () => null);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不存在", f.UnavailableReason);
    }

    [Fact]
    public void 登錄值讀取失敗_標讀取錯誤不冒充沒有()
    {
        var facts = ShimcacheFactsService.Collect(At, () => throw new System.Security.SecurityException("denied"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("SecurityException", f.UnavailableReason);
        Assert.Contains("讀不到不等於沒有", f.UnavailableReason);
    }

    [Fact]
    public void 格式辨認不出_如實說不猜()
    {
        var facts = ShimcacheFactsService.Collect(At, () => new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("格式辨認不出", f.UnavailableReason);
        Assert.Contains("不猜", f.UnavailableReason);
    }

    [Fact]
    public void Win8家族_格式辨認出但未實作_如實標不適用()
    {
        byte[] raw = new byte[192];
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0, 4), 0x80);
        System.Text.Encoding.ASCII.GetBytes("00ts").CopyTo(raw, 128);
        var facts = ShimcacheFactsService.Collect(At, () => raw);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("Win8/Server2012", f.Value);
        Assert.Contains("未對準出處", f.UnavailableReason);
    }

    [Fact]
    public void 正常解析_三件事實樣本帶界線與敏感標記()
    {
        var t = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var raw = BuildWin10((@"\??\C:\Windows\System32\notepad.exe", t, true));
        var facts = ShimcacheFactsService.Collect(At, () => raw);

        Assert.Equal(3, facts.Count);
        var count = facts.Single(f => f.Key == "fs.shim.count");
        Assert.Equal(1, count.NumericValue);
        Assert.Contains("執行痕跡≠執行成功", count.Value);

        var latest = facts.Single(f => f.Key == "fs.shim.latest");
        Assert.Contains("notepad.exe", latest.Value);
        Assert.Contains("不是執行時間", latest.Value);

        var sample = facts.Single(f => f.Key == "fs.shim.sample");
        Assert.True(sample.Sensitive, "路徑可能含使用者名——樣本條目要標敏感，匯出預設遮蔽");
        Assert.Contains("執行痕跡≠執行成功", sample.Value);
        Assert.Contains("不是中毒判決", sample.Value);
        Assert.Contains("只讀 CurrentControlSet", sample.Value);
    }

    [Fact]
    public void 申報與解析數不一致_如實並列()
    {
        var raw = BuildWin10(
            (@"\??\C:\a.exe", DateTimeOffset.UtcNow, true),
            (@"\??\C:\b.exe", DateTimeOffset.UtcNow, false));
        // 表頭申報 5、實際 2——解碼器照實解析，服務層要把落差講出來
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x28, 4), 5);
        var facts = ShimcacheFactsService.Collect(At, () => raw);
        var count = facts.Single(f => f.Key == "fs.shim.count");
        Assert.Equal(2, count.NumericValue);
        Assert.Contains("申報 5", count.Value);
        Assert.Contains("解析 2", count.Value);
    }

    /// <summary>Win10 家族合成值（與解碼器金標同款產生器）。</summary>
    private static byte[] BuildWin10(params (string Path, DateTimeOffset? Time, bool Executed)[] entries)
    {
        var records = new List<byte>();
        foreach (var (path, time, executed) in entries)
        {
            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(path);
            byte[] data = new byte[8];
            if (executed) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 1);
            var rec = new List<byte>();
            rec.AddRange(System.Text.Encoding.ASCII.GetBytes("10ts"));
            rec.AddRange(new byte[4]);
            rec.AddRange(BitConverter.GetBytes(8));
            rec.AddRange(BitConverter.GetBytes((ushort)pathBytes.Length));
            rec.AddRange(pathBytes);
            rec.AddRange(BitConverter.GetBytes((time ?? DateTimeOffset.FromFileTime(0)).ToFileTime()));
            rec.AddRange(BitConverter.GetBytes(data.Length));
            rec.AddRange(data);
            records.AddRange(rec);
        }
        byte[] value = new byte[0x34 + records.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(0, 4), 0x34);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(0x28, 4), entries.Length);
        records.CopyTo(value, 0x34);
        return value;
    }
}
