namespace XinSpect;

public static class DeepBenchCrossDomainSynthesis
{
    public static IReadOnlyList<DeepBenchInsight> Summarize(DeepBenchRunRecord record) => Summarize(record.SessionId, record.Results);

    public static IReadOnlyList<DeepBenchInsight> Summarize(Guid sessionId, IReadOnlyList<DeepBenchTestResult> results)
    {
        List<DeepBenchInsight> insights = [];
        var sameSession = results.Where(result => result.SessionId == sessionId && result.FailureKind == DeepBenchFailureKind.None && result.Metrics.Count > 0).ToArray();

        if (sameSession.Length == 0)
        {
            return [new("沒有可聚合實測", "本場沒有成功測項，不生成總分或排名。", [])];
        }

        var domains = sameSession.Select(result => result.TestId.Split('.')[0]).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        string[] required = ["cpu", "topology", "memory", "gpu", "storage"];
        string[] missing = required.Where(domain => !domains.Contains(domain)).ToArray();
        if (missing.Length > 0)
        {
            insights.Add(new(
                "缺少量測域",
                $"缺少：{string.Join("、", missing.Select(NormalizeDomain))}。跨域歸因需要同場證據；缺域不補猜。",
                []));
        }

        if (domains.Contains("cpu") && domains.Contains("memory"))
        {
            insights.Add(new(
                "CPU 與記憶體對照",
                "CPU 微基準與記憶體頻寬/延遲來自同一 run session，可對照運算吞吐與資料供給邊界；不合成單一總分。",
                sameSession.Where(result => result.TestId.StartsWith("cpu.", StringComparison.Ordinal) || result.TestId.StartsWith("memory.", StringComparison.Ordinal)).Select(result => result.TestId).ToArray()));
        }

        DeepBenchTestResult? storage = sameSession.FirstOrDefault(result => result.TestId == "storage.qd-ladder");
        if (storage is not null)
        {
            var points = storage.Metrics.SelectMany(metric => metric.Points).ToArray();
            var qd1 = points.Where(point => point.Axes.TryGetValue("queueDepth", out string? value) && value == "1").ToArray();
            var best = points.OrderByDescending(point => point.Value).FirstOrDefault();
            if (best is not null && qd1.Length > 0)
            {
                double scaling = qd1.Average(point => point.Value) == 0 ? 0 : best.Value / qd1.Average(point => point.Value);
                insights.Add(new("儲存佇列擴展性", $"同場 QD1 到最佳點的實測比例為 {scaling:0.##}×；僅描述觀察值，不代表理論上限。", [storage.TestId]));
            }
        }

        return insights;
    }

    private static string NormalizeDomain(string domain) => domain switch
    {
        "cpu" => "CPU",
        "topology" => "拓撲",
        "memory" => "記憶體",
        "gpu" => "GPU",
        "storage" => "儲存",
        _ => domain
    };
}
