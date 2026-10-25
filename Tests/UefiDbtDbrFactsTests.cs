using System;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// dbt／dbr 簽章資料庫事實的守門（U10-FW-0005 的同構擴充）。
/// 守的是：計數與逐條清單同構於 db／dbx；「變數不存在」＝NotApplicable（環境事實）；
/// 權限不足＝InsufficientPrivilege（不得讀成「沒有」）；短資料拒解；清單最多列 5 條。
/// </summary>
public class UefiDbtDbrFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>造一條 EFI_SIGNATURE_LIST：型別 GUID＋清單大小＋n 筆（Owner GUID 16＋資料）。</summary>
    private static byte[] SigList(string typeGuid, int sigCount, int sigDataBytes = 32)
    {
        int sigSize = 16 + sigDataBytes;
        int listSize = 28 + sigCount * sigSize;
        var list = new byte[listSize];
        new Guid(typeGuid).ToByteArray().CopyTo(list, 0);
        BitConverter.GetBytes((uint)listSize).CopyTo(list, 16);
        BitConverter.GetBytes(0u).CopyTo(list, 20);      // SignatureHeaderSize
        BitConverter.GetBytes((uint)sigSize).CopyTo(list, 24);
        return list;
    }

    private static byte[] TwoListData()
        => SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 2)
           .Concat(SigList("A5C2E7C3-8D9F-4A2B-9C3D-1E2F3A4B5C6D", 1)).ToArray();

    private static System.Collections.Generic.IReadOnlyList<HardwareFact> Facts(byte[]? dbt)
        => UefiDbtDbrFactsService.Collect(At, name => name == "dbt" ? dbt : null);

    // ── dbt 有值 ────────────────────────────────────────────────────────

    [Fact]
    public void dbt有值時要給計數與逐條清單()
    {
        var facts = Facts(TwoListData());

        var count = facts.Single(f => f.Key == "uefi.dbt.count");
        Assert.Equal(FactAvailability.Present, count.Availability);
        Assert.Equal(3.0, count.NumericValue); // 2＋1 條
        Assert.Contains("3 條簽章（2 條清單）", count.Value);
        Assert.Contains("dbt（授權時戳簽章資料庫）", count.Name);

        var list0 = facts.Single(f => f.Key == "uefi.dbt.list.0");
        Assert.Equal(FactAvailability.Present, list0.Availability);
        Assert.Contains("C1C4F266-A914-414F-90EB-2B3A3D903F0D", list0.Value);
        Assert.Equal(2.0, list0.NumericValue);

        var list1 = facts.Single(f => f.Key == "uefi.dbt.list.1");
        Assert.Contains("A5C2E7C3-8D9F-4A2B-9C3D-1E2F3A4B5C6D", list1.Value);
        Assert.Equal(1.0, list1.NumericValue);
    }

    [Fact]
    public void 超過五條清單時只列前五條但計數要完整()
    {
        var data = Enumerable.Range(0, 7)
            .Select(i => SigList($"0000000{i % 10}-0000-0000-0000-00000000000{i}", 1))
            .Aggregate((a, b) => a.Concat(b).ToArray());
        var facts = Facts(data);

        var count = facts.Single(f => f.Key == "uefi.dbt.count");
        Assert.Equal(7.0, count.NumericValue);
        Assert.Equal(5, facts.Count(f => f.Key.StartsWith("uefi.dbt.list.", StringComparison.Ordinal)));
        Assert.NotNull(facts.SingleOrDefault(f => f.Key == "uefi.dbt.list.4"));
        Assert.Null(facts.SingleOrDefault(f => f.Key == "uefi.dbt.list.5"));
    }

    // ── 讀不到的各種原因 ────────────────────────────────────────────────

    [Fact]
    public void dbr不存在時要標不適用並說出那是韌體狀態()
    {
        var facts = Facts(TwoListData()); // dbt 有、dbr 無

        var dbr = facts.Single(f => f.Key == "uefi.dbr.count");
        Assert.Equal(FactAvailability.NotApplicable, dbr.Availability);
        Assert.Null(dbr.NumericValue);
        Assert.Contains("ERROR_FILE_NOT_FOUND", dbr.UnavailableReason);
        Assert.Contains("韌體沒有佈建這個資料庫", dbr.UnavailableReason);
        Assert.Contains("不是讀取失敗", dbr.UnavailableReason);
    }

    [Fact]
    public void 兩個變數都不存在時各自如實標不適用()
    {
        var facts = Facts(null);

        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.NotApplicable, f.Availability);
            Assert.Null(f.NumericValue);
        });
        Assert.Equal(2, facts.Count);
    }

    [Fact]
    public void 權限不足時要標InsufficientPrivilege而不是不存在()
    {
        var facts = UefiDbtDbrFactsService.CollectWithRichReader(At, name =>
            new FirmwareVariableAccess.VariableReadResult(null,
                FirmwareVariableAccess.ErrorPrivilegeNotHeld, 0, false));

        var dbt = facts.Single(f => f.Key == "uefi.dbt.count");
        Assert.Equal(FactAvailability.InsufficientPrivilege, dbt.Availability);
        Assert.Contains("SeSystemEnvironmentPrivilege", dbt.UnavailableReason);
        Assert.Contains("這不是「變數不存在」", dbt.UnavailableReason);
    }

    [Fact]
    public void 其他Win32錯誤時要標不支援並照抄原因()
    {
        var facts = UefiDbtDbrFactsService.CollectWithRichReader(At, name =>
            new FirmwareVariableAccess.VariableReadResult(null, 1, 0, true));

        var dbr = facts.Single(f => f.Key == "uefi.dbr.count");
        Assert.Equal(FactAvailability.NotSupported, dbr.Availability);
        Assert.Contains("不能讀成「沒有這個資料庫」", dbr.UnavailableReason);
        Assert.Contains("Win32 1", dbr.UnavailableReason);
    }

    [Fact]
    public void 短於一個簽章清單標頭時要如實拒解()
    {
        var facts = Facts(new byte[10]);

        var dbt = facts.Single(f => f.Key == "uefi.dbt.count");
        Assert.Equal(FactAvailability.NotSupported, dbt.Availability);
        Assert.Contains("只有 10 bytes", dbt.UnavailableReason);
        Assert.Contains("如實拒解，不猜", dbt.UnavailableReason);
    }

    // ── GUID 查證與通路界線 ─────────────────────────────────────────────

    /// <summary>
    /// 通路界線要成文：dbt/dbr 與 db/dbx 同命名空間 GUID、dbr 與 dbDefault 是不同變數。
    /// </summary>
    [Fact]
    public void 服務檔要寫明GUID查證與dbr語意的界線()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(
                   System.IO.Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, "Services", "UefiDbtDbrFactsService.cs"));

        Assert.Contains("d719b2cb-3d3a-4596-a3bc-dad00e67656f", text);
        Assert.Contains("EFI_IMAGE_SECURITY_DATABASE2", text);
        Assert.Contains("dbDefault", text);
        Assert.Contains("不同的變數", text);
    }

    /// <summary>計數鍵以 const 字面宣告（掃描器只認字面），前綴不含 Key（家族前綴不進鍵集）。</summary>
    [Fact]
    public void 鍵與前綴的字面宣告要與db家族同構()
    {
        Assert.Equal("uefi.dbt.count", UefiDbtDbrFactsService.DbtCountKey);
        Assert.Equal("uefi.dbt.list.", UefiDbtDbrFactsService.DbtListPrefix);
        Assert.Equal("uefi.dbr.count", UefiDbtDbrFactsService.DbrCountKey);
        Assert.Equal("uefi.dbr.list.", UefiDbtDbrFactsService.DbrListPrefix);
    }
}
