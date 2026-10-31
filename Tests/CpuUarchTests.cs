using System;
using System.Collections.Generic;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// CPUID family／model → 微架構對照，以及 <c>cpu.uarch</c> 這把事實的兩個來源。
///
/// <para>
/// 這裡有<b>兩張表</b>，職責不同，測試也要分開看：
/// </para>
/// <list type="bullet">
/// <item><see cref="CpuUarchTable"/>：pytorch/cpuinfo 的 <c>src/x86/uarch.c</c> 逐格搬過來，
/// 覆蓋面最廣（AMD 各世代、Hygon、Intel 舊世代與伺服器部件）。搬這種表最容易錯的不是
/// 「值抄錯」而是「查法抄錯」：AMD 的 0x17／0x19／0x1A 是拿 <b>extended model（model&gt;&gt;4）</b>
/// 分段，不是拿 model 直接查；0x16 用門檻切 jaguar／puma；0x15 先查 model、查不到才退回
/// extended model。這幾條只要錯一條，整台機器會被標成隔壁那一代的核心——而且是悄悄錯。</item>
/// <item><see cref="MicroarchProfile"/>：本專案既有的 Intel 表（TMA 分母用），含大小核分流。
/// <c>cpu.uarch</c> 對 Intel 先問它，因此桌機的 0x97／0xB7／0xA7 不會因為 cpuinfo 沒收錄而變成
/// 「未收錄」。</item>
/// </list>
/// </summary>
public sealed class CpuUarchTests
{
    /// <summary>把 family／model／stepping 反向組成 CPUID leaf 1 EAX（family &gt; 0xF 時拆成 base＋extended）。</summary>
    private static uint Sig(int family, int model, int stepping)
    {
        int baseFamily = family > 0xF ? 0xF : family;
        int extFamily = family > 0xF ? family - 0xF : 0;
        return (uint)((stepping & 0xF)
                      | ((model & 0xF) << 4)
                      | (baseFamily << 8)
                      | (((model >> 4) & 0xF) << 16)
                      | ((extFamily & 0xFF) << 20));
    }

    [Fact]
    public void Intel_依_model_對照到核心()
    {
        // 先釘住「上游真的有這幾格」的那些：Emerald Rapids 的 0xCF → Raptor Cove、
        // Sapphire Rapids 的 0x8F → Golden Cove、Granite Rapids 的 0xAD → Redwood Cove。
        // 特意不用 0xB7 當例子：那顆（Raptor Lake-S）cpuinfo 裡沒有 case，拿它來斷言等於
        // 在測一張不存在的表——真正該負責它的是 MicroarchProfile，見下面的桌機那條。
        Assert.Equal("raptor_cove", CpuUarchTable.Lookup("GenuineIntel", 6, 0xCF));
        Assert.Equal("Raptor Cove", CpuUarchTable.DisplayName("raptor_cove"));
        Assert.Equal("golden_cove", CpuUarchTable.Lookup("GenuineIntel", 6, 0x8F));
        Assert.Equal("redwood_cove", CpuUarchTable.Lookup("GenuineIntel", 6, 0xAD));

        // Atom 系：Alder Lake-N 的 0xBE → Gracemont、Sierra Forest 的 0xAF → Crestmont。
        Assert.Equal("gracemont", CpuUarchTable.Lookup("GenuineIntel", 6, 0xBE));
        Assert.Equal("crestmont", CpuUarchTable.Lookup("GenuineIntel", 6, 0xAF));
        Assert.Equal("Gracemont", CpuUarchTable.DisplayName("gracemont"));

        // 舊家族也要對：Pentium（family 5）、Pentium 4（family 0xF）、Nehalem、Haswell、Tiger Lake。
        Assert.Equal("p5", CpuUarchTable.Lookup("GenuineIntel", 5, 0x04));
        Assert.Equal("prescott", CpuUarchTable.Lookup("GenuineIntel", 0xF, 0x04));
        Assert.Equal("nehalem", CpuUarchTable.Lookup("GenuineIntel", 6, 0x1A));
        Assert.Equal("haswell", CpuUarchTable.Lookup("GenuineIntel", 6, 0x3C));
        Assert.Equal("willow_cove", CpuUarchTable.Lookup("GenuineIntel", 6, 0x8C));
    }

