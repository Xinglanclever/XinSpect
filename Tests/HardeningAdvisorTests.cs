using Xunit;

namespace XinSpect.Tests;

public class HardeningAdvisorTests
{
    [Fact]
    public void 全部Good_沒有建議()
    {
        var categories = new List<SecurityCategoryScore>
        {
            MakeCategory("test", "測試", 100, [
                new SecurityFinding("a", "測試", "OK", "好", SecuritySeverity.Good, null),
            ]),
        };
        var recs = HardeningAdvisor.Recommend(categories);
        Assert.Empty(recs);
    }

    [Fact]
    public void 有Warning_產生建議()
    {
        var categories = new List<SecurityCategoryScore>
        {
            MakeCategory("test", "測試", 60, [
                new SecurityFinding("fw.secboot", "測試", "問題", "壞", SecuritySeverity.Warning, "請修復"),
            ]),
        };
        var recs = HardeningAdvisor.Recommend(categories);
        Assert.Single(recs);
        Assert.Equal("問題", recs[0].Title);
        Assert.Equal(SecuritySeverity.Warning, recs[0].Priority);
    }

    [Fact]
    public void 無Recommendation的發現不產生建議()
    {
        var categories = new List<SecurityCategoryScore>
        {
            MakeCategory("test", "測試", 70, [
                new SecurityFinding("x", "測試", "讀不到", "未知", SecuritySeverity.Advisory, null),
            ]),
        };
        var recs = HardeningAdvisor.Recommend(categories);
        Assert.Empty(recs);
    }

    [Fact]
    public void Critical排在Warning前面()
    {
        var categories = new List<SecurityCategoryScore>
        {
            MakeCategory("test", "測試", 30, [
                new SecurityFinding("a", "測試", "警告", "中", SecuritySeverity.Warning, "做法A"),
                new SecurityFinding("b", "測試", "危急", "嚴重", SecuritySeverity.Critical, "做法B"),
            ]),
        };
        var recs = HardeningAdvisor.Recommend(categories);
        Assert.Equal(2, recs.Count);
        Assert.Equal(SecuritySeverity.Critical, recs[0].Priority);
        Assert.Equal(SecuritySeverity.Warning, recs[1].Priority);
    }

    [Fact]
    public void 已知Id有對應的難度和影響()
    {
        var categories = new List<SecurityCategoryScore>
        {
            MakeCategory("test", "測試", 50, [
                new SecurityFinding("dma.hvci", "測試", "HVCI", "關", SecuritySeverity.Critical, "開它"),
            ]),
        };
        var recs = HardeningAdvisor.Recommend(categories);
        Assert.Single(recs);
        Assert.Equal("中等", recs[0].Difficulty);
        Assert.Contains("核心代碼注入", recs[0].Impact);
    }

    private static SecurityCategoryScore MakeCategory(
        string id, string name, int score, IReadOnlyList<SecurityFinding> findings)
        => new(id, name, score, 100, score >= 90 ? SecuritySeverity.Good : SecuritySeverity.Warning, findings);
}
