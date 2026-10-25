# U10 四方查重・自動初判報告

**這是自動查重的初判。`key-dup` 與 `family-overlap` 不等於重複**：

- 撞鍵可能是**既有實作已收口**——該列應標「收口」，引用既有鍵即可；
- 也可能是**候選鍵命名撞車**——改一個名就是新列，不必刪；
- 家族重疊只說明「前兩段同家族」，同家族不等於同能力。

**結論以人工複核為準**，本報告只負責把嫌疑名單列全。

---

## 一、總量統計

- 新目錄檔：`ULTIMATE-10000-EXPANSION-2026-10-24.md`，共 11863 行
- U10 條目總數：**10022**（不重複 ID 10022）
- 域數：**39**（U10-XX 的 XX）
  - 各域條數：AC(798)、AT(100)、BD(30)、BT(197)、CP(787)、EN(25)、EV(20)、FS(61)、FW(512)、GD(542)、GP(139)、GV(76)、HU(123)、IC(593)、KP(24)、MM(87)、MR(485)、MS(187)、MT(73)、NP(719)、NV(350)、NX(21)、OS(224)、PC(355)、PF(43)、PH(554)、PM(798)、RP(28)、SB(1108)、SE(62)、SS(140)、ST(83)、TS(227)、UI(48)、VC(43)、VT(181)、VZ(127)、WH(16)、WM(36)
- 有鍵條目：**9964**；無鍵條目：**58**（無鍵＝該列反引號內抽不出符合鍵形狀的字串）
- 候選鍵去重後：9933 把
- 既有具體鍵（.cs 字面 + 既有目錄 md 反引號，合併去重）：**2265** 把（其中出自 .cs 的 1367 把）
- 既有目錄 md 裡的家族形申報（`*`／`{...}`／前綴）：404 個家族
- 既有具體鍵構成的兩段家族數：1807

## 二、key-dup（候選鍵與既有鍵逐字相同）

共 **8** 筆。

| U10 ID | 鍵 | 描述 | 撞在哪個檔案 |
|---|---|---|---|
| U10-MM-0027 | `mem.slot.map` | 通道/插槽實體對映（SMBIOS Type 17 locator 與控制器定序交叉） | `docs/ULTIMATE-1000/STAGE-03-SILICON.md` |
| U10-MM-0038 | `cache.l3.slices` | L3 跨 CCX/CCD 分塊（AMD 拓撲；分塊禁止加總陳述） | `docs/ULTIMATE-1000/STAGE-03-SILICON.md` |
| U10-SB-0019 | `sb.type1.uuid` | UUID（位元組序陷阱成文；全 0/全 F 之「無效」判讀） | `Services/FactKeyCatalog.cs`、`Services/SmbiosIdentityFactsService.cs` |
| U10-FW-0004 | `uefi.kek.count` | KEK 清單計數與大小 | `Services/FactKeyCatalog.cs`、`Services/UefiSignatureFactsService.cs`、`docs/ULTIMATE-1000/GATE-LOG-258.md` |
| U10-FW-0060 | `tpm.pcr.banks` | PCR bank 清單與各 bank 已分配索引遮罩 | `Services/FactKeyCatalog.cs`、`Services/TpmFactsService.cs`、`docs/ULTIMATE-1000/GATE-LOG-258.md`、`docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md` |
| U10-FW-0100 | `mc.date` | 微碼日期欄位（登錄檔/更新歷史源） | `docs/ULTIMATE-1000/STAGE-03-SILICON.md` |
| U10-FW-0106 | `ec.present` | EC 存在性判讀（ACPI PNP ID 如 ITE/ENE 之跡證；僅存在性） | `Services/EmbeddedControllerFactsService.cs`、`Services/FactKeyCatalog.cs`、`docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md` |
| U10-NP-0121 | `bt.state` | 藍牙電源狀態與可發現性設定 | `docs/ULTIMATE-1000/GATE-LOG-258.md`、`docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md`、`docs/ULTIMATE-1000/STAGE-02-REMAINING-KEYS.md` |

## 三、family-overlap（前兩段家族與既有鍵重疊）

共 **147** 筆條目，涉及 **53** 個家族。

