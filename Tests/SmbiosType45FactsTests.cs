using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS Type 45（Firmware Inventory Information）解碼的守門（U10-SB-0161）。
///
/// <para>
/// <b>核心：</b>①版本閘——SMBIOS &lt;3.5 是 NotApplicable（環境事實），不得寫成「沒有韌體組件」；
/// ②字串區慣例——索引 0＝無字串，不得把 0 當成第一個字串；
/// ③對照規則——只有「版本字串完全相等」這一條有出處，模糊比對會把「字串一樣」說成「同一個組件」。
/// </para>
/// </summary>
public class SmbiosType45FactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>合成一筆 Type 45（位移核對 dmidecode case 45）。</summary>
    private static SmbiosStruct Type45(
        string? name = "TPM Firmware", string? version = "2.0.1", string? fwId = "ID-1",
        string? release = "2026-01-01", string? maker = "ACME", string? lowest = "2.0.0",
        ulong imageSize = 0x200000, ushort characteristics = 0x0001, byte state = 0x04,
        ushort[]? handles = null, int length = 0x18)
    {
        var strings = new List<string>();
        int Str(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            strings.Add(s);
            return strings.Count;
        }
        var d = new byte[Math.Max(length, 0x18 + (handles?.Length ?? 0) * 2)];
        d[0] = 45;
        d[1] = (byte)d.Length;
        d[0x04] = (byte)Str(name);
        d[0x05] = (byte)Str(version);
        d[0x06] = 0x01;                                   // 版本格式（未收錄對照表）
        d[0x07] = (byte)Str(fwId);
        d[0x08] = 0x01;                                   // 韌體 ID 格式
        d[0x09] = (byte)Str(release);
        d[0x0A] = (byte)Str(maker);
        d[0x0B] = (byte)Str(lowest);
        BitConverter.GetBytes(imageSize).CopyTo(d, 0x0C);
        BitConverter.GetBytes(characteristics).CopyTo(d, 0x14);
        d[0x16] = state;
        d[0x17] = (byte)(handles?.Length ?? 0);
        for (int i = 0; i < (handles?.Length ?? 0); i++)
            BitConverter.GetBytes(handles[i]).CopyTo(d, 0x18 + i * 2);
        return new SmbiosStruct(45, 0x2D00, d, strings.ToArray());
    }

    private static SmbiosType45FactsService.SmbiosTable Table(byte major, byte minor, params SmbiosStruct[] structs)
        => new(structs, major, minor);

    private static SmbiosType45FactsService.TableProbe Probe(SmbiosType45FactsService.SmbiosTable? t) => () => t;

    private static PeripheralFirmwareFactsService.PeripheralInventory Inventory(
        params (string Model, string Firmware)[] disks)
        => new(disks.Select(d => new PeripheralFirmwareFactsService.DeviceFirmware("SSD", d.Model, d.Firmware)).ToList(), 0);

    [Fact]
    public void SMBIOS低於三五要標不適用且是環境事實()
    {
        var facts = SmbiosType45FactsService.Collect(At, Probe(Table(3, 4, Type45())), () => Inventory());
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains("環境事實", facts[0].UnavailableReason);
        Assert.Contains("3.4", facts[0].UnavailableReason);
        // 版本門檻擋在解碼之前：明明有 Type 45 結構也不該出逐筆鍵
        Assert.DoesNotContain(facts, f => f.Key.StartsWith("sb.type45.item."));
    }

    [Fact]
    public void 表讀取失敗兩把鍵都標讀取錯誤()
    {
        var facts = SmbiosType45FactsService.Collect(At, Probe(null));
        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    [Fact]
    public void 三五以上但沒有Type45要標韌體沒有列()
    {
        var other = new SmbiosStruct(0, 0, new byte[] { 0, 0x09, 0, 0 }, []);
        var facts = SmbiosType45FactsService.Collect(At, Probe(Table(3, 5, other)));
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains("韌體沒有提供韌體組件清單", facts[0].UnavailableReason);
    }

    [Fact]
    public void 解碼逐欄核對()
    {
        var facts = SmbiosType45FactsService.Collect(At,
            Probe(Table(3, 5, Type45(characteristics: 0x0003, state: 0x06, handles: [0x1234]))));
        var item = facts.Single(f => f.Key == "sb.type45.item.0");
        Assert.Equal(FactAvailability.Present, item.Availability);
        Assert.Contains("組件名 TPM Firmware", item.Value);
        Assert.Contains("版本 2.0.1", item.Value);
        Assert.Contains("韌體 ID ID-1", item.Value);
        Assert.Contains("發行日 2026-01-01", item.Value);
        Assert.Contains("製造商 ACME", item.Value);
        Assert.Contains("最低支援版本 2.0.0", item.Value);
        Assert.Contains("2,097,152 bytes", item.Value);
        Assert.Contains("Updatable 是、Write-Protect 是", item.Value);   // 0x0003 兩個位元
        Assert.Contains("狀態 待機離線", item.Value);                     // 0x06
        Assert.Contains("0x1234", item.Value);
        Assert.Contains("未收錄對照表", item.Value);                      // 版本格式代碼不解讀

        var exist = facts.Single(f => f.Key == SmbiosType45FactsService.ExistenceKey);
        Assert.Equal(1, exist.NumericValue);
    }

    [Fact]
    public void 字串索引零與空白字串都是沒有而不是空字串()
    {
        var facts = SmbiosType45FactsService.Collect(At,
            Probe(Table(3, 8, Type45(name: null, version: null, maker: "  "))));
        var item = facts.Single(f => f.Key == "sb.type45.item.0");
        Assert.DoesNotContain("組件名", item.Value);
        Assert.DoesNotContain("版本 2.0.1", item.Value);
        Assert.DoesNotContain("製造商", item.Value);
    }

    [Fact]
    public void 結構長度不足要逐筆標解不開()
    {
        var shortStruct = new SmbiosStruct(45, 0x2D00, new byte[] { 45, 0x10, 0x00, 0x2D }, []);
        var facts = SmbiosType45FactsService.Collect(At, Probe(Table(3, 5, shortStruct)), () => null);
        var item = facts.Single(f => f.Key == "sb.type45.item.0");
        Assert.Equal(FactAvailability.ReadError, item.Availability);
        Assert.Contains("如實拒解", item.UnavailableReason);
        // 存在性仍成立：表裡有 Type 45 這件事不因解不開而消失
        Assert.Equal(FactAvailability.Present, facts.Single(f => f.Key == SmbiosType45FactsService.ExistenceKey).Availability);
    }

    [Fact]
    public void 對照_只有版本字串完全相等才計交集()
    {
        // Type 45 自述版本 "2.0.1"；硬碟：一顆同字串、一顆不同、一顆不自述。
        var facts = SmbiosType45FactsService.Collect(At,
            Probe(Table(3, 5, Type45(version: "2.0.1"))),
            () => Inventory(("Disk A", "2.0.1"), ("Disk B", "9.9.9"), ("Disk C", "")));
        var cross = facts.Single(f => f.Key == SmbiosType45FactsService.CrossKey);
        Assert.Equal(FactAvailability.Present, cross.Availability);
        Assert.Equal(1, cross.NumericValue);
        Assert.Contains("完全相等", cross.Value);
        Assert.Contains("Type 45 端無對應 0 筆、周邊端未出現在 Type 45 1 筆", cross.Value);
        Assert.Contains("不做模糊比對", cross.Value);
        Assert.Contains("不自述", cross.Value);
    }

    [Fact]
    public void 對照_周邊盤點失敗要說缺一半而不是裝作交集為零()
    {
        var facts = SmbiosType45FactsService.Collect(At,
            Probe(Table(3, 5, Type45(version: "2.0.1"))), () => null);
        var cross = facts.Single(f => f.Key == SmbiosType45FactsService.CrossKey);
        Assert.Contains("對照面缺一半", cross.Value);
        Assert.Contains("交集無從計起", cross.Value);
    }
}
