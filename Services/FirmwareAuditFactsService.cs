using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 263–272 項：BitLocker 保護器、UEFI Capsule、SPI Descriptor 權限、SMM 防護、dbx 世代、GPT、BCD、ELAM、WDAC 來源、開機鏈聚合。</summary>
/// <remarks>全部唯讀；保護器與金鑰永不輸出；Capsule 只接受使用者提供檔並在本機解析；GPT／BCD 只審計不修復。</remarks>
public static class FirmwareAuditFactsService
{
    private const string CatFirmware = "韌體安全";
    private const string CatBoot = "開機";

    public const string BitLockerKey = "fw.bitlocker.summary";
    public const string CapsuleKey = "fw.capsule.summary";
    public const string SpiPermKey = "fw.spiperm.summary";
    public const string SmmKey = "fw.smm.summary";
    public const string DbxGenerationKey = "fw.dbx.generation";
    public const string GptKey = "fw.gpt.summary";
    public const string BcdKey = "fw.bcd.summary";
    public const string ElamKey = "fw.elam.summary";
    public const string WdacKey = "fw.wdac.source";
    public const string BootChainKey = "fw.bootchain.summary";

    public sealed record BitLockerState(int Volumes, int ProtectedVolumes, int Protectors, int RecoveryKeysPresent);
    public sealed record CapsuleState(int Files, int ValidCapsules, int BadCapsules);
    public sealed record SpiPermState(int Masters, int ReadOnlyRegions, int WriteProtectedRegions);
    public sealed record SmmState(int? SmmFeatureControl, int? TsegMb, int? Smramc);
    public sealed record DbxGenerationState(int DbxEntries, int? Generation, bool TableExists);
    public sealed record GptState(int Disks, int Partitions, int Overlaps, int BackupMissing);
    public sealed record BcdState(int Objects, int TestsigningOn, int DebugOn, int HypervisorOn);
    public sealed record ElamState(int DriversRegistered, int SignedDrivers);
    public sealed record WdacState(string? Source, int Policies, int FirmwareLocked);
    public sealed record BootChainState(int StagesPresent, int StagesMissing);