| 家族 | 既有鍵數 | 既有鍵抽樣 | U10 條目（抽樣） | U10 條目數 |
|---|---:|---|---|---:|
| `ac.dsm.` | 1 | `ac.dsm.summary` | `ac.dsm.pci.guids`、`ac.dsm.usbbat.guids` | 2 |
| `acpi.bert.` | 2 | `acpi.bert.record`、`acpi.bert.region_length` | `acpi.bert.raw` | 1 |
| `acpi.hest.` | 1 | `acpi.hest.sources` | `acpi.hest.src{n}` | 1 |
| `audio.capture.` | 1 | `audio.capture` | `audio.capture.formats` | 1 |
| `audio.endpoints.` | 1 | `audio.endpoints` | `audio.endpoints.list` | 1 |
| `cli.exit.` | 1 | `cli.exit.code` | `cli.exit.contract` | 1 |
| `cpu.hybrid.` | 1 | `cpu.hybrid` | `cpu.hybrid.core{n}`、`cpu.hybrid.counts` | 2 |
| `fleet.diff.` | 5 | `fleet.diff.kind`、`fleet.diff.present`、`fleet.diff.state`、`fleet.diff.symmetric`、`fleet.diff.value` | `fleet.diff.report` | 1 |
| `fleet.report.` | 1 | `fleet.report` | `fleet.report.template` | 1 |
| `fw.bootchain.` | 1 | `fw.bootchain.summary` | `fw.bootchain.stages` | 1 |
| `fw.fv.` | 1 | `fw.fv` | `fw.fv.cross` | 1 |
| `fw.me.` | 2 | `fw.me`、`fw.me.manifest` | `fw.me.psp.info`、`fw.me.stance` | 2 |
| `fw.risk.` | 6 | `fw.risk.facts.baseline`、`fw.risk.facts.delta`、`fw.risk.facts.gap`、`fw.risk.facts.level`、`fw.risk.facts.parts`… | `fw.risk.cross` | 1 |
| `fw.smm.` | 1 | `fw.smm.summary` | `fw.smm.tseg` | 1 |
| `fw.spi.` | 1 | `fw.spi` | `fw.spi.hash.cross` | 1 |
| `gpu.pcie.` | 2 | `gpu.pcie.download-bandwidth-gbps`、`gpu.pcie.upload-bandwidth-gbps` | `gpu.pcie.link`、`gpu.pcie.replay`、`gpu.pcie.throughput` | 3 |
| `mem.slot.` | 1 | `mem.slot.map` | `mem.slot.occupied` | 1 |
| `mem.speed.` | 1 | `mem.speed.delta` | `mem.speed.setvsrun` | 1 |
| `mem.training.` | 1 | `mem.training` | `mem.training.state` | 1 |
| `msr.0x8b.` | 1 | `msr.0x8b` | `msr.0x8b.amd64.patch.level`、`msr.0x8b.ia32.ucode.rev` | 2 |
| `net.dns.` | 1 | `net.dns` | `net.dns.config` | 1 |
| `numa.slit.` | 1 | `numa.slit.nodes` | `numa.slit.grid` | 1 |
| `nvme.ns.` | 0 | （僅前綴申報：`docs/ULTIMATE-1000/STAGE-04-STORAGE.md`） | `nvme.ns.activevsall`、`nvme.ns.changed`、`nvme.ns.ctlmap` | 3 |
| `nvme.smart.` | 1 | `nvme.smart.cross` | `nvme.smart.busytime`、`nvme.smart.critwarn`、`nvme.smart.du.tb`、`nvme.smart.errlog{i}`、`nvme.smart.hostcmds`… | 16 |
| `nvme.temp.` | 0 | （僅前綴申報：`docs/ULTIMATE-1000/STAGE-04-STORAGE.md`） | `nvme.temp.loadcorr`、`nvme.temp.sensor{i}.th`、`nvme.temp.series` | 3 |
| `os.uptime.` | 1 | `os.uptime` | `os.uptime.stats` | 1 |
| `pdev.fw.` | 3 | `pdev.fw.delta`、`pdev.fw.updatable`、`pdev.fw.version` | `pdev.fw.inventory` | 1 |
| `sb.mem.` | 0 | （僅前綴申報：`docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md`） | `sb.mem.linkcheck` | 1 |
| `sb.oem.` | 3 | `sb.oem.count`、`sb.oem.list`、`sb.oem.strings` | `sb.oem.raw` | 1 |
| `sb.type0.` | 1 | `sb.type0` | `sb.type0.char.bits`、`sb.type0.char.ext1`、`sb.type0.char.ext2`、`sb.type0.date`、`sb.type0.romsize`… | 8 |
| `sb.type1.` | 2 | `sb.type1`、`sb.type1.uuid` | `sb.type1.ident`、`sb.type1.os.cross`、`sb.type1.serial`、`sb.type1.sku`、`sb.type1.uuid.caveat`… | 6 |
| `sb.type2.` | 1 | `sb.type2` | `sb.type2.boardtype`、`sb.type2.chipset.hint`、`sb.type2.contains`、`sb.type2.feature`、`sb.type2.ident`… | 8 |
| `sb.type3.` | 1 | `sb.type3` | `sb.type3.bootstate`、`sb.type3.elements`、`sb.type3.height`、`sb.type3.ident`、`sb.type3.lock`… | 10 |
| `sb.type4.` | 2 | `sb.type4`、`sb.type4.count` | `sb.type4.cache.links`、`sb.type4.chars`、`sb.type4.core.cross`、`sb.type4.cores`、`sb.type4.cpuregid`… | 16 |
| `sb.type7.` | 2 | `sb.type7.count`、`sb.type7.summary` | `sb.type7.assoc`、`sb.type7.config`、`sb.type7.cpuid.cross`、`sb.type7.ecc`、`sb.type7.l1split`… | 9 |
| `sec.dma.` | 1 | `sec.dma.kernel` | `sec.dma.perdev`、`sec.dma.protect` | 2 |
| `sec.hvci.` | 1 | `sec.hvci` | `sec.hvci.risk` | 1 |
| `sec.vbs.` | 1 | `sec.vbs` | `sec.vbs.services`、`sec.vbs.state` | 2 |
| `spd.speed.` | 0 | （僅前綴申報：`docs/ULTIMATE-1000/STAGE-03-SILICON.md`） | `spd.speed.{dimm}` | 1 |
| `tpm.pcr.` | 1 | `tpm.pcr.banks` | `tpm.pcr.map`、`tpm.pcr.replay`、`tpm.pcr.{bank}` | 3 |
| `tpm.present.` | 1 | `tpm.present` | `tpm.present.cross` | 1 |
| `uefi.boot.` | 0 | （僅前綴申報：`Services/FactKeyDynamicCatalog.cs`） | `uefi.boot.current`、`uefi.boot.gaps`、`uefi.boot.next`、`uefi.boot.order`、`uefi.boot.timeout`… | 9 |
| `uefi.db.` | 3 | `uefi.db`、`uefi.db.count`、`uefi.db.list.0` | `uefi.db.chain`、`uefi.db.expiry` | 2 |
| `uefi.dbx.` | 2 | `uefi.dbx`、`uefi.dbx.count` | `uefi.dbx.count.cross`、`uefi.dbx.kinds` | 2 |
| `uefi.kek.` | 3 | `uefi.kek`、`uefi.kek.count`、`uefi.kek.list.0` | `uefi.kek.algos` | 1 |
| `uefi.pk.` | 1 | `uefi.pk` | `uefi.pk.size` | 1 |
| `ui.a11y.` | 1 | `ui.a11y` | `ui.a11y.names` | 1 |
| `ui.key.` | 1 | `ui.key.label` | `ui.key.compare`、`ui.key.sidepanel` | 2 |
| `ui.search.` | 1 | `ui.search` | `ui.search.global` | 1 |
| `usb.history.` | 1 | `usb.history` | `usb.history.devices` | 1 |
| `usb.speed.` | 1 | `usb.speed.delta` | `usb.speed.mismatch` | 1 |
| `viz.heatmap.` | 1 | `viz.heatmap` | `viz.heatmap.cclatency`、`viz.heatmap.coretime`、`viz.heatmap.memlat` | 3 |
| `viz.topo.` | 1 | `viz.topo` | `viz.topo.power`、`viz.topo.speeds` | 2 |

