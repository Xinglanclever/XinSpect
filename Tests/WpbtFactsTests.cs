using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>SE-010 WPBT 表存在性（Vol2 推進第 56 項）的守門：由 acpi.tables 推導，只宣告存在性。</summary>
public class WpbtFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static HardwareFact TableList(string value, FactAvailability availability = FactAvailability.Present) =>
        new("acpi.tables", "ACPI", "ACPI 表清單", value, "", "EnumSystemFirmwareTables",
            FactTrustLevel.Measured, false, At, null, availability,
            availability == FactAvailability.Present ? null : "表清單不可用");

    [Fact]
    public void WPBT_在表清單裡_宣告存在且明講不解析()
    {
        var f = ClosingFactsService.WpbtFact(At, [TableList("DSDT SSDT WPBT FACP")]);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(1, f.NumericValue);
        Assert.Contains("存在", f.Value);
        Assert.Contains("不載入、不執行、不解析", f.Value);
    }

    [Fact]
    public void WPBT_不在表清單裡_不存在也是事實()
    {
        var f = ClosingFactsService.WpbtFact(At, [TableList("DSDT SSDT FACP")]);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0, f.NumericValue);
        Assert.Contains("未列舉到 WPBT", f.Value);
    }

    [Fact]
    public void WPBT_acpi_tables不在場_標Unknown不推測()
    {
        var f = ClosingFactsService.WpbtFact(At, [TableList("", FactAvailability.ReadError)]);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("不推測", f.UnavailableReason);
    }

    [Fact]
    public void WPBT_沒有acpi_tables這一鍵_同樣不推測()
    {
        var f = ClosingFactsService.WpbtFact(At, []);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
    }

    [Fact]
    public void WPBT_分類是進階安全_鍵在目錄()
    {
        var f = ClosingFactsService.WpbtFact(At, [TableList("WPBT")]);
        Assert.Equal("進階安全", f.Category);
        Assert.Contains("se.wpbt", FactKeyCatalog.Keys);
    }
}