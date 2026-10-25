using System;
using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// FV／FFS 分解對照的守門（U10-FW-0050）。
///
/// <para>
/// <b>核心：</b>本服務只消費既有的 <see cref="UefiFvInfo"/> 清單（不重讀快閃），
/// 並且要把「分解過」與「只列名不解開」分開陳述——後者是專案界線，不是解析失敗。
/// </para>
/// </summary>
public class FirmwareFvCrossFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static UefiFvSection Sect(string typeName) =>
        new(typeName, 0x100, null, 0x10);

    private static UefiFvFile File(string guid, bool named, params UefiFvSection[] sections) =>
        new(guid, "DRIVER", 0x200, true, false, named ? "Mod" + guid[..4] : null, sections);

    private static UefiFvInfo Fv(int index, params UefiFvFile[] files) =>
        new(index, 0, "0A2E3F4A-0000-0000-0000-00000000000" + index, 0x10000, 2,
            files.Length, 0, 0, true, false, files);

    [Fact]
    public void 沒有走訪結果要標不支援並說出不重讀()
    {
        var fact = FirmwareFvCrossFactsService.Collect(At, null);
        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Contains("不自己讀 SPI 快閃", fact.UnavailableReason);
        Assert.Null(fact.NumericValue);
    }

    [Fact]
    public void 沒有FVH標記是不適用不是錯誤()
    {
        var fact = FirmwareFvCrossFactsService.Collect(At, []);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有可辨識的 _FVH 標記", fact.UnavailableReason);
    }

    [Fact]
    public void 分解過與只列名要分開陳述()
    {
        var fvs = new List<UefiFvInfo>
        {
            Fv(0,
                File("11111111-1111-1111-1111-111111111111", named: true, Sect("PE32")),
                File("22222222-2222-2222-2222-222222222222", named: false, Sect("GUID_DEFINED")),
                File("33333333-3333-3333-3333-333333333333", named: true, Sect("COMPRESSION"))),
            Fv(1,
                File("44444444-4444-4444-4444-444444444444", named: false,
                    Sect("PE32"), Sect("FIRMWARE_VOLUME_IMAGE"))),
        };

        var fact = FirmwareFvCrossFactsService.Collect(At, fvs);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(4, fact.NumericValue);                        // 合計 4 個 FFS 檔案
        Assert.Null(fact.UnavailableReason);

        Assert.Contains("FV#0：檔案 3", fact.Value);
        Assert.Contains("FV#1：檔案 1", fact.Value);
        Assert.Contains("含未展開壓縮 2", fact.Value);              // FV#0 的 GUID_DEFINED＋COMPRESSION
        Assert.Contains("巢狀 FV 引用 1", fact.Value);
        Assert.Contains("具 UI 名 2 個、僅以 GUID 呈現 2 個", fact.Value);
        Assert.Contains("界線", fact.Value);
        Assert.Contains("不是解析失敗", fact.Value);
    }

    [Fact]
    public void 校驗與略過數要接出來()
    {
        var fv = new UefiFvInfo(0, 0, "GUID", 0x10000, 2, 1, 3, 2, false, false,
            [File("55555555-5555-5555-5555-555555555555", named: false)]);
        var fact = FirmwareFvCrossFactsService.Collect(At, [fv]);
        Assert.Contains("表頭校驗和不符 2 個（如實呈現，不拒收）", fact.Value);
        Assert.Contains("略過 3 個", fact.Value);
    }

    [Fact]
    public void 超過八個FV要說等不假裝列完()
    {
        var fvs = new List<UefiFvInfo>();
        for (int i = 0; i < 10; i++) fvs.Add(Fv(i, File("AAAAAAAA-0000-0000-0000-00000000000" + i, named: false)));
        var fact = FirmwareFvCrossFactsService.Collect(At, fvs);
        Assert.Contains("；等 10 個 FV", fact.Value);
        Assert.Equal(10, fact.NumericValue);
    }
}
