namespace XinSpect;

public sealed class RenderTestResult
{
    public string Name   { get; init; } = "";
    public double AverageFps { get; init; }
    public string FpsText   => AverageFps > 0 ? $"{AverageFps:0.0} FPS" : "—";
    public double Score      { get; init; }
    public string ScoreText  => Score > 0 ? $"{Score:0}" : "—";
}
