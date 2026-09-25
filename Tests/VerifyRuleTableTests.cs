using Xunit;

namespace XinSpect.Tests;

/// <summary>規則表的整表不變式（表的形狀正確，而非某一條規則判得對不對）。</summary>
/// <remarks>
/// 這一組管的是那些「寫錯了不會有人發現」的事：新增 FactId 忘了登記到 FactCatalog（缺值訊息
/// 會變成英文列舉名）、規則宣告了不存在的依賴、極端值讓某條規則除以零或炸掉。
/// </remarks>
public class VerifyRuleTableTests
{
    private static readonly VerifyScope[] Scopes = [VerifyScope.Machine, VerifyScope.Disk];

    [Fact]
    public void 規則編號不得重複()
    {
        var ids = VerifyRules.All.Select(r => r.Id).ToList();
        Assert.Empty(ids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void 規則編號要照格式_部件與標題不得留空()
        => Assert.All(VerifyRules.All, r =>
        {
            Assert.Matches(@"^R-[A-Z]{3}-\d{2}$", r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.Part), $"{r.Id} 沒有部件名");
            Assert.False(string.IsNullOrWhiteSpace(r.Title), $"{r.Id} 沒有標題");
        });

    [Fact]
    public void 每條規則都要宣告依賴_且依賴都得登記在事實目錄裡()
        => Assert.All(VerifyRules.All, r =>
        {
            Assert.NotEmpty(r.RequiredFacts);
            Assert.All(r.RequiredFacts, id =>
                Assert.True(FactCatalog.Covers(id), $"{r.Id} 依賴的 {id} 沒有登記在 FactCatalog"));
        });

    [Fact]
    public void 每個FactId都要登記在事實目錄裡()
        => Assert.All(Enum.GetValues<FactId>(), id =>
            Assert.True(FactCatalog.Covers(id),
                $"{id} 沒有登記在 FactCatalog——缺這個事實時，訊息會印出英文列舉名"));

    [Fact]
    public void 沒有任何事實時_每條規則都判無法判定_而不是丟例外()
        => Assert.All(Scopes, scope =>
            Assert.All(VerifyEngine.Run(new VerifyFacts([]), scope), f =>
            {
                Assert.Equal(VerifyVerdict.Unread, f.Verdict);
                Assert.Equal(Severity.Neutral, f.Severity);
                Assert.False(string.IsNullOrWhiteSpace(f.Explanation));
                Assert.Empty(f.Evidence);
            }));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1e15)]
    [InlineData(-1)]
    public void 極端數值不得讓任何規則丟例外(double value)
        => Assert.All(VerifyRules.All, r =>
        {
            var facts = new VerifyFacts(r.RequiredFacts.Select(id => new VerifyFact(
                id, FactCatalog.Name(id), value.ToString("0.##"), value, "", FactSource.Derived,
                "測試合成", FactCatalog.NeedsAdmin(id), FactTrust.Derived, DateTime.UnixEpoch)));
            var finding = VerifyEngine.Run(facts, r.Scope).Single(x => x.Id == r.Id);
            Assert.False(string.IsNullOrWhiteSpace(finding.Explanation), $"{r.Id} 沒有給說明");
        });

    [Fact]
    public void 矛盾者必須附上證據()
        => Assert.All(Scopes, scope =>
        {
            var facts = new VerifyFacts(Enum.GetValues<FactId>().Select(id => new VerifyFact(
                id, FactCatalog.Name(id), "1", 1, "", FactSource.Derived,
                "測試合成", FactCatalog.NeedsAdmin(id), FactTrust.Derived, DateTime.UnixEpoch)));
            Assert.All(VerifyEngine.Run(facts, scope).Where(x => x.Verdict == VerifyVerdict.Conflict),
                x => Assert.NotEmpty(x.Evidence));
        });

    [Fact]
    public void 兩個作用範圍不得互相污染_合計等於規則總數()
    {
        var machine = VerifyEngine.Run(new VerifyFacts([]), VerifyScope.Machine);
        var disk = VerifyEngine.Run(new VerifyFacts([]), VerifyScope.Disk);
        Assert.NotEmpty(machine);
        Assert.NotEmpty(disk);
        Assert.Equal(VerifyRules.All.Length, machine.Count + disk.Count);
        Assert.Empty(machine.Select(x => x.Id).Intersect(disk.Select(x => x.Id)));
    }
}
