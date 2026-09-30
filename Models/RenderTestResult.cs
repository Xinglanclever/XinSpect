namespace XinSpect;

public sealed class RenderTestResult
{
    public string Name   { get; init; } = "";
    public double AverageFps { get; init; }
    public string FpsText   => AverageFps > 0 ? $"{AverageFps:0.0} FPS" : "—";
    public double Score      { get; init; }
    public string ScoreText  => Score > 0 ? $"{Score:0}" : "—";

    /// <summary>測了什麼、怎麼測的（過程展示）。</summary>
    public string Detail { get; init; } = "";
    /// <summary>末幀像素檢核：有效（非空白）像素占比 0–100；空白幀的 FPS 不能當成績。</summary>
    public double ValidPixelPercent { get; init; } = 100;
    /// <summary>逐秒 FPS 抽樣（過程展示用）。</summary>
    public string FpsTrace { get; init; } = "";
    /// <summary>true＝像素檢核未通過，分數不可信。</summary>
    public bool IsBlank => ValidPixelPercent < 1.0;
    public bool HasTrace => FpsTrace.Length > 0;
}