    [Fact]
    public void AMD_用_extended_model_分段而且_0x15_有退回規則()
    {
        // 0x19：extended model 0x0–0x5 → Zen 3、0x1/0x6/0x7/0xA → Zen 4。
        // 0x11（extended 1）是 Raphael／Phoenix 那一線；若誤用 model 直接查，會落進 Zen 3。
        Assert.Equal("zen3", CpuUarchTable.Lookup("AuthenticAMD", 0x19, 0x21));
        Assert.Equal("zen4", CpuUarchTable.Lookup("AuthenticAMD", 0x19, 0x11));
        Assert.Equal("zen4", CpuUarchTable.Lookup("AuthenticAMD", 0x19, 0x74));

        // 0x1A：Zen 5／Zen 6 的分界也只看 extended model。
        Assert.Equal("zen5", CpuUarchTable.Lookup("AuthenticAMD", 0x1A, 0x02));
        Assert.Equal("zen6", CpuUarchTable.Lookup("AuthenticAMD", 0x1A, 0x50));

        // 0x17：0x0/0x1 → Zen、0x3/0x6/0x7/0x9/0xA → Zen 2；Oberon（PS5）與 Arden（Xbox）是獨立代號。
        Assert.Equal("zen", CpuUarchTable.Lookup("AuthenticAMD", 0x17, 0x01));
        Assert.Equal("zen2", CpuUarchTable.Lookup("AuthenticAMD", 0x17, 0x31));
        Assert.Equal("oberon", CpuUarchTable.Lookup("AuthenticAMD", 0x17, 0x47));
        Assert.Equal("arden", CpuUarchTable.Lookup("AuthenticAMD", 0x17, 0x84));

        // 0x15：model 查不到才退回 extended model；0x16 是門檻切換（jaguar/puma）。
        Assert.Equal("bulldozer", CpuUarchTable.Lookup("AuthenticAMD", 0x15, 0x01));
        Assert.Equal("piledriver", CpuUarchTable.Lookup("AuthenticAMD", 0x15, 0x02));
        Assert.Equal("piledriver", CpuUarchTable.Lookup("AuthenticAMD", 0x15, 0x12)); // 未收錄 model → extended 1
        Assert.Equal("jaguar", CpuUarchTable.Lookup("AuthenticAMD", 0x16, 0x00));
        Assert.Equal("puma", CpuUarchTable.Lookup("AuthenticAMD", 0x16, 0x30));
    }

    [Fact]
    public void 表裡每個核心都有顯示名稱_而且不含底線()
    {
        // 掃過所有實際可能的 family／model：Lookup 回傳的每一個代號都必須在 DisplayName 裡，
        // 否則畫面上會直接出現 raptor_cove 這種程式內部字串。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string vendor in new[] { "GenuineIntel", "AuthenticAMD", "HygonGenuine" })
        {
            for (int family = 0; family <= 0xFF; family++)
            {
                for (int model = 0; model <= 0xFF; model++)
                {
                    if (CpuUarchTable.Lookup(vendor, family, model) is { } id) seen.Add(id);
                }
            }
        }

        Assert.True(seen.Count >= 30, $"只掃到 {seen.Count} 個核心代號——對照表縮水或查法壞了");
        foreach (string id in seen)
        {
            string? name = CpuUarchTable.DisplayName(id);
            Assert.False(string.IsNullOrWhiteSpace(name), $"核心代號 {id} 沒有顯示名稱");
            Assert.DoesNotContain("_", name);
        }
    }

    [Fact]
    public void 未收錄與未知廠商都回_null_不猜也不拋例外()
    {
        Assert.Null(CpuUarchTable.Lookup("GenuineIntel", 6, 0xFF));      // 未收錄的 model
        Assert.Null(CpuUarchTable.Lookup("GenuineIntel", 0x0E, 0x00));   // 不存在的 family
        Assert.Null(CpuUarchTable.Lookup("QuadCoreAMD", 6, 0x9E));       // 認不得的廠商字串
        Assert.Null(CpuUarchTable.Lookup("", 6, 0x9E));
        Assert.Null(CpuUarchTable.Lookup(null, 6, 0x9E));
        Assert.Null(CpuUarchTable.DisplayName(null));
        Assert.Null(CpuUarchTable.DisplayName("not_a_real_core"));

        // 任意輸入都不該拋：這一支會被掃描頁面用「可能拿不到 CPUID」的值呼叫。
        foreach (int family in new[] { -1, 0, 5, 6, 0x1A, 0xFFFF })
        {
            foreach (int model in new[] { -1, 0, 0xB7, 0xFF, 0x1234 })
            {
                _ = CpuUarchTable.Lookup("AuthenticAMD", family, model);
                _ = CpuUarchTable.Lookup("GenuineIntel", family, model);
            }
        }
    }

    [Fact]
    public void 廠商字串由三個暫存器拼回來()
    {
        // CPUID leaf 0 的 12 bytes 是 EBX、EDX、ECX 依序排列——不是 EBX、ECX、EDX。
        // 拼錯的症狀是廠商字串變成 "Genuineletni"，於是整台機器查不到核心。
        static uint Pack(string four) =>
            (uint)(four[0] | (four[1] << 8) | (four[2] << 16) | (four[3] << 24));

        Assert.Equal("GenuineIntel", CpuUarchTable.VendorName(Pack("Genu"), Pack("ineI"), Pack("ntel")));
        Assert.Equal("AuthenticAMD", CpuUarchTable.VendorName(Pack("Auth"), Pack("enti"), Pack("cAMD")));
    }

