// 事實鍵目錄：由 Services/ 原始碼掃描產生，並由 FactKeyCatalogTests 驗證與原始碼一致。
// 為什麼要這份目錄而不是執行期掃原始碼：發佈版只有單一 exe，沒有 Services/ 目錄——
// 掃不到時會回報「0 個鍵」，而覆蓋申報會把 0 讀成「全部都覆蓋了」。
// 那是「掃不到」冒充「沒有問題」，所以改成編譯期固定的目錄＋測試守門。

using System.Collections.Generic;

namespace XinSpect;

/// <summary>事實鍵目錄：本版建置時的事實鍵全集。</summary>
/// <remarks>
/// <para>
/// <b>為什麼要有這份目錄：</b>事實鍵是<b>程式碼的性質</b>而不是機器的性質——有哪些鍵取決於
/// 有哪些服務，不取決於這台機器有什麼硬體。因此它可以、也應該在編譯期固定下來。
/// </para>
/// <para>
/// <b>為什麼不在執行期掃原始碼：</b>發佈版是單一 exe，目錄裡只有 <c>Assets/</c> 與執行檔，
/// 沒有 <c>Services/</c>。掃不到時回報「0 個鍵」，而覆蓋申報會把 0 讀成「全部都覆蓋了」——
/// 那是「掃不到」冒充「沒有問題」，正是本專案最不該犯的錯。
/// </para>
/// <para>
/// <b>一致性由測試守住：</b><c>FactKeyCatalogTests</c> 重新掃描 <c>Services/</c> 原始碼並與本目錄
/// 逐鍵比對——新增事實而忘了更新目錄會紅燈。
/// </para>
/// </remarks>
public static class FactKeyCatalog
{
    /// <summary>本版建置時掃到的事實鍵，共 141 個。</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "acpi.bert.record",
        "acpi.bert.region_length",
        "acpi.hest.sources",
        "acpi.tables",
        "amd.psp.present",
        "asset.chassis",
        "asset.identify",
        "asset.identify.evidence",
        "asset.uuid",
        "audio.endpoints",
        "audio.latency",
        "audit.logclear.count",
        "audit.logclear.last",
        "audit.mode",
        "backend.environment_decision",
        "backend.mmio",
        "backend.msr",
        "boot.duration_ms",
        "boot.last_time",
        "boot.post_code",
        "byovd.hits",
        "byovd.rules",
        "cam.count",
        "cert.foreign_roots",
        "chassis.security_status",
        "chipset.bios_cntl",
        "chipset.me_hfs",
        "chipset.smramc",
        "cmos.checksum",
        "cmos.rtc_time",
        "cmos.rtc_valid",
        "cpu.die.count",
        "cpu.generation",
        "cpu.tjmax",
        "cpu.topology.smt",
        "cxl.cfmws.count",
        "dbg.kernel",
        "dbg.start_options",
        "dbg.testsigning",
        "defender.exclusions.count",
        "display.summary",
        "drvinsp.drivers.count",
        "drvinsp.truncated",
        "ent.fc",
        "ent.iscsi",
        "ent.mpio",
        "ent.nvmeof",
        "evt.system.7d",
        "gp.registrypol",
        "gpu.lz.driver_version",
        "gpu.lz.memory",
        "gpu.lz.name",
        "gpu.radeon.fan",
        "gpu.radeon.name",
        "gpu.radeon.power",
        "gpu.radeon.temp",
        "gpu.retired_pages",
        "gpu.tdr.delay",
        "gpu.tdr.dpc",
        "gpu.tdr.level",
        "hpa.dco",
        "mchbar.base",
        "mchbar.registers",
        "mem.encryption.sgx",
        "mem.encryption.tme",
        "mem.rowhammer",
        "mon.count",
        "mon.dp",
        "monitor.summary",
        "msr.0x8b",
        "nic.count",
        "nic.dirty_count",
        "nic.gap",
        "nic.mac.0",
        "numa.slit.nodes",
        "numa.topology",
        "oob.ipmi",
        "pci.bus0.inventory",
        "pci.dev.00.0",
        "pci.dev.1f.5",
        "pci.res.1f.5",
        "pcieaer.ecam",
        "pcieaer.scan",
        "platform.feature_control",
        "platform.hvci",
        "platform.pawnio",
        "platform.secure_boot",
        "platform.testsigning",
        "pmu.fixed.0",
        "pmu.fixed_counters",
        "pmu.general_counters",
        "pmu.version",
        "psu.pmbus.pout",
        "psu.pmbus.temp1",
        "psu.pmbus.vin",
        "psu.pmbus.vout",
        "raid.oob",
        "reg.microcode",
        "role.surface",
        "role.surface.evidence",
        "setuptl.recent",
        "setuptl.sections.count",
        "smart.failing_now.count",
        "smbus.tsod",
        "spi.bios_compare",
        "spi.bios_hash",
        "spi.entropy_map",
        "spi.flash_map",
        "spi.frap",
        "spi.hsfsts",
        "spi.prr",
        "spi.regions",
        "spi.write_surface",
        "storage.reliability.available",
        "storage.reliability.count",
        "time.drift.ppm",
        "time.hpet",
        "time.pm_timer",
        "tpm.present",
        "tpm.tcg_log",
        "uefi.audit_mode",
        "uefi.boot_entries.count",
        "uefi.boot_order_count",
        "uefi.deployed_mode",
        "uefi.pk",
        "uefi.secure_boot",
        "uefi.setup_mode",
        "ufv.bios.count",
        "ups.battery.percent",
        "ups.runtime_minutes",
        "ups.status",
        "usb.busiest_controller",
        "usb.controllers",
        "usb.devices",
        "usbstor.count",
        "virt.judge",
        "virt.msr",
        "virt.vbs",
        "virt.vendor",
        "virt.vmcount",
        "virt.vswitch",
    ];

    /// <summary>鍵的數量（供申報與測試比對）。</summary>
    public static int Count => Keys.Count;
}
