using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>
/// CPU 世代判定的純解碼器（V7 WP3 的前提基建）：MCHBAR 等暫存器佈局世代相依，
/// 任何「按世代解碼」都需要先知道本機是哪個微架構。
/// 對照表只收錄把握度高的 Family 6 model（依 Intel SDM CPUID 章節與 coreboot 的
/// model_*.h 命名慣例）；未收錄的 model 如實回 null——錯誤的世代標示比沒有更糟。
/// </summary>
public static class CpuGeneration
{
    /// <summary>解 CPUID leaf 1 EAX：family（extended 進位）、model（extended×16＋低 4 位）、stepping。</summary>
    public static (byte Family, byte Model, byte Stepping) DecodeSignature(uint eax)
        => ((byte)((eax >> 8) & 0xF),
            (byte)((((eax >> 16) & 0xF) << 4) | ((eax >> 4) & 0xF)),
            (byte)(eax & 0xF));

    /// <summary>Family 6 model → 微架構／世代名。只收錄有把握的子集；null＝未收錄，不猜。</summary>
    public static string? GenerationName(byte family, byte model) => (family, model) switch
    {
        (6, 0x4F) => "Skylake（伺服器 SP）",
        (6, 0x55) => "Skylake-X / Cascade Lake（HEDT／伺服器）",
        (6, 0x5E) => "Skylake（用戶端）",
        (6, 0x8E) => "Kaby Lake / Coffee Lake / Whiskey Lake / Amber Lake（行動與低功耗）",
        (6, 0x9E) => "Kaby Lake-X / Coffee Lake（用戶端）",
        (6, 0xA5) => "Comet Lake（用戶端）",
        (6, 0xA6) => "Comet Lake（行動）",
        (6, 0x97) => "Rocket Lake（用戶端）",
        (6, 0x9A) => "Alder Lake（用戶端）",
        (6, 0xB7) => "Raptor Lake（用戶端）",
        (6, 0xBA) => "Raptor Lake-P（行動）",
        (6, 0xAA) => "Meteor Lake（用戶端）",
        _ => null,
    };

    /// <summary>生產讀取：CPUID leaf 1 的 EAX。不支援 x86 指令集時回 null（如實三態）。</summary>
    public static uint? ReadSignature()
        => X86Base.IsSupported ? (uint)X86Base.CpuId(1, 0).Eax : null;
}
