This build registers **562** explicit exemptions. An exemption means the tool deliberately does *not* reconcile that class of facts, and the threshold for exemption is "no second source exists to cross with" — not "the rule was tedious to write". The complete per-entry rationale text (all 562 entries, in the language they are authored in) is in the Chinese editions; what follows is the category structure with counts of representative entries and translations of the most instructive ones.

### Why exemptions are reported but not enforced

Requiring every fact key to be considered by a rule is unrealistic where 844 keys meet 18 rules. Making "uncovered" a failure condition pushes developers to write rules that exist to lower a number: their inputs slightly contrived, their logic slightly strained, their "match" verdicts false signals that nonetheless create an impression of having been checked. So the uncovered count is made *visible and honest* instead: a reader can see how many keys no rule considers and judge accordingly whether a specific field was cross-checked.

### Category 1 — single source

These facts simply have no second path to cross with.

| Prefix | Reason |
|---|---|
| `asset.` | Identity fields (serials, SKU, asset tag) have only the firmware as a source. What *can* be checked is whether they are populated, which is already written into the `asset.identify` interpretation rather than into the values themselves. |
| `audio.` | The audio endpoint buffer is an `IAudioClient` configuration result, a single source. Its relationship to sample rate is arithmetic (milliseconds = frames ÷ sample rate), not reconciliation. |
| `role.` | Installed roles and features come from the optional-feature and service lists, a single source; service state is an extension of the same data. |
| `display.` | Display adapter identity comes from WMI, a single source. The crossing of the PnP bus source with EDID is already written into the interpretation layer. |
| `monitor.` | Monitor composition comes from the WMI EDID parse result, a single source. |
| `mon.` | The monitor connection interface (`VideoOutputTechnology`) has a single source. |
| `ups.` | Battery and UPS facts come from the Windows battery API, a single source. |
| `cert.` | Certificate store enumeration, a single source (`CertOpenStore`). |
| `defender.` | Defender state and exclusions come from WMI, a single source. |
| `byovd.` | Vulnerable-driver matching uses Microsoft's blocklist; there is no second list to compare against. |
| `time.` | Time-source facts (HPET, PM timer, drift) are measurement results, a single source. |
| `evt.` | The event log summary's source *is* the event log; self-reconciliation is meaningless. |
| `audit.` | Audit policy facts come from LSA and the event log, a single source. |
| `boot.` | Boot duration and POST codes are measurements and read-only port values, a single source. |
| `chassis.` | Chassis security state (intrusion detection, lock) comes from SMBIOS Type 3, a single source. |
| `cxl.` | CXL facts come from ACPI tables (CEDT/CHBS), a single source; this machine has no CXL hardware. |
| `smbus.` | SMBus read-only facts (TSOD) travel a single controller path. |
| `cam.` | Camera enumeration comes from the Windows media device class, a single source with no second path. |
| `usb.` | USB topology enumeration comes from the controller and hub device tree, a single source. |
| `usbstor.` | USB storage device enumeration comes from a device interface GUID query, a single source. |
| `acpi.` | Checksum self-verification is already inside the decoders (verified per table), so no separate cross-source rule is raised. |

### Category 2 — deliberately kept separate

These look like they should reconcile, but binding them would judge *normal* states as conflicts.

| Prefix | Reason |
|---|---|
| `virt.` | Virtualisation's three observations (CPUID, optional feature, service) are deliberately distinct statements. Binding them into one rule would judge the normal "installed but not loaded" state as a conflict. |
| `nic.` | The NIC "shortfall" already places the PCIe side and the line side side by side in the interpretation layer. The two are *supposed* to differ and therefore do not constitute a conflict. |
| `numa.` | NUMA topology comes from kernel32 and the ACPI SLIT table, already placed side by side at the decode layer; on single-node machines the cross-node items simply have nothing to measure. |
| `hpa.` | The HPA/DCO capacity gap *is itself* the "claimed vs addressable capacity" reconciliation, already contained. |
| `gp.` | The synthetic graphics pipeline load result has no second source. |

### Category 3 — platform limitations

Temporary technical debt, registered rather than papered over.

| Prefix | Reason |
|---|---|
| `amd.` | AMD-specific facts (SMU and PSP presence) cannot be verified because this machine is an Intel platform. When real hardware is available, a reconciliation rule should be added and this exemption removed. |
| `oob.` | Out-of-band management presence probing (SMBIOS Type 38); simply not applicable when the machine has no such hardware. |

### Category 4 — specification decisions (things explicitly not done)

| Prefix | Reason |
|---|---|
| `xt.rest` | REST example set (XT-003): **explicitly declined.** Publishing a usable example means shipping code that targets an uncommitted endpoint; the alternatives are the CLI's `--json` output and file exchange. |
| `dl.dump` | Process dump gate (DL-002): declares consent state only; this version has no dump implementation, so there is no value to reconcile. |
| `zr.passkey` | Passkey support (ZR-005): a conditional statement of file existence plus build number, with no second source. |

### Category 5 — facts recorded with their governing item

These entries record the design constraint behind a capability item, so that "decided against" is a recorded decision rather than an omission.

| Prefix | Reason |
|---|---|
| `ng.checklist` | Custom checklist (NG-002): a user asset, verified line by line with line numbers; no silent partial success, unrecognised key names are not errors, verification only without execution. |
| `zr.compliance.` | Device compliance baseline (ZR-004): a user-supplied compliance list (knowledge-pack table); **missing data must not masquerade as a pass**, the snapshot date is always printed, and item text is not listed. |
| `ag.service` | Background service mode (AG-005): capability inventory and rough cost; **creates no service and registers no schedule** — it answers "what is missing if we wanted to", because "could be done" and "is being done" are two different things. |
| `po.svcboot.` | Service readiness timeline (PO-005): seconds relative to boot; **time is a fact, cause is not** — no grading or judging of slowness, and no service names listed. |
| `tu.jitter` | Local jitter measurement (TU-008): the same opt-in allowlist as `ms.ping`; no target specified means no traffic **and not 0 ms**; both the sample count and the definition of jitter are stated. |
| `rs.sig` | Report signing (RS-010): the audit chain head and the verification result; chain integrity is guarded by `AuditVerifier`'s dedicated tests rather than by a reconciliation rule. |
| `hm.hwdiff` | Hardware change timeline (HM-007): the input is a comparison of two snapshots, already a third-party cross-check, so no further rules are stacked on it. |
| `se.pipe.` | Named pipe enumeration (SE-004): the pipe directory is a single source; enumeration does not imply connectability, nor that anyone is using it. |
| `qs.help` | Help coverage (QS-005): a comparison of `HelpCatalog` against `PageRegistry`, both project-owned assets. |
| `zr.mdm` | MDM enrolment state (ZR-001): a single source in the Enrollments registry; status only, no identity data read. |
| `zr.aadj` | Entra join state (ZR-002): a single source in the AAD/WorkplaceJoin registry. |

### Prefixes deliberately *not* exempted

Some classes that could be exempted are deliberately left in the "uncovered" list so that they remain visible. The clearest case is `psu.`: PMBus mains power against RAPL and NVML **can** be reconciled (the blueprint's "power conservation reconciliation"), so it is not exempted — the fact that it is crossable but not yet crossed is recorded in the coverage output rather than hidden behind an exemption.

---