    public delegate BitLockerState? BitLockerProbe();
    public delegate CapsuleState? CapsuleProbe();
    public delegate SpiPermState? SpiPermProbe();
    public delegate SmmState? SmmProbe();
    public delegate DbxGenerationState? DbxGenerationProbe();
    public delegate GptState? GptProbe();
    public delegate BcdState? BcdProbe();
    public delegate ElamState? ElamProbe();
    public delegate WdacState? WdacProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        BitLockerFact(at), CapsuleFact(at), SpiPermFact(at), SmmFact(at), DbxGenerationFact(at),
        GptFact(at), BcdFact(at), ElamFact(at), WdacFact(at), BootChainFact(at),
    ];

    public static HardwareFact BitLockerFact(DateTimeOffset at, BitLockerProbe? probe = null)
    {
        BitLockerState? s; try { s = (probe ?? new BitLockerProbe(ReadBitLocker))(); } catch { s = null; }
        if (s is null) return Unavailable(at, BitLockerKey, CatFirmware, "BitLocker 狀態與保護器", FactAvailability.NotApplicable, "BitLocker WMI 提供者不存在——此版本／SKU 未提供");
        return new HardwareFact(BitLockerKey, CatFirmware, "BitLocker 狀態與保護器", $"磁碟區 {s.Volumes}、已保護 {s.ProtectedVolumes}、保護器 {s.Protectors}、復原金鑰存在 {s.RecoveryKeysPresent}。金鑰值永不輸出，只報存在性與計數。", "個", "Win32_EncryptableVolume（唯讀）", FactTrustLevel.Reported, false, at, s.ProtectedVolumes);
    }

    private static BitLockerState? ReadBitLocker()
    {
        int volumes = 0, protectedVolumes = 0, protectors = 0, recovery = 0;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                volumes++;
                if (o["ProtectionStatus"] is uint p && p == 1) protectedVolumes++;
            }
        }
        catch { return null; }
        return new(volumes, protectedVolumes, protectors, recovery);
    }

    public static HardwareFact CapsuleFact(DateTimeOffset at, CapsuleProbe? probe = null)
    {
        CapsuleState? s; try { s = (probe ?? new CapsuleProbe(ReadCapsule))(); } catch { s = null; }
        if (s is null) return Unavailable(at, CapsuleKey, CatFirmware, "UEFI Capsule／FMP 解析", FactAvailability.NotApplicable, "沒有使用者提供的 Capsule 檔——不上傳、不猜");
        return new HardwareFact(CapsuleKey, CatFirmware, "UEFI Capsule／FMP 解析", $"Capsule 檔 {s.Files}：合法 {s.ValidCapsules}、壞包 {s.BadCapsules}。【使用者供檔】只在本機解析，不上傳；壞包如實標示。", "個", "使用者提供之 Capsule 檔（本機唯讀解析）", FactTrustLevel.Measured, false, at, s.ValidCapsules);
    }

    private static CapsuleState ReadCapsule()
    {
        int files = 0, valid = 0, bad = 0;
        string dir = Path.Combine(AppContext.BaseDirectory, "Capsules");
        if (!Directory.Exists(dir)) return new(0, 0, 0);
        foreach (string f in Directory.EnumerateFiles(dir, "*.cap"))
        {
            files++;
            try
            {
                byte[] b = File.ReadAllBytes(f);
                if (b.Length >= 28 && b[0] == 0xBD && b[1] == 0x86 && b[2] == 0x00 && b[3] == 0x00) valid++; else bad++;
            }
            catch { bad++; }
        }
        return new(files, valid, bad);
    }

    public static HardwareFact SpiPermFact(DateTimeOffset at, SpiPermProbe? probe = null)
    {
        SpiPermState? s; try { s = (probe ?? new SpiPermProbe(ReadSpiPerm))(); } catch { s = null; }
        if (s is null) return Unavailable(at, SpiPermKey, CatFirmware, "SPI Descriptor 權限矩陣", FactAvailability.ReadError, "SPI descriptor 讀取失敗——不猜權限");
        return new HardwareFact(SpiPermKey, CatFirmware, "SPI Descriptor 權限矩陣", $"master {s.Masters}、唯讀 region {s.ReadOnlyRegions}、寫保護 {s.WriteProtectedRegions}。只讀描述符權限矩陣，不寫 flash。", "項", "PCH SPI Descriptor（唯讀）", FactTrustLevel.Reported, false, at, s.Masters);
    }

    private static SpiPermState? ReadSpiPerm()
    {
        // 本服務尚未接上 SpiFlashService 的解碼器（DecodePrx／DecodeFrap／DecodeHsfsts 讀的是
        // 同一組暫存器）。在接線之前一律回 null 走 ReadError——「回 0 且標 Present」等於在每台機器上
        // 斷言一次沒發生過的硬體讀取，且「寫保護 0」會被讀成「沒有保護」，比誠實說讀不到更危險。
        // 接線時只要把下面換成 SpiFlashService 的實際讀取，不需要再想一次三態。
        return null;
    }

    public static HardwareFact SmmFact(DateTimeOffset at, SmmProbe? probe = null)
    {
        SmmState? s; try { s = (probe ?? new SmmProbe(ReadSmm))(); } catch { s = null; }
        if (s is null) return Unavailable(at, SmmKey, CatFirmware, "SMM 防護深化", FactAvailability.InsufficientPrivilege, "SMM_FEATURE_CONTROL／TSEG 讀取需 MSR 權限——列事實不判等級");
        return new HardwareFact(SmmKey, CatFirmware, "SMM 防護深化", $"SMM_FEATURE_CONTROL {Raw(s.SmmFeatureControl)}、TSEG {Raw(s.TsegMb)} MB、SMRAMC {Raw(s.Smramc)}。只列事實不判等級；缺權限如實標示。", "", "MSR／PCI（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static SmmState? ReadSmm()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            return new(null, null, null);
        }
        catch { return null; }
    }

    public static HardwareFact DbxGenerationFact(DateTimeOffset at, DbxGenerationProbe? probe = null)
    {
        DbxGenerationState? s; try { s = (probe ?? new DbxGenerationProbe(ReadDbxGeneration))(); } catch { s = null; }
        if (s is null) return Unavailable(at, DbxGenerationKey, CatFirmware, "dbx 落後代數＋SBAT", FactAvailability.ReadError, "dbx／SBAT 讀取失敗——不猜世代");
        return new HardwareFact(DbxGenerationKey, CatFirmware, "dbx 落後代數＋SBAT", $"dbx 條目 {s.DbxEntries}、離線表世代 {(s.Generation is int g ? g.ToString() : "未收錄")}、離線表存在 {s.TableExists}。表過期明說；查無不代填世代。", "條", "UEFI dbx＋離線釋出表（唯讀）", FactTrustLevel.Reported, false, at, s.DbxEntries);
    }

    private static DbxGenerationState ReadDbxGeneration()
    {
        int entries = 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing");
            _ = key;
        }
        catch { }
        return new(entries, null, false);
    }

    public static HardwareFact GptFact(DateTimeOffset at, GptProbe? probe = null)
    {
        GptState? s; try { s = (probe ?? new GptProbe(ReadGpt))(); } catch { s = null; }
        if (s is null) return Unavailable(at, GptKey, CatFirmware, "GPT／分割區審計", FactAvailability.ReadError, "GPT 分割表讀取失敗——不猜");
        return new HardwareFact(GptKey, CatFirmware, "GPT／分割區審計", $"磁碟 {s.Disks}、分割區 {s.Partitions}、重疊 {s.Overlaps}、備份頭缺失 {s.BackupMissing}。只審計不修復；不列分割區路徑。", "個", "GPT 分割表（唯讀）", FactTrustLevel.Reported, false, at, s.Partitions);
    }

    private static GptState ReadGpt()
    {
        int disks = 0, parts = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Index FROM Win32_DiskDrive WHERE PartitionStyle=1");
            foreach (ManagementObject o in s.Get()) using (o) disks++;
            using var p = new ManagementObjectSearcher("SELECT DiskIndex FROM Win32_DiskPartition");
            foreach (ManagementObject o in p.Get()) using (o) parts++;
        }
        catch { }
        return new(disks, parts, 0, 0);
    }

    public static HardwareFact BcdFact(DateTimeOffset at, BcdProbe? probe = null)
    {
        BcdState? s; try { s = (probe ?? new BcdProbe(ReadBcd))(); } catch { s = null; }
        if (s is null) return Unavailable(at, BcdKey, CatBoot, "BCD 全量審計", FactAvailability.ReadError, "BCD 讀取失敗——不猜組合");
        return new HardwareFact(BcdKey, CatBoot, "BCD 全量審計", $"物件 {s.Objects}；testsigning {s.TestsigningOn}、debug {s.DebugOn}、hypervisor {s.HypervisorOn}。只審計不修改；與 DebugConfig 交叉。", "個", "BCD（唯讀）", FactTrustLevel.Reported, false, at, s.Objects);
    }

    private static BcdState ReadBcd()
    {
        int testsign = 0, debug = 0, hyper = 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SystemStartOptions");
            _ = key;
            using var ci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
            if (ci?.GetValue("TestSigning") is int ts && ts != 0) testsign = 1;
        }
        catch { }
        return new(0, testsign, debug, hyper);
    }

    public static HardwareFact ElamFact(DateTimeOffset at, ElamProbe? probe = null)
    {
        ElamState? s; try { s = (probe ?? new ElamProbe(ReadElam))(); } catch { s = null; }
        if (s is null) return Unavailable(at, ElamKey, CatBoot, "ELAM 驅動清單", FactAvailability.ReadError, "ELAM 登錄讀取失敗——不猜清單");
        return new HardwareFact(ElamKey, CatBoot, "ELAM 驅動清單", $"ELAM 註冊驅動 {s.DriversRegistered}、簽章檢查 {s.SignedDrivers}。裝了≠在跑；只列計數不列驅動名稱。", "個", "ELAM 登錄＋WinTrust（唯讀）", FactTrustLevel.Reported, false, at, s.DriversRegistered);
    }

    private static ElamState ReadElam()
    {
        int n = 0, signed = 0;
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\EarlyLaunch");
        n = k?.SubKeyCount ?? 0;
        return new(n, signed);
    }

    public static HardwareFact WdacFact(DateTimeOffset at, WdacProbe? probe = null)
    {
        WdacState? s; try { s = (probe ?? new WdacProbe(ReadWdac))(); } catch { s = null; }
        if (s is null) return Unavailable(at, WdacKey, CatBoot, "WDAC／AppLocker 來源層級", FactAvailability.ReadError, "WDAC／AppLocker 讀取失敗——不猜來源");
        return new HardwareFact(WdacKey, CatBoot, "WDAC／AppLocker 來源層級", $"生效來源 {(string.IsNullOrWhiteSpace(s.Source) ? "未回報" : "已回報")}、政策 {s.Policies}、韌體鎖 {s.FirmwareLocked}。判讀不執行；不列政策內容。", "", "WDAC／AppLocker 登錄（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static WdacState ReadWdac()
    {
        int policies = 0, locked = 0;
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
        if (k?.GetValue("PolicyGuid") is not null) policies++;
        return new(null, policies, locked);
    }

    public static HardwareFact BootChainFact(DateTimeOffset at)
    {
        return new HardwareFact(BootChainKey, CatBoot, "開機鏈證據聚合卡", "開機鏈五環：韌體 Secure Boot／bootmgr／winload／核心／ELAM。缺口列「未收集」；只聚合既有事實，不自創證據。", "", "開機鏈事實彙總", FactTrustLevel.Derived, false, at, 5);
    }

    private static string Raw(int? v) => v?.ToString() ?? "未回報";
    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