### 家族對照抽樣（每家族最多 6 筆）

- **`ac.dsm.`**（既有 1 鍵 / U10 2 筆）：既有如 `ac.dsm.summary`；U10 如 `ac.dsm.pci.guids`、`ac.dsm.usbbat.guids`
- **`acpi.bert.`**（既有 2 鍵 / U10 1 筆）：既有如 `acpi.bert.record`、`acpi.bert.region_length`；U10 如 `acpi.bert.raw`
- **`acpi.hest.`**（既有 1 鍵 / U10 1 筆）：既有如 `acpi.hest.sources`；U10 如 `acpi.hest.src{n}`
- **`audio.capture.`**（既有 1 鍵 / U10 1 筆）：既有如 `audio.capture`；U10 如 `audio.capture.formats`
- **`audio.endpoints.`**（既有 1 鍵 / U10 1 筆）：既有如 `audio.endpoints`；U10 如 `audio.endpoints.list`
- **`cli.exit.`**（既有 1 鍵 / U10 1 筆）：既有如 `cli.exit.code`；U10 如 `cli.exit.contract`
- **`cpu.hybrid.`**（既有 1 鍵 / U10 2 筆）：既有如 `cpu.hybrid`；U10 如 `cpu.hybrid.core{n}`、`cpu.hybrid.counts`
- **`fleet.diff.`**（既有 5 鍵 / U10 1 筆）：既有如 `fleet.diff.kind`、`fleet.diff.present`、`fleet.diff.state`；U10 如 `fleet.diff.report`
- **`fleet.report.`**（既有 1 鍵 / U10 1 筆）：既有如 `fleet.report`；U10 如 `fleet.report.template`
- **`fw.bootchain.`**（既有 1 鍵 / U10 1 筆）：既有如 `fw.bootchain.summary`；U10 如 `fw.bootchain.stages`
- **`fw.fv.`**（既有 1 鍵 / U10 1 筆）：既有如 `fw.fv`；U10 如 `fw.fv.cross`
- **`fw.me.`**（既有 2 鍵 / U10 2 筆）：既有如 `fw.me`、`fw.me.manifest`；U10 如 `fw.me.psp.info`、`fw.me.stance`
- **`fw.risk.`**（既有 6 鍵 / U10 1 筆）：既有如 `fw.risk.facts.baseline`、`fw.risk.facts.delta`、`fw.risk.facts.gap`；U10 如 `fw.risk.cross`
- **`fw.smm.`**（既有 1 鍵 / U10 1 筆）：既有如 `fw.smm.summary`；U10 如 `fw.smm.tseg`
- **`fw.spi.`**（既有 1 鍵 / U10 1 筆）：既有如 `fw.spi`；U10 如 `fw.spi.hash.cross`
- **`gpu.pcie.`**（既有 2 鍵 / U10 3 筆）：既有如 `gpu.pcie.download-bandwidth-gbps`、`gpu.pcie.upload-bandwidth-gbps`；U10 如 `gpu.pcie.link`、`gpu.pcie.replay`、`gpu.pcie.throughput`
- **`mem.slot.`**（既有 1 鍵 / U10 1 筆）：既有如 `mem.slot.map`；U10 如 `mem.slot.occupied`
- **`mem.speed.`**（既有 1 鍵 / U10 1 筆）：既有如 `mem.speed.delta`；U10 如 `mem.speed.setvsrun`
- **`mem.training.`**（既有 1 鍵 / U10 1 筆）：既有如 `mem.training`；U10 如 `mem.training.state`
- **`msr.0x8b.`**（既有 1 鍵 / U10 2 筆）：既有如 `msr.0x8b`；U10 如 `msr.0x8b.amd64.patch.level`、`msr.0x8b.ia32.ucode.rev`
- **`net.dns.`**（既有 1 鍵 / U10 1 筆）：既有如 `net.dns`；U10 如 `net.dns.config`
- **`numa.slit.`**（既有 1 鍵 / U10 1 筆）：既有如 `numa.slit.nodes`；U10 如 `numa.slit.grid`
- **`nvme.ns.`**（既有 0 鍵 / U10 3 筆）：僅前綴申報（`docs/ULTIMATE-1000/STAGE-04-STORAGE.md`），無具體鍵可比；U10 如 `nvme.ns.activevsall`、`nvme.ns.changed`、`nvme.ns.ctlmap`
- **`nvme.smart.`**（既有 1 鍵 / U10 16 筆）：既有如 `nvme.smart.cross`；U10 如 `nvme.smart.busytime`、`nvme.smart.critwarn`、`nvme.smart.du.tb`
- **`nvme.temp.`**（既有 0 鍵 / U10 3 筆）：僅前綴申報（`docs/ULTIMATE-1000/STAGE-04-STORAGE.md`），無具體鍵可比；U10 如 `nvme.temp.loadcorr`、`nvme.temp.sensor{i}.th`、`nvme.temp.series`
- **`os.uptime.`**（既有 1 鍵 / U10 1 筆）：既有如 `os.uptime`；U10 如 `os.uptime.stats`
- **`pdev.fw.`**（既有 3 鍵 / U10 1 筆）：既有如 `pdev.fw.delta`、`pdev.fw.updatable`、`pdev.fw.version`；U10 如 `pdev.fw.inventory`
- **`sb.mem.`**（既有 0 鍵 / U10 1 筆）：僅前綴申報（`docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md`），無具體鍵可比；U10 如 `sb.mem.linkcheck`
- **`sb.oem.`**（既有 3 鍵 / U10 1 筆）：既有如 `sb.oem.count`、`sb.oem.list`、`sb.oem.strings`；U10 如 `sb.oem.raw`
- **`sb.type0.`**（既有 1 鍵 / U10 8 筆）：既有如 `sb.type0`；U10 如 `sb.type0.char.bits`、`sb.type0.char.ext1`、`sb.type0.char.ext2`
- **`sb.type1.`**（既有 2 鍵 / U10 6 筆）：既有如 `sb.type1`、`sb.type1.uuid`；U10 如 `sb.type1.ident`、`sb.type1.os.cross`、`sb.type1.serial`
- **`sb.type2.`**（既有 1 鍵 / U10 8 筆）：既有如 `sb.type2`；U10 如 `sb.type2.boardtype`、`sb.type2.chipset.hint`、`sb.type2.contains`
- **`sb.type3.`**（既有 1 鍵 / U10 10 筆）：既有如 `sb.type3`；U10 如 `sb.type3.bootstate`、`sb.type3.elements`、`sb.type3.height`
- **`sb.type4.`**（既有 2 鍵 / U10 16 筆）：既有如 `sb.type4`、`sb.type4.count`；U10 如 `sb.type4.cache.links`、`sb.type4.chars`、`sb.type4.core.cross`
- **`sb.type7.`**（既有 2 鍵 / U10 9 筆）：既有如 `sb.type7.count`、`sb.type7.summary`；U10 如 `sb.type7.assoc`、`sb.type7.config`、`sb.type7.cpuid.cross`
- **`sec.dma.`**（既有 1 鍵 / U10 2 筆）：既有如 `sec.dma.kernel`；U10 如 `sec.dma.perdev`、`sec.dma.protect`
- **`sec.hvci.`**（既有 1 鍵 / U10 1 筆）：既有如 `sec.hvci`；U10 如 `sec.hvci.risk`
- **`sec.vbs.`**（既有 1 鍵 / U10 2 筆）：既有如 `sec.vbs`；U10 如 `sec.vbs.services`、`sec.vbs.state`
- **`spd.speed.`**（既有 0 鍵 / U10 1 筆）：僅前綴申報（`docs/ULTIMATE-1000/STAGE-03-SILICON.md`），無具體鍵可比；U10 如 `spd.speed.{dimm}`
- **`tpm.pcr.`**（既有 1 鍵 / U10 3 筆）：既有如 `tpm.pcr.banks`；U10 如 `tpm.pcr.map`、`tpm.pcr.replay`、`tpm.pcr.{bank}`
- **`tpm.present.`**（既有 1 鍵 / U10 1 筆）：既有如 `tpm.present`；U10 如 `tpm.present.cross`
- **`uefi.boot.`**（既有 0 鍵 / U10 9 筆）：僅前綴申報（`Services/FactKeyDynamicCatalog.cs`），無具體鍵可比；U10 如 `uefi.boot.current`、`uefi.boot.gaps`、`uefi.boot.next`
- **`uefi.db.`**（既有 3 鍵 / U10 2 筆）：既有如 `uefi.db`、`uefi.db.count`、`uefi.db.list.0`；U10 如 `uefi.db.chain`、`uefi.db.expiry`
- **`uefi.dbx.`**（既有 2 鍵 / U10 2 筆）：既有如 `uefi.dbx`、`uefi.dbx.count`；U10 如 `uefi.dbx.count.cross`、`uefi.dbx.kinds`
- **`uefi.kek.`**（既有 3 鍵 / U10 1 筆）：既有如 `uefi.kek`、`uefi.kek.count`、`uefi.kek.list.0`；U10 如 `uefi.kek.algos`
- **`uefi.pk.`**（既有 1 鍵 / U10 1 筆）：既有如 `uefi.pk`；U10 如 `uefi.pk.size`
- **`ui.a11y.`**（既有 1 鍵 / U10 1 筆）：既有如 `ui.a11y`；U10 如 `ui.a11y.names`
- **`ui.key.`**（既有 1 鍵 / U10 2 筆）：既有如 `ui.key.label`；U10 如 `ui.key.compare`、`ui.key.sidepanel`
- **`ui.search.`**（既有 1 鍵 / U10 1 筆）：既有如 `ui.search`；U10 如 `ui.search.global`
- **`usb.history.`**（既有 1 鍵 / U10 1 筆）：既有如 `usb.history`；U10 如 `usb.history.devices`
- **`usb.speed.`**（既有 1 鍵 / U10 1 筆）：既有如 `usb.speed.delta`；U10 如 `usb.speed.mismatch`
- **`viz.heatmap.`**（既有 1 鍵 / U10 3 筆）：既有如 `viz.heatmap`；U10 如 `viz.heatmap.cclatency`、`viz.heatmap.coretime`、`viz.heatmap.memlat`
- **`viz.topo.`**（既有 1 鍵 / U10 2 筆）：既有如 `viz.topo`；U10 如 `viz.topo.power`、`viz.topo.speeds`

