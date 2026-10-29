using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 裝置樹全圖的守門（U10 第三圈 OS-0001，第 321 輪）。
///
/// <para>
/// <b>這一支盯四件事：</b>①層深與子樹規模要從樹真的數出來（不是把扁平清單當樹）；
/// ②「根」有兩種——真的沒有父節點、與「父不在這份清單裡」——兩者必須分開計數；
/// ③成環的節點不得無限遞迴，且要如實申報；④值裡不得出現 instance ID（那串常含序號）。
/// </para>
/// </summary>
public class DeviceTreeFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static DeviceDiagnosticDevice Dev(
        string id, string cls = "USB", string name = "裝置",
        string? parent = null, bool present = true, uint? problem = null,
        IReadOnlyList<DeviceDiagnosticDevice>? children = null)
        => new()
        {
            InstanceId = id,
            FriendlyName = name,
            Description = name,
            DeviceClass = cls,
            ParentInstanceId = parent ?? DeviceDiagnosticUnknown.Value,
            IsPresent = present,
            ProblemCode = problem,
            Children = children ?? [],
        };

    /// <summary>由扁平清單建出與 <see cref="DeviceDiagnosticService.BuildReport"/> 相同形狀的樹。</summary>
    private static DeviceDiagnosticReport Report(params DeviceDiagnosticDevice[] flat)
    {
        var byId = flat.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
        var childOf = new Dictionary<string, List<DeviceDiagnosticDevice>>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<DeviceDiagnosticDevice>();

        foreach (var d in flat)
        {
            if (d.ParentInstanceId != DeviceDiagnosticUnknown.Value
                && byId.ContainsKey(d.ParentInstanceId))
            {
                if (!childOf.TryGetValue(d.ParentInstanceId, out var list))
                    childOf[d.ParentInstanceId] = list = [];
                list.Add(d);
            }
            else roots.Add(d);
        }

        DeviceDiagnosticDevice Freeze(DeviceDiagnosticDevice d, HashSet<string> path)
        {
            if (!path.Add(d.InstanceId)) return d with { Children = [] };
            var kids = childOf.TryGetValue(d.InstanceId, out var list)
                ? list.Select(c => Freeze(c, path)).ToArray()
                : [];
            path.Remove(d.InstanceId);
            return d with { Children = kids };
        }

        var tree = roots.Select(r => Freeze(r, new HashSet<string>(StringComparer.OrdinalIgnoreCase))).ToList();
        return new DeviceDiagnosticReport(flat, tree);
    }

    private static HardwareFact Summary(IReadOnlyList<HardwareFact> facts)
        => Assert.Single(facts, f => f.Key == DeviceTreeFactsService.SummaryKey);

    private static IEnumerable<HardwareFact> Roots(IReadOnlyList<HardwareFact> facts)
        => facts.Where(f => f.Key.StartsWith(DeviceTreeFactsService.RootPrefix, StringComparison.Ordinal));

    [Fact]
    public void 列舉回零台要標不適用_不是沒有裝置樹()
    {
        var facts = DeviceTreeFactsService.Collect(new DeviceDiagnosticReport([], []), At);

        var summary = Summary(facts);
        Assert.Equal(FactAvailability.NotApplicable, summary.Availability);
        Assert.Null(summary.NumericValue);
        Assert.Contains("不是「這台機器沒有裝置樹」", summary.UnavailableReason);
        Assert.Empty(Roots(facts));
    }

    [Fact]
    public void 三層樹要數出子樹規模與最深層數()
    {
        // 根 → 橋 → 裝置，最深三層。
        var report = Report(
            Dev("PCI\\ROOT", "系統", "根匯流排"),
            Dev("PCI\\BRIDGE", "系統", "PCI 橋", parent: "PCI\\ROOT"),
            Dev("USB\\DEV", "USB", "滑鼠", parent: "PCI\\BRIDGE"));

        var facts = DeviceTreeFactsService.Collect(report, At);
        var root = Assert.Single(Roots(facts));

        Assert.Equal("os.devtree.root.0001", root.Key);
        Assert.Equal(3, root.NumericValue);          // 子樹 3 台
        Assert.Contains("子樹 3 台", root.Value);
        Assert.Contains("最深 3 層", root.Value);
        Assert.Contains("直接子節點 1 個", root.Value);
        Assert.Contains("PCI 橋", root.Value);

        Assert.Contains("最深 3 層", Summary(facts).Value);
        Assert.Contains("第 1 層 1 台、第 2 層 1 台、第 3 層 1 台", Summary(facts).Value);
    }

    [Fact]
    public void 兩種根要分開計數()
    {
        // ①真的沒有父（ParentInstanceId＝Unknown）；②有父，但父不在清單裡。
        var report = Report(
            Dev("ROOT\\A", name: "真頂層"),
            Dev("ORPHAN\\B", name: "孤兒", parent: "NOT\\IN\\LIST"));

        var facts = DeviceTreeFactsService.Collect(report, At);
        Assert.Equal(2, Roots(facts).Count());

        // 根的鍵序按 instance ID 字典序（"ORPHAN\B" < "ROOT\A"），所以**按內容找而不是按編號猜**。
        var trueTop = Roots(facts).Single(f => f.Value.Contains("真頂層", StringComparison.Ordinal));
        Assert.Contains("Windows 回報它沒有父節點（真正的頂層）", trueTop.Value);

        var orphan = Roots(facts).Single(f => f.Value.Contains("孤兒", StringComparison.Ordinal));
        Assert.Contains("它有父節點，但那個父不在這份清單裡", orphan.Value);
        Assert.Contains("樹在這裡斷了，不是「它是頂層」", orphan.Value);

        Assert.Contains("父節點不在這份清單的根 1 個", Summary(facts).Value);
    }

    [Fact]
    public void 不在場的節點要算進子樹但另外講()
    {
        var report = Report(
            Dev("ROOT\\A"),
            Dev("GHOST\\B", present: false, parent: "ROOT\\A"));

        var root = Assert.Single(Roots(DeviceTreeFactsService.Collect(report, At)));
        Assert.Equal(2, root.NumericValue);
        Assert.Contains("在場 1 台、不在場 1 台", root.Value);
    }

    [Fact]
    public void 子樹內的問題碼要計數()
    {
        var report = Report(
            Dev("ROOT\\A"),
            Dev("DEV\\B", problem: 28, parent: "ROOT\\A"),
            Dev("DEV\\C", problem: 10, parent: "ROOT\\A"));

        var root = Assert.Single(Roots(DeviceTreeFactsService.Collect(report, At)));
        Assert.Contains("子樹內有問題碼 2 台", root.Value);
    }

    [Fact]
    public void 值裡不得出現instance_ID()
    {
        // instance ID 常含序號——與 os.dev.* 同一條界線。
        var report = Report(
            Dev("USB\\VID_1234&PID_5678\\SERIAL123456", "USB", "滑鼠"));

        var root = Assert.Single(Roots(DeviceTreeFactsService.Collect(report, At)));
        Assert.DoesNotContain("SERIAL123456", root.Value);
        Assert.DoesNotContain("VID_1234", root.Value);
        Assert.Contains("USB「滑鼠」", root.Value);     // 類別＋名稱仍可辨識

        Assert.DoesNotContain("SERIAL123456", Summary(DeviceTreeFactsService.Collect(report, At)).Value);
    }

    [Fact]
    public void 直接子節點太多時要說明還有幾個沒列出()
    {
        var children = Enumerable.Range(0, 12)
            .Select(i => Dev($"C\\{i:D2}", "USB", $"子{i}"))
            .ToArray();
        var flat = new List<DeviceDiagnosticDevice> { Dev("ROOT\\A") };
        flat.AddRange(children.Select(c => c with { ParentInstanceId = "ROOT\\A" }));

        var root = Assert.Single(Roots(DeviceTreeFactsService.Collect(Report(flat.ToArray()), At)));
        Assert.Contains($"直接子節點 12 個", root.Value);
        Assert.Contains($"另有 {12 - DeviceTreeFactsService.MaxChildrenListed} 個未列出", root.Value);
    }

    [Fact]
    public void 根太多時要說明截斷_不靜默()
    {
        var flat = Enumerable.Range(0, 5).Select(i => Dev($"ROOT\\{i}")).ToArray();
        var facts = DeviceTreeFactsService.Collect(Report(flat), At, maxRoots: 2);

        Assert.Equal(2, Roots(facts).Count());
        Assert.Contains("根清單只列前 2 個，另有 3 個未列出", Summary(facts).Value);
        Assert.Contains("根 5 個", Summary(facts).Value);
    }

    [Fact]
    public void 沒有名稱的節點要寫沒有名稱_不是空白()
    {
        var report = Report(Dev("ROOT\\A", "USB", name: DeviceDiagnosticUnknown.Value));
        var root = Assert.Single(Roots(DeviceTreeFactsService.Collect(report, At)));
        Assert.Contains("（沒有名稱）", root.Value);
    }

    [Fact]
    public void 成環的節點不得無限遞迴且要如實申報()
    {
        // A 的父是 B、B 的父是 A：兩者都不會成為某個根的後代（BuildReport 會各自成根，
        // 但這裡手動造一份「兩個都不在 Roots 裡」的報告，模擬真的走不到的情形）。
        var a = Dev("A", parent: "B");
        var b = Dev("B", parent: "A");
        var report = new DeviceDiagnosticReport([a, b], []);

        var facts = DeviceTreeFactsService.Collect(report, At);
        var summary = Summary(facts);

        Assert.Equal(FactAvailability.Present, summary.Availability);
        Assert.Contains("成環而未連到任何根的節點 2 台", summary.Value);
        Assert.Empty(Roots(facts));
    }

    [Fact]
    public void 沒有成環時要明說每個節點都連得到某個根()
    {
        var report = Report(Dev("ROOT\\A"), Dev("DEV\\B", parent: "ROOT\\A"));
        Assert.Contains("沒有成環的節點", Summary(DeviceTreeFactsService.Collect(report, At)).Value);
    }

    [Fact]
    public void 總覽的根數要等於逐根事實的個數()
    {
        var report = Report(
            Dev("ROOT\\A"), Dev("DEV\\B", parent: "ROOT\\A"),
            Dev("ROOT\\C"), Dev("ROOT\\D"));

        var facts = DeviceTreeFactsService.Collect(report, At);
        Assert.Contains($"根 {Roots(facts).Count()} 個", Summary(facts).Value);
        Assert.Equal(4, Summary(facts).NumericValue);   // 節點總數
    }

    [Fact]
    public void 逐根事實要帶可信度與來源_且Present不帶理由()
    {
        var root = Assert.Single(Roots(DeviceTreeFactsService.Collect(Report(Dev("ROOT\\A")), At)));

        Assert.Equal(FactTrustLevel.Reported, root.Trust);
        Assert.Equal(FactAvailability.Present, root.Availability);
        Assert.Null(root.UnavailableReason);
        Assert.Contains("SetupAPI", root.Source);
    }

    [Fact]
    public void 家族前綴必須在動態家族登記裡()
    {
        Assert.True(FactKeyDynamicCatalog.Families.ContainsKey(DeviceTreeFactsService.RootPrefix),
            $"動態家族沒有登記 {DeviceTreeFactsService.RootPrefix}");
        Assert.Contains(DeviceTreeFactsService.SummaryKey, FactKeyCatalog.Keys);
    }
}