    [Fact]
    public void 桌面處理器不再因為_cpuinfo_沒收錄而顯示未收錄()
    {
        // 這幾格是 cpuinfo 的空白處，也正是本專案使用者最可能踩到的型號：
        // 0x97＝Alder Lake-S（i5-12600K，簽章 6／151／2）、0xB7＝Raptor Lake-S（i5-13600K，6／183／2）、
        // 0xA7＝Rocket Lake（i7-11700K，6／167／1）。任何一格掉回 Unknown，掃描頁就會對一台
        // 現役桌機說「對照表未收錄」。
        var alderLake = CpuFirmwareFactsService.UarchFact(Sig(6, 0x97, 2), "GenuineIntel", DateTimeOffset.UnixEpoch);
        Assert.Equal(FactAvailability.Present, alderLake.Availability);
        Assert.Contains("Golden Cove", alderLake.Value);
        Assert.Contains("Gracemont", alderLake.Value);       // 大小核：只報大核等於只講一半

        var raptorLake = CpuFirmwareFactsService.UarchFact(Sig(6, 0xB7, 2), "GenuineIntel", DateTimeOffset.UnixEpoch);
        Assert.Equal(FactAvailability.Present, raptorLake.Availability);
        Assert.Contains("Raptor Cove", raptorLake.Value);

        var rocketLake = CpuFirmwareFactsService.UarchFact(Sig(6, 0xA7, 1), "GenuineIntel", DateTimeOffset.UnixEpoch);
        Assert.Equal(FactAvailability.Present, rocketLake.Availability);
        Assert.Contains("Cypress Cove", rocketLake.Value);

        // 核型取自 CPUID 0x1A；給 E 核時同一顆 model 要回小核的名字。
        var ecore = CpuFirmwareFactsService.UarchFact(Sig(6, 0xB7, 2), "GenuineIntel", DateTimeOffset.UnixEpoch,
            () => CoreKind.Efficiency);
        Assert.Equal(FactAvailability.Present, ecore.Availability);
        Assert.Contains("Gracemont", ecore.Value);
    }

    [Fact]
    public void AMD_與舊世代走_cpuinfo_表_值裡同時給顯示名與代號()
    {
        // Zen 4（Raphael，0x19 extended 1）：MicroarchProfile 只認 Intel，這裡必須由 cpuinfo 表接住。
        var zen4 = CpuFirmwareFactsService.UarchFact(Sig(0x19, 0x11, 1), "AuthenticAMD", DateTimeOffset.UnixEpoch);
        Assert.Equal(FactAvailability.Present, zen4.Availability);
        Assert.Contains("Zen 4", zen4.Value);
        Assert.Contains("zen4", zen4.Value);

        // Intel 但 MicroarchProfile 沒有的舊型號（Northwood，family 0xF model 0x02）也要接住。
        var northwood = CpuFirmwareFactsService.UarchFact(Sig(0xF, 0x02, 0), "GenuineIntel", DateTimeOffset.UnixEpoch);
        Assert.Equal(FactAvailability.Present, northwood.Availability);
        Assert.Contains("Willamette", northwood.Value);
    }

    [Fact]
    public void 讀不到廠商字串與對照表未收錄都是如實三態而不是猜()
    {
        // 沒有廠商字串 → Unknown（不是拿 Intel 表硬套）。
        var noVendor = CpuFirmwareFactsService.UarchFact(Sig(6, 0x9E, 0xA), null, DateTimeOffset.UnixEpoch);
        Assert.Equal(FactAvailability.Unknown, noVendor.Availability);
        Assert.Equal("", noVendor.Value);

        // 有廠商但兩張表都沒有 → Unknown，附原始簽章讓人自己查，不挑最像的填。
        var unknown = CpuFirmwareFactsService.UarchFact(Sig(6, 0xEF, 0), "GenuineIntel", DateTimeOffset.UnixEpoch);
        Assert.Equal(FactAvailability.Unknown, unknown.Availability);
        Assert.Contains("0xEF", unknown.UnavailableReason);

        // 非 x86（讀不到 CPUID）→ NotSupported。
        var none = CpuFirmwareFactsService.UarchFact(null, "GenuineIntel", DateTimeOffset.UnixEpoch);
        Assert.Equal(FactAvailability.NotSupported, none.Availability);
        Assert.Null(none.NumericValue);
    }

    [Fact]
    public void 世代表與微架構表對同一顆處理器不打架()
    {
        // 這兩張表先前對 0x97 給過不同答案（世代表寫 Rocket Lake、微架構表寫 Alder Lake 那條線），
        // 錯的是世代表。這裡把它們釘在一起：產品名必須一致，否則兩張表又會各自漂走。
        foreach (var (model, product) in new[] { (0x97, "Alder Lake"), (0xA7, "Rocket Lake"), (0xB7, "Raptor Lake") })
        {
            string? generation = CpuGeneration.GenerationName(6, (byte)model);
            Assert.NotNull(generation);
            Assert.Contains(product, generation);

            var profile = MicroarchProfile.Identify(6, model);
            Assert.True(profile.IsKnown, $"MicroarchProfile 不認得 model 0x{model:X2}");
            Assert.StartsWith(product, profile.Product);
        }
    }
}