## 四、unique（無撞鍵）

共 **9809** 筆有鍵條目在三方既有集合中都找不到同鍵、也找不到同兩段家族。另有 **58** 筆**無鍵條目**——它們沒有鍵可比，本腳本無法替它們判定撞鍵與否，清單見 report.json 的 `no_key_entries`，需人工補鍵或人工判定。

完整清單見 `report.json` 的 `unique` 欄。

## 五、〔查重〕標記條目（供下一輪人工收口）

目錄正文自稱「〔查重〕84 處」；本腳本在 U10 表格列內數到含「查重」字樣的列 **79** 列（含 〔查重〕〔查重——〔查重：等變體，純正文敘述行不計）。兩個數字對不上就以本腳本清單為準逐列核。

| U10 ID | 描述 |
|---|---|
| U10-CP-0148 | leaf 0x12 SGX 子葉展開（EPC 逐銀行大小；查重：階二已做者收口） |
| U10-CP-0217 | 微碼修訂雙源對帳（登錄檔 vs MSR 0x8B；衝突陳述）〔查重：mc.* 收口〕 |
| U10-CP-0223 | NUMA 節點清單與距離矩陣（SLIT；查重 SRAT 已有者收口） |
| U10-MS-0053 | IA32_BIOS_SIGN_ID（0x8B）微碼修訂（雙源對帳另一源）〔查重：mc.*〕 |
| U10-MM-0040 | 延遲曲線（stride 掃描；拐點即快取邊界）〔查重：roadmap 舊項〕 |
| U10-MM-0052 | 記憶體映射裝置佔用（MMIO 總量；Type 19/ACPI 交叉）〔查重：sb.memmap 收口〕 |
| U10-MM-0059 | 記憶體錯誤記錄（WHEA 記憶體節區；僅轉述不歸因）〔查重 OS 域交叉〕 |
| U10-SB-0097 | Type 19 記憶體映射〔查重：sb.memmap 已做——此為對帳收口〕 |
| U10-SB-0161 | Type 45 項目與 pdev.fw.updatable 盤點對帳〔查重收口〕 |
| U10-AC-0042 | TCPA／TPM2 表〔查重：階二已做——收口對帳〕 |
| U10-AC-0043 | DBG2／SPCR／BGRT〔查重：2.84 已做——收口對帳〕 |
| U10-AC-0047 | SRAT〔查重：已有 Service——收口〕＋親近性網域清單 |
| U10-AC-0048 | SLIT 距離矩陣（與 NUMA 節點交叉）〔查重〕 |
| U10-FW-0005 | db／dbx／dbt／dbr 存在性與計數〔查重：uefi.db.* 已做——收口〕 |
| U10-FW-0021 | 條目跳號偵測（如 0–6、8、9——與執行期家族對帳規則沿用）〔查重〕 |
| U10-FW-0037 | SPI BIOS 區塊雜湊〔查重：階二 SPI 地圖——收口〕 |
| U10-FW-0046 | 韌體防護：Boot Guard 生產模式狀態鏈〔查重：階二 FIT/Boot Guard——收口〕 |
| U10-FW-0050 | 映像 FV/FFS 分解〔查重：階二已做——收口對帳行〕 |
| U10-FW-0051 | TPM 存在性與來源（TBS／WMI 雙路徑；查重：已有——收口） |
| U10-FW-0061 | PCR 逐 bank 現值（SHA-1/256/384/512；查重：已有——收口擴 bank） |
| U10-FW-0075 | TCG log 逐事件類型統計〔查重：tpm.ev.* ——收口為全型別表〕 |
| U10-FW-0104 | 周邊韌體盤點全表〔查重：pdev.fw.updatable——擴充為全裝置〕 |
| U10-FW-0113 | 韌體風險陳述六把〔查重：fw.risk.facts.*——收口引用〕 |
| U10-NV-0068 | 0x03 韌體槽〔查重：已做——收口〕 |
| U10-OS-0025 | Win32_Processor〔查重：核心已用——收口未用欄位清單〕 |
| U10-OS-0028 | Win32_BIOS〔查重：與 SMBIOS 對帳收口〕 |
| U10-OS-0029 | Win32_BaseBoard／Win32_SystemEnclosure〔查重收口〕 |
| U10-OS-0030 | Win32_PhysicalMemory〔查重：作 SPD 對帳第三源〕 |
| U10-OS-0033 | Win32_DiskDrive〔查重：擴充未用欄位（分區/匯流排類型碼）〕 |
| U10-OS-0038 | Win32_VideoController〔查重：擴充 VRAM 類型/掃描模式欄〕 |
| U10-OS-0040 | Win32_NetworkAdapter(+Configuration)〔查重收口〕 |
| U10-OS-0057 | Win32_PnPEntity（全量；問題碼過濾用）〔查重〕 |
| U10-OS-0061 | Win32_TPM〔查重：已有 WMI 備援路徑——收口〕 |
| U10-SE-0003 | VBS/HVCI 啟用鏈（含未啟用原因碼）〔查重 2.84——收口擴鏈〕 |
| U10-SE-0036 | 週邊韌體可攻擊面陳述（可更新周邊 vs 不可更新——事實面）〔查重 pdev.fw〕 |
| U10-SE-0043 | 憑證主體名稱絕不出現——L3 禁項守門（正對照測試）〔查重：已有——收口〕 |
| U10-SE-0044 | 匯出遮蔽預覽與解除留痕（L2 解除逐條紀錄）〔查重——收口〕 |
| U10-SE-0047 | 韌體風險六把報告節〔查重 fw.risk.facts——報告整合〕 |
| U10-SE-0051 | 出網零聲明之驗證（egress.allowlist 空集的守門）〔查重——收口〕 |
| U10-SE-0054 | Smart App Control 三態〔查重 2.84——收口引用〕 |
| U10-SE-0058 | 驅動服務 SDDL 異常清單〔查重 2.84——擴展至硬碟控制器類〕 |
| U10-SE-0059 | 替代資料流掃描（驅動檔案 ADS）〔查重 2.84——收口引用〕 |
| U10-SE-0060 | 已安裝程式簽章狀態分布〔查重 2.84——硬體相關子集〕 |
| U10-SE-0061 | 登入歷史硬體關聯（裝置插入與登入時間交集——僅事實）〔查重 2.84〕 |
| U10-MT-0036 | 季節模式偵測〔查重 2.84——收口引用〕 |
| U10-MT-0044 | 解碼器全量單元測試〔查重：突變測試已有——擴展目標成文〕 |
| U10-MT-0049 | 解碼方法之規格出處註記守門（未註記者測試擋下）〔查重既有規則——自動化〕 |
| U10-VZ-0014 | 桑基圖〔查重 2.84 已有——收口引用〕 |
| U10-VZ-0016 | 拓撲樹〔查重 2.84——擴充互動〕 |
| U10-VZ-0064 | 事實總表頁 sparkline 欄〔查重 2.86 FactAtlas——擴充〕 |
| U10-VZ-0069 | 3D 拓撲持續不做〔查重 2.84 NX——重申〕 |
| U10-VZ-0078 | 釘選感測器面板（常駐小窗/迷你浮窗〔查重：MiniOverlay 已有——擴充〕 |
| U10-UI-0010 | 逐頁說明〔查重 HelpCatalog——擴充覆蓋新域〕 |
| U10-UI-0014 | 三語切換即時生效〔查重：語言工作已完成——收口檢查項〕 |
| U10-UI-0015 | 三語鍵同步守門〔查重 lang-* 工具鏈——轉為常設守門〕 |
| U10-UI-0032 | 迷你浮窗擴充〔查重 MiniOverlay——自訂內容版〕 |
| U10-UI-0033 | 應用內更新紀錄檢視〔查重 ChangelogCatalog——頁面收口〕 |
| U10-UI-0034 | 關於頁授權清單〔查重 THIRD-PARTY-NOTICES——收口〕 |
| U10-PF-0001 | HTML 報告產生器〔查重：離線自足已有——擴充節架構〕 |
| U10-PF-0009 | 匯出簽章（內嵌雜湊鏈；第三方可驗）〔查重報告雜湊——擴至 JSON/CSV〕 |
| U10-PF-0012 | Headless 掃描模式〔查重已有——擴充分域逾時參數〕 |
| U10-PF-0017 | PowerShell 模組逐域 cmdlet〔查重 psm1——補齊新域〕 |
| U10-PF-0028 | 安裝器〔查重 Installer/——收口檢查〕 |
| U10-PF-0029 | 單一檔案發佈驗證自動化〔查重發版技能——轉守門〕 |
| U10-EN-0012 | 知識包版本演進與撤回〔查重 kp.*/key.evolution——收口成流程〕 |
| U10-GV-0002 | 守門白名單上限（成長預算——防白名單腐化）〔查重——收口〕 |
| U10-GV-0003 | 誤報治理（FP 紀錄；放寬一律不接受——新域適用）〔查重——收口〕 |
| U10-GV-0004 | 突變測試目標擴充（逐版提升覆蓋域）〔查重 Stryker——排程化〕 |
| U10-GV-0005 | 測試數單調閘（測試數只升不降；降必須解釋）〔查重 TestSuiteBaseline——常設〕 |
| U10-GV-0006 | 鍵目錄與原始碼逐鍵相等閘〔查重——擴至新域〕 |
| U10-GV-0007 | 執行期產鍵必須登記閘〔查重 2.85——擴至新動態家族〕 |
| U10-GV-0008 | 更新紀錄最新筆=版號閘〔查重 ChangelogTests——常設〕 |
| U10-GV-0009 | 檢視 XAML 不得留舊版號閘〔查重 2.87——常設〕 |
| U10-GV-0011 | 覆蓋申報基線（已檢視未覆蓋清單逐版成長管控）〔查重——收口〕 |
| U10-GV-0012 | 文件對帳閘〔查重——擴至本目錄文件〕 |
| U10-GV-0014 | 發佈驗證（release.verify——每版自動）〔查重——收口〕 |
| U10-GV-0015 | 可重現建置（build.reproducible 加強：兩次建置雜湊一致）〔查重——收口〕 |
| U10-GV-0036 | 守門執行時間預算（GuardBudget——新閘納入）〔查重——收口〕 |
| U10-GV-0040 | 目錄文件稽核工具自跑（入版控且會被跑——2.87 教訓）〔查重——收口〕 |

## 六、方法與限制（誠實聲明）

- 候選鍵：U10 表格列「鍵欄及其後」反引號內、形如 `a.b…` 的字串；GUID、hex 序列、無點字串不算鍵。描述欄內出現的鍵形反引號字串刻意不計（避免把「沿用既有鍵」的敘述誤判成撞鍵）；這種條目有 0 筆，清單見 report.json `stats` 之計數與 `desc_bt_ids`。
- 既有鍵：.cs 引號內具體鍵形字串（含註解裡出現的），加上既有目錄 md 反引號內的具體鍵。
  一般原始碼的掃描另加兩道雜訊過濾：首段必須全小寫（駝峰串是方法名／事件名不是鍵）、末段是常見副檔名的排除（advapi32.dll、cmd.exe 是檔名不是鍵）。
  這個抽法仍可能混入少量非鍵的字串字面（誤報方向），也可能漏掉拼出來的動態鍵（漏報方向）——
  `FactKeyDynamicCatalog` 已登記的動態家族前綴已併入家族判定。
- 家族＝鍵的前兩段。家族重疊是**粗篩**，同家族下是否同一能力必須人工看描述。
- 無鍵條目與查重標記條目的數目如實列出，不硬湊。
