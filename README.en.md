[繁體中文](README.md) · [简体中文](README.zh-CN.md) · English

[![Official site](https://img.shields.io/badge/%F0%9F%8C%90_OFFICIAL_SITE-xinspect--browser--preview.wild--chimpanzee.workers.dev-4C8DFF?style=for-the-badge&logo=cloudflare&logoColor=white)](https://xinspect-browser-preview.wild-chimpanzee.workers.dev)

> ## 🌐 [Official site: xinspect-browser-preview.wild-chimpanzee.workers.dev ↗](https://xinspect-browser-preview.wild-chimpanzee.workers.dev)
>
> **Product overview · Fact catalogue · Features and data sources · Three-source reconciliation · Evidence and snapshots · Version history · Downloads and requirements** — all on one page.
> The site only presents documentation; it **does not scan your machine** and needs no installation.

# XinSpect

A free, open-source hardware verification, monitoring, and security auditing tool for Windows. Ships as a single portable executable. Read-only by default; the few operations that write are gated behind explicit consent and labelled with their risks. It collects and transmits no user data.

![Version](https://img.shields.io/badge/version-3.10-4C8DFF)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0A7EA4)
![Framework](https://img.shields.io/badge/.NET-10.0--windows%20(WPF)-512BD4)
![Tests](https://img.shields.io/badge/tests-7713%20passed-3FB950)
![Mutation Score](https://img.shields.io/badge/Stryker-82%25-8B5CF6)
![License](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue)
![Commercial license](https://img.shields.io/badge/license-commercial%20available-8B5CF6)

The name means "morning light, seen at a glance"; "Spect" comes from the Latin *spectator*. This is not a tool that draws conclusions for you. It lays three accounts of the same machine side by side — what the hardware says, what the firmware says, and what the operating system says — gives a value and a source when it can read one, gives a reason when it cannot, and prints contradictions in red with both sides named. Interpretation is left to the reader.

This is the extended documentation. Every feature lists its data source (which register, which WMI class, which API), its decoding basis, what it displays, and **what it deliberately does not do**. Jargon is explained in parentheses on first use. The document itself is guarded by machine checks (chapter 13): the version, test count, download links, byte sizes, and the first changelog entry all fail the build if they fall behind.

> **Document scope (v3.10).** The Traditional Chinese edition ([README.md](README.md)) is the reference text and was expanded in this release to roughly 80,000 characters. The sections added in that pass (3.7–3.12, 6.11–6.18, 7.4–7.9, 8.1–8.5, 9.1–9.4, 10.1–10.4, 11.4–11.7, 12.1–12.3, 13.3–13.7, 14.1–14.4, 15.1–15.3, 16.2–16.7, 17.12–17.18, 18.6–18.8, 19.6–19.9, 20.6–20.10, 21.8–21.13) and appendices F–T exist only in Chinese for now. This English edition remains a complete translation of the 25 chapters and appendices A–E, with a condensed changelog; where the two disagree, the Chinese text is correct.

---

## Contents

- [1. What this is](#1-what-this-is)
- [2. Design principles](#2-design-principles)
- [3. Architecture](#3-architecture)
- [4. Download and requirements](#4-download-and-requirements)
- [5. Quick start](#5-quick-start)
- [6. Features in detail](#6-features-in-detail)
- [7. Deep Bench (38-item Run Session)](#7-deep-bench-38-item-run-session)
- [8. Monitoring and measurement tools](#8-monitoring-and-measurement-tools)
- [9. Evidence Lab](#9-evidence-lab)
- [10. Risk and consent gates](#10-risk-and-consent-gates)
- [11. CLI and automation](#11-cli-and-automation)
- [12. Public specifications and documents](#12-public-specifications-and-documents)
- [13. Quality assurance](#13-quality-assurance)
- [14. Privacy and data sovereignty](#14-privacy-and-data-sovereignty)
- [15. Known limitations](#15-known-limitations)
- [16. Building from source](#16-building-from-source)
- [17. Anatomy of the distinctive features](#17-anatomy-of-the-distinctive-features)
- [18. Comparison with other tools](#18-comparison-with-other-tools)
- [19. Defect ledger](#19-defect-ledger)
- [20. Hypothesised vulnerabilities and fixes](#20-hypothesised-vulnerabilities-and-fixes)
- [21. Version iteration analysis](#21-version-iteration-analysis)
- [22. Version history](#22-version-history)
- [23. FAQ](#23-faq)
- [24. License](#24-license)
- [25. Author](#25-author)
- [26. Community, contributions and sponsorship](#26-community-contributions-and-sponsorship)
- [Appendix A: Utility tools](#appendix-a-utility-tools)
- [Appendix B: Settings](#appendix-b-settings)
- [Appendix C: Audit log](#appendix-c-audit-log)
- [Appendix D: Troubleshooting](#appendix-d-troubleshooting)
- [Appendix E: For contributors](#appendix-e-for-contributors)

---

## 1. What this is

XinSpect is a WPF (Windows Presentation Foundation, the .NET desktop UI framework) application built with MVVM (Model–View–ViewModel, separating UI from logic). It integrates the LibreHardwareMonitor sensing engine, an Intel XTU overclocking bridge, NVIDIA NVML/NVAPI GPU control, WebView2 for the embedded browser, and its own WinRing0 fact-reading layer plus the XsRegProbe allow-list read-only driver.

It presents three accounts of the same machine:

1. **What the hardware says**: MSRs (Model-Specific Registers, per-model processor registers), PCI configuration space, SPD (Serial Presence Detect, the EEPROM on a memory module) and TSOD (temperature sensor on DIMM) over SMBUS (the board's low-speed management bus), ATA SMART (the drive firmware's self-reported health attributes) and NVMe health logs, Super I/O registers (the board chip that handles fans, temperatures and serial ports), and CMOS (the small battery-backed memory holding boot settings and the real-time clock).
2. **What the firmware says**: ACPI tables (the hardware description tables the OS consumes, e.g. MCFG, HEST, BERT, SLIT, CEDT, HPET, FADT), SMBIOS structures (the board's and modules' identity papers), and UEFI variables (where boot firmware keeps Secure Boot keys and settings).
3. **What the operating system says**: WMI (Windows Management Instrumentation, including CIM and MSFT classes), the registry, event logs, performance counters, ETW (Event Tracing for Windows), and the X509 certificate stores.

When two sources disagree, the cross-reconciliation engine prints the contradiction in red and names both sides. It does not pick a winner.

### 1.1 Why a fourth kind of tool

There is no shortage of single-purpose tools: sensor monitors, identification utilities, drive-health widgets, stress testers, security scanners. The gap is in three places:

- **"Could not read" is drawn as "normal."** A missing temperature sensor shows 0 °C; a failed SMART query shows "good"; an invalid fan counter is converted to 0 RPM. None of these produces an error message, and each produces a clean-looking verification report.
- **Two sources disagree and nobody shows both.** Firmware says Secure Boot is on, the registry says it is off. The CPU reports microcode revision N, Windows recorded N−1. Most tools display one and never mention the other.
- **Numbers have no provenance.** "Link speed" may come from the driver, from a PCIe capability register, or from an OS device property. The three mean different things. A number without a source cannot be defended when it matters.

XinSpect fills those three gaps: six-state reporting, auditable provenance, cross-source reconciliation. It does not compete on sensor count or benchmark scores, and it does not import other tools' readings and present them as its own measurements.

### 1.2 Five readers, five paths

| Reader | Question | Entry point |
|---|---|---|
| Used-PC buyer | Was this machine opened, modified, or capacity-faked? | Chassis intrusion, HPA, SMART failing-now, microcode reconciliation |
| Builder/upgrader | Are the DIMMs in the right channels? Is PCIe negotiating correctly? | SPD direct read, PCIe gap analysis, ReBAR live status |
| Owner of an unstable system | Is it heat, power, a driver, or firmware stealing time? | Bottleneck diagnosis, performance ceilings, DPC latency, SMI |
| Security auditor | What does the trust root and attack surface look like? | Firmware security, platform trustworthiness, Blue Squadron, driver signatures |
| Enterprise operations | Can I get auditable evidence from a script? | CLI `--json evidence`, `--verify-audit`, query language |

### 1.3 Use cases

- **Verification before buying a used machine**: chassis intrusion (SMBIOS event), HPA hidden capacity (Host Protected Area, capacity the firmware withholds from the OS — direct evidence of a refurbished or capacity-faked drive), SMART failing-now, microcode cross-check, driver signature audit, Defender exclusion audit, USBSTOR traces, non-Microsoft root certificates, fake-capacity write verification.
- **Confirming a build or upgrade**: SPD direct read and slot layout, memory channels, negotiated PCIe link speed, USB link speed, display link encoding, ReBAR status.
- **Attributing instability**: bottleneck diagnosis, performance ceilings (thermal/power/current walls), SMI stalls (System Management Interrupts, firmware work invisible to the OS), DPC latency culprits, thread migration, machine-check records.
- **Security posture**: firmware security registers (BIOS_CNTL, SMRAMC), the six Blue Squadron defence lines, BYOVD (Bring Your Own Vulnerable Driver) block-list comparison, deep-access exemption toggle.
- **Long-term monitoring**: live sensors, history playback, CSV export.

### 1.4 What it is not

- **Not antivirus.** It does not block, quarantine, or isolate. Blue Squadron is posture assessment and an event timeline, not endpoint protection.
- **Not a certification tool.** Every output is a fact or a relationship between facts, never a pass/fail verdict. It will not vouch for a machine.
- **Not a cloud service.** The fact-collection layer has zero network APIs (enforced by a source scan). All data stays local.
- **Not a universal hardware database.** It only emits decodes with a specification reference. Unknown models are labelled "not covered," not guessed.
- **Not an overclocking tutorial.** Overclocking and fan control are bridges to existing functionality; they carry risk, and they are wrapped in snapshot, read-back verification, watchdog and automatic restore.

## 2. Design principles

None of these is a slogan. Each has unit tests or a machine check, and each section also states what the check cannot catch.

### 2.1 Six states and lattice propagation

Every fact carries an `availability` field with six values:

| State | Meaning | Typical cause |
|---|---|---|
| `Present` | Read and confirmed | Normal path |
| `Unknown` | Has a value, not vouched for | Stale cache, suspicious source, sources not yet arbitrated |
| `NotApplicable` | This machine has no such hardware | No discrete GPU, no TPM, no battery |
| `NotSupported` | Platform or this build offers no path | Old platform lacks the MSR; Windows has no enumeration API |
| `InsufficientPrivilege` | Needs elevation | Not running as administrator, driver not loaded |
| `ReadError` | Tried and failed, cause unconfirmed | Device timeout, malformed firmware response |

The states form a partial order, strongest to weakest: `Present` > `Unknown` > `NotApplicable` > `NotSupported` > `InsufficientPrivilege` > `ReadError`. **Derived facts take the weakest state of their inputs** (conjunction propagation): all three sources must be `Present` before the conclusion may claim to be usable. Implementation: `Services/FactStateLattice.cs`; tests: `Tests/FactStateLatticeTests.cs`.

The reason is simple: feeding "could not read" into arithmetic as zero is the most common way an honest tool collapses, and it produces no error message. There is no path in the program that fills a gap with 0, 0xFF, a typical value, or the previous reading. Three examples:

- A Super I/O fan counter of 0 or 0xFFFF is invalid per the ITE specification (stopped or unconnected); the program prints "invalid counter" instead of converting it to 0 RPM.
- When both DWORDs of the registry microcode revision are non-zero, the layout is ambiguous; the program prints "ambiguous — not decoded" with the raw hex for manual review.
- An NVMe WCTEMP (warning composite temperature) of 0 means the controller did not provide it; the program says "not provided" instead of comparing against 0.

**Boundary**: the lattice guarantees that propagation cannot launder a weak source into a strong one. It does not guarantee the source is honest. If a hypervisor returns a plausible but synthetic MSR, the lattice will call it `Present` — which is why the platform-trustworthiness page must display HVCI/VBS status alongside MSR-based cards.

### 2.2 Provenance and SpecRef

Every fact's `source` field records where it came from: register address and bit positions (e.g. "MSR 0x8B bits[63:32], read bound per physical core"), WMI class name, ioctl name, or API name. Register decoders must carry a `SpecRef` attribute naming the document, section, register and bits. `SpecRefRegistry` reflects over every public method of every registered decoder; a missing reference fails the test suite. Forty-six decoders are registered and checked, covering SPI flash, chipset security, platform MSRs, PCIe AER, ACPI tables, CMOS, TSOD, Super I/O, platform trust, the PCI knowledge base, PCI BARs, the Super I/O knowledge base, Wi-Fi BSS, display link, SuperIO HWM, CPU topology, SLIT and PMU.

**Boundary (important)**: SpecRef proves there is a citation, not that the citation is correct. The TPM_PT fix in v2.85 is the counter-example: implementation, comments, fixture and documentation were all consistently wrong, the reflection check stayed green, and only a column-by-column re-read of TPM 2.0 Part 2 plus golden vectors found it. Citation and correctness are two different defences.

### 2.3 Self-validating measurements

Measurement features validate the measurement before reporting a number:

- Chess benchmarks use perft (the exact number of leaf nodes at a given depth) as a checksum. perft is a mathematical constant; a different number means the machine computed it wrong, not that it is slow.
- Memory bandwidth is only converted after a known-size workload self-check; if it fails, the raw counter is reported and no bandwidth is claimed.
- L3 miss counts are validated the same way before conversion.
- PMU (Performance Monitoring Unit) programming verifies write/read-back consistency and full restore on every round.
- The statistics engine validates its own classification against synthetic sample sets with known answers (tight samples must classify High, dispersed Low, NaN must not leak); any mismatch rejects the whole run.
- Every GPU measurement is read back and compared byte-for-byte against a CPU reference, so an all-zero output cannot pass as a measurement.

### 2.4 Mutation testing

Pure decoders live in a separate class library, `XinSpect.Decoders` (no WPF dependency, no privileged calls), and Stryker mutation testing runs against it with a score of 82%. It found a real defect: `CpuGeneration.DecodeSignature` documented extended-family carry but did not implement it — harmless for all Family 6 consumer parts, wrong for base family 0xF. Knowledge-table data files (SuperIoKnowledge, PciKnowledge) are deliberately excluded: asserting every string in a lookup table is snapshot duplication, with less protection value than maintenance cost.

### 2.5 Not found is not absent

The query language and local API return matches for items that exist but cannot be read, with the reason. Syntax errors return 400 with guidance. Disguising a query error as an empty result, or an unreadable item as absent, is what this project defines as a honesty violation. The same rule applies to the UI: the Fact Atlas denominator is the compile-time catalogue, not "whatever this scan found"; missing entries are labelled "not collected this run," and the page does not guess why.

### 2.6 Guards before memory

Anything that must be remembered every time will eventually be forgotten. The project turns rules into tests. Eight standing guards are registered in `Services/GuardRegistry.cs`:

1. The version matches in six places (csproj, README badge, About page, FileVersion's first three fields, download links, first changelog entry).
2. The test badge equals `TestSuiteBaseline.ProjectTests`, and the baseline is never below the number of test methods.
3. All three READMEs point their links, badge, and changelog head at the current version.
4. The post-release verification script exists, has a UTF-8 BOM, and actually checks `releases/tags` and `bytes`.
5. Every view is either in the navigation registry or explicitly registered as a standalone window/card.
6. Every fact service is either referenced by production code or named with a reason in `WiringDecisions`.
7. Every runtime-generated fact key is covered by the catalogue or a registered dynamic family.
8. No XAML attribute carries a stale version literal (writing one is allowed; writing an old one is red).

**Boundary**: guards only protect what someone thought to guard. Chapter 19 lists the defects the guards missed, and that list is the real manual for this section.

### 2.7 Performance budget

Cold start ≤3 s (measured 2.7 s); other thresholds live in `Models/PerfBudget.cs`. Self-telemetry is off by default, anonymous, numeric-and-time only. `Tests/PerformanceBudgetTests.cs` keeps a "just one more scan" from quietly doubling startup time.

## 3. Architecture

### 3.1 Components

| Component | Form | Responsibility | Privilege |
|---|---|---|---|
| XinSpect main | WPF (.NET 10, single-file publish) | UI, fact collection, reconciliation, reports, Deep Bench, overclocking | Most features none; MSR/SMART/Security log need administrator |
| WinRing0 | Existing kernel driver (reflection-loads LibreHardwareMonitor's Ring0 module) | Primary MSR / PCI config / physical-memory MMIO reads | Administrator |
| XsRegProbe | Project's own allow-list read-only driver (C source in `XsRegProbe/`) | Fallback reads of allow-listed MSR/MMIO; everything else refused; unloads on process exit | Administrator; the `.sys` must be compiled and signed by the user |
| Intel XTU bridge | net48 out-of-process bridge (embedded automatically in Release) | Hosts the XTU SDK (the legacy WCF stack .NET 10 removed) for CPU overclocking | Administrator |
| Blue Squadron guard | .NET 10 console app (embedded automatically in Release) | Live ETW threat detection, driver baseline comparison, JSON IPC over stdin/stdout | User |

The main program declares `requireAdministrator` in `app.manifest`. That buys "plug in and read MSRs/SMART"; the price is that the entire UI — including the embedded browser — runs with an elevated token. This is a deliberate trade-off, not an oversight; its risk and a suggested fix are V-01 in chapter 20.

### 3.2 Embedding, extraction and integrity

At Release build time, the MSBuild targets `BuildAndEmbedXtuBridge` and `BuildAndEmbedBlueSquadronBridge` publish the two bridges as single files and embed them as resources. On first use they are extracted to dedicated folders under `%LOCALAPPDATA%\XinSpect\`: the SHA-256 is checked against the embedded original, and the directory ACL (Access Control List) is tightened to SYSTEM + Administrators with inheritance disabled. If verification fails, the bridge is not executed.

The reason: the program runs elevated while the extraction folder lives in the user profile. If it reused an existing copy based only on file size, a medium-integrity process of the same user could plant a same-length malicious executable and wait for it to be launched elevated. Debug builds do not embed; they search upward through project outputs instead.

Residual risk: a small TOCTOU (time-of-check to time-of-use) window exists between verification and launch. The ACL already excludes non-administrator writes, so an attacker who could exploit it already owns the machine. Rated low; see V-09.

### 3.3 The fact model

Every fact is a `HardwareFact`:

| Field | Meaning |
|---|---|
| `key` | Stable identifier (e.g. `msr.0x8b`, `spd.slot0.size`); the shared language of the catalogue, query, diff and coverage reporting |
| `category` | Domain (processor, memory, firmware security, …) |
| `name` / `value` / `numericValue` / `unit` | Human-readable name and value; numbers and units are stored separately so "12.5 GB" is never sorted as a string |
| `source` | Provenance (register address, WMI class, API) |
| `trust` | `Measured` (this tool measured it), `Reported` (system/firmware says so), `Derived`, `Unknown` |
| `availability` | One of the six states (see 2.1) |
| `measuredAtUtc` | Sample time (UTC) |

Time capsules, the query language, the local API, and HTML reports all consume the same model. That is why a new feature plugged into the collection entry point automatically appears in the CLI, diffs, coverage reporting and reports.

### 3.4 Trust boundaries

| Boundary | One side | Other side | Control |
|---|---|---|---|
| User ↔ kernel | WPF main (elevated) | WinRing0 / XsRegProbe | Drivers load only for fact queries; all writes funnel through three `WinRing0Bridge` APIs and are ledgered |
| Main ↔ bridges | Main process | XtuBridge / BlueSquadronBridge | Embedded SHA-256 + ACL; JSON over stdin/stdout; no network ports |
| Program ↔ firmware | Windows APIs | UEFI variables / ACPI / SMBIOS | Read-only; privilege-enable failures are reported honestly, never faked |
| Program ↔ network | Fact layer | External endpoints | Zero network APIs in fact collection (source-scan guard); five user-initiated features on the allow-list |
| User ↔ files | Snapshots/reports | Malicious or corrupted JSON | SHA-256 envelope + byte-level verification on load; malformed input is refused |

### 3.5 Startup and collection flow

1. Parse the command line. CLI subcommands branch before the single-instance logic (no named mutex, no "already running" dialog).
2. Load settings and the audit log (read-only; writes happen after actions).
3. Usermode collection: CPUID, SMBIOS, ACPI, WMI, registry, event logs, UEFI variables (enable privilege first where needed; report failures by name).
4. Driver collection: read MSR/PCI/MMIO through WinRing0 when available; otherwise report the six-state reason and flip to real values once deep access is enabled.
5. Run cross-reconciliation (26 rules) and produce verdict cards.
6. Lazy-load heavy facts on demand (NVMe logs, surface scan, Deep Bench).
7. A failure affects only its own item. The CLI exit code is derived from the six-state tally.

### 3.6 Data flow

```
hardware / firmware / OS ──► fact collection (*FactsService)
                                  │
                                  ├─► HardwareFact (six states + source + trust)
                                  │       ├─► cross-reconciliation (26 rules) ──► verdict cards
                                  │       ├─► time capsule / diffs / HTML report
                                  │       ├─► query language / local API / CLI
                                  │       └─► coverage report (catalogue vs runtime)
                                  │
                                  └─► write audit (write features only) ──► hash chain
```

## 4. Download and requirements

| File | Size | Purpose |
|---|---|---|
| [XinSpect.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.10/XinSpect.exe) | 35.25 MB | Main program (Blue Squadron guard included) |
| [BlueSquadronBridge.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.10/BlueSquadronBridge.exe) | 6.5 MB | Standalone guard process |
| [XinSpectDeploy.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.10/XinSpectDeploy.exe) | 15,360 bytes | One-click installer (three sources: GitHub, Google Drive, China direct line; shows a progress bar, speed and time remaining while downloading) |

The exact byte count of the main executable is published in the Traditional Chinese README and verified after each release; the MB figure here is rounded.

The installer (`XinSpectDeploy.exe`) offers three sources: **1** GitHub latest (fully automatic deploy of the main program plus the guard process), **2** a Google Drive mirror (a shared folder - it opens the browser and tells you what to click), and **3** a China direct line - a directory-style endpoint the installer downloads from itself, with the same progress bar, speed and time remaining as option 1. Which mode a slot uses depends on the endpoint: only a host that really returns the files under a directory over GET is marked direct, and a share page always just opens the browser - it never saves the share page as a fake exe. Mirror links live in `Installer/mirrors.txt` and the installer fetches a fresh copy at startup (3 s timeout, built-in defaults if that fails); a slot with no link is listed as "not available yet", and choosing it explains why and falls back to GitHub. Every download is checked for a PE header, so an HTML page (share page, rate-limit page, Google's virus-scan notice) is rejected, deleted, and reported with what the content actually was.

### 4.1 Version and integrity

- The version has one source: `<Version>` in `XinSpect.csproj`. `Tools/release.ps1` compares csproj, README badges, the About page, FileVersion's first three fields, download links and the changelog head before packaging; any mismatch aborts.
- After publishing, `Tools/verify-release.ps1` re-queries the GitHub Release and compares each asset's size with the byte counts stated in the README.
- The executable is not code-signed, so SmartScreen may warn about an unknown publisher. That is a fact; the documentation does not tell users to disable SmartScreen.

### 4.2 Requirements

- Windows 10 1903 or later, Windows 11 x64 (ARM64 untested);
- .NET 10 Desktop Runtime (not needed with a self-contained build);
- D3D11-compatible device for GPU measurements;
- Administrator for MSR reads, SMART ioctls and the Security event log. Without it, those items report `InsufficientPrivilege` — the program neither fakes success nor silently degrades.

### 4.3 Install and remove

Portable: run `XinSpect.exe`. It does not write to an install directory, modify PATH, or register services. What it leaves behind:

- `%ProgramData%\XinSpect\Audit\audit.json` (audit log);
- `%ProgramData%\XinSpect\Driver\` (only if deep access was enabled);
- `%LOCALAPPDATA%\XinSpect\` (bridge extraction and the WebView2 profile).

To remove: close the program, delete those folders. If deep access was enabled, turn it off in Settings first (stops and deletes the service, removes only its own CA), then delete the files.

## 5. Quick start

1. Run `XinSpect.exe` as administrator. It also runs unelevated: most usermode features work and privileged items are labelled honestly.
2. **Firmware Security**: BIOS write protection, SMRAM lock, reconciliation verdict cards. Enabling Deep Access turns SPI flash and PCIe AER facts into real values.
3. **Storage**: per-drive SMART attributes and failing-now verdicts, NVMe health and WCTEMP, HPA hidden capacity.
4. **Deep Bench**: set a storage root, run Quick or Full across 38 items. Results are raw samples side by side, no weighted composite.
5. **Protection**: Blue Squadron posture and the live threat timeline; the guard-process toggle is here.
6. **Evidence Lab**: create a time capsule, then diff fields, export an HTML report or a raw register snapshot.

### 5.1 The first five minutes

| Minute | Action | What to look at |
|---|---|---|
| 0–1 | Open "My PC" | Any red health items; click through to the reason, not to a zero |
| 1–2 | Open "Firmware Security" | How many verdicts are red or grey; grey means "cannot verify," not "fine" |
| 2–3 | Open "Storage" | SMART current value vs threshold; HPA hidden capacity |
| 3–4 | Create a time capsule | The snapshot's SHA-256 and its "not collected" list |
| 4–5 | Open the coverage report in Settings | Of 1,167 catalogue keys, how many this machine produced and measured |

### 5.2 What elevation changes

| Feature | Unelevated | Elevated |
|---|---|---|
| CPUID, SMBIOS, ACPI, WMI, registry | Yes | Yes |
| MSR (TjMax, microcode, C-states, power limits) | No (`InsufficientPrivilege`) | Yes, once the driver loads |
| SMART / NVMe health | Mostly no | Yes |
| Security event log (1102) | No | Yes |
| Surface scan (logical volume) | Yes (read-only) | Yes |
| Deep access toggle | Refused (no changes made) | Yes |

### 5.3 Three first misconceptions

- **"Everything is zero — is it broken?"** Read the state column. `NotApplicable` means this machine lacks the hardware; `InsufficientPrivilege` means no elevation; `ReadError` is an actual failure. Blank values are by design.
- **"The card is grey, so it is safe?"** No. Grey means unverifiable — any missing input prevents a verdict. Green is the consistent state.
- **"Why no overall score?"** Cross-domain numbers have no common unit (a GPU 200 and a disk 200 cannot be added). The tool gives raw samples, confidence, and limits. If you need a ranking, bring your own weight table and publish it.

## 6. Features in detail

The order follows the left navigation. Each section lists sources first, then what is displayed, then the boundary (what it does not do and why).

### 6.1 Overview group

**My PC (simple-mode home)**. Health, temperatures and hardware inventory on one page for readers unfamiliar with hardware terms. Hidden from the sidebar in advanced mode; still reachable from the command palette. The "conclusion" is backed by facts and six states: a red health item opens the fact, its source and its reason.

**Overview**. Full machine specification and live status: CPU (model, cores/threads, clock, load), motherboard (vendor badge and model), memory (capacity, channels, speed), GPU (model, VRAM), storage (capacity and activity). CPU and board vendors get their logos/badges.

The DIMM layout is drawn from the SMBIOS physical arrangement: solid for populated slots (capacity and speed), dashed for empty ones. Channel letters appear only when the firmware slot naming is parseable (e.g. `ChannelA-DIMM0`); boards that only name `DIMM0/DIMM1` are labelled "channel cannot be determined." The reason for not guessing: a wrong channel label makes the user install memory in the wrong slot.

**Bottleneck diagnosis**. Combines readings scattered across pages to answer "what is holding this machine back right now": thermal wall, power wall, single-thread, memory, storage, GPU, DPC, power policy, MCA (Machine Check Architecture) events, ordered by "look here first." Every judgement includes the raw numbers that produced it and a link to the deeper page. Items not covered by measurement are listed under "not included in this judgement" — unmeasured data is never treated as fine.

**AI review**. Connect Ollama or any OpenAI-compatible endpoint to have a language model comment on the local hardware summary. The prompt is editable; endpoint and key are user-configured. This is one of the few network features, and only runs when invoked. The summary can be previewed; sensitive values are redacted by default.

### 6.2 Processor

Sources: CPUID (usermode), MSRs (per-physical-core through WinRing0), registry.

- **Specification and topology**: family/model/stepping (including extended-family carry, once missing and caught by mutation testing); instruction sets (AVX-512, AES-NI, …); cache hierarchy; die topology (CPUID leaf 0x1F Module/Tile/Die levels; if unsupported, `NotSupported` — it does not fall back to leaf 0xB, which has no die level and would invent a single die).
- **Microcode revision (two-source reconciliation)**: the CPU side reads MSR 0x8B bits[63:32] per physical core; if cores disagree, a per-core list is shown rather than one core's value pretending to be the whole package. The Windows side reads `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\Update Revision`, which has both 8-byte and 4-byte layouts in the field; both are decoded. When both DWORDs are non-zero the layout is ambiguous: "not decoded" plus raw hex.
- **TjMax**: MSR 0x1A2 bits[23:16], °C (the processor's own maximum, not a board sensor).
- **Clock truth**: MPERF/APERF ratio and the turbo ladder, with counters fully restored around sampling.
- **TME / SGX memory encryption**: TME (Total Memory Encryption) support via CPUID leaf 7 ECX bit25, activation via MSR 0x982 bits[3:0], algorithm via bits[7:4] (0 = AES-XTS-128, 1 = AES-XTS-256, others reported as unlisted); SGX via leaf 7 ECX bit30. Unsupported platforms report `NotSupported`.
- **C-state residency**: package-level C2 (0x60D), C3 (0x3FC), C6 (0x3F9), C7 (0x3FA) accumulated microseconds. Read failures or unimplemented states are per-item six-state. The numbers describe sleep, not efficiency.
- **PMU**: capability discovery (CPUID leaf 0xA: PMU version, general-purpose counters per logical CPU and width, fixed counters and width) is read-only and shipped. Programming verification is consent-gated: it writes only the fixed-counter-0 enable bit in IA32_FIXED_CTR_CTRL (0x38D), OR-ing into the existing value, never touching PMI (performance-monitoring interrupt) bits. Each round is enable → read-back consistent → known workload → counter activity → restore → read-back, three rounds aggregated, with `finally` on every path. The UI and results say "multi-round test, not guaranteed usable": comparison with wpr (Windows Performance Recorder) and other references (sandbox plan S5) has not been run.
- **NPU detection**: Intel/AMD/Qualcomm NPUs, driver and estimated compute. The estimate is an upper bound, not measured throughput.

**Performance ceilings** (same group) answers "why doesn't it reach the clock it should": TCC (Thermal Control Circuit) throttle temperature, PL1/PL2 power limits and time windows, current limit (IccMax) and power-supply alerts, turbo ratio limit tables — all read from MSRs, not datasheets. The limit-reason register (MSR 0x1FC) is sticky: it records every wall hit since boot. On top of that, user-triggered per-window wall measurement (baseline / integer / AVX2 / AVX-512) uses APERF/MPERF to measure effective ratio and active cores, and ends in one verdict: thermal wall, power wall, current wall, VRM overtemperature, autonomous P-states, multi-core turbo limit, or "the gap is not in the hardware." Read-only throughout; no sticky bit is written or cleared; if the energy counter fails self-validation, only raw counts are reported, never watts.

### 6.3 Memory

Sources: SMBUS (allow-listed addresses, direct), SMBIOS, WMI, ACPI SLIT, Windows memory APIs.

- **SPD direct read**: tries the PCH SMBus and the processor iMC SMBus, labelling each read with its bus. Decodes manufacturer, serial, module part number, nominal and actual speed, primary and secondary timings (tCL/tRCD/tRP/tRAS and sub-timings). Unreadable buses get a reason; an empty slot is not a fault.
- **TSOD**: TSE2004-compatible temperature (bits[15:4], two's complement, 1/16 °C) at allow-listed addresses 0x18–0x1F.
- **ECC status**, slot layout and channel identification (if it cannot tell, it says so; see 6.1). ECC is reported as enabled/disabled and supported/unsupported; "supported" is never written as "active."
- **Rowhammer**: a risk statement plus a stress probe, not a proper attack. The probe hammers two hot spots inside a self-owned contiguous buffer, verifies xorshift samples (chunk index mixed into the seed) byte by byte, and excludes the hot-spot offsets from verification. It requires explicit consent (UI checkbox plus a service-layer exception on unconsented calls). The UI and result say "unverified": usermode cannot execute clflush, so the probe does not guarantee triggering Rowhammer and its result is not a memory-reliability conclusion. Multi-round mode (10 independent probes) carries the same warning: zero flips does not imply Rowhammer resistance.
- **NUMA**: topology (GetNumaHighestNodeNumber / GetNumaNodeProcessorMaskEx; deliberately not parsing the variable-length GetLogicalProcessorInformationEx structure, whose GROUP_AFFINITY offsets move between Windows versions and would turn a misparse into a "fact"), distance matrix (ACPI SLIT; refused when the declared node count does not match the data), TLB and large-page cost (the same random pointer chase on 4 KB and 2 MB pages; the only variable is page size; "cannot allocate large pages" and "large pages do not help" are separate statements), and cross-NUMA comparison.

### 6.4 Motherboard

Sources: SMBIOS (MSSmbs_RawSMBiosTables), ACPI (GetSystemFirmwareTable), Super I/O config ports 0x2E/0x4E, PCI config space (WinRing0), CMOS ports.

- **Chassis intrusion**: SMBIOS Type 3 offset 12 Security Status. Value 5 means the firmware recorded an intrusion — firmware-level evidence, shown in a warning colour with a suggestion to ask about it. Offset 5 (chassis type: tower, desktop, rack mount, …) is decoded too.
- **BIOS / chipset**: version, date, vendor.
- **Super I/O**: tries both entry sequences on 0x2E/0x4E and reads the chip and vendor IDs (name mapping from coreboot superiotool's ite.c/nuvoton.c, cited). Config mode is exited in `finally` on every path — leaving the chip in config mode is a system risk. HWM sensors: select LDN 4 (environment controller), read the base, then `base+5` (index) / `base+6` (data): fan RPM (ITE formula 1,350,000 ÷ (divisor × count); count 0/0xFFFF is invalid and never becomes 0 RPM), temperature (8-bit two's complement), voltage (LSB 16 mV, labelled "not board-calibrated" — the chip's reading is not the rail voltage).
- **PCI bus 0 inventory**: 32-slot scan, multifunction bit controls function depth; PCI-SIG class codes map to role names (unlisted = "not covered"); each device lists BAR (Base Address Register) address and type. BAR size requires a write probe (write all Fs, read the mask); this tool is read-only on PCI config space, so it does not report sizes.
- **ReBAR live status**: asks Windows which memory range is actually assigned to the device — the effective window, not the capability claim. Deliberately not PCI config space (the ReBAR capability structure lives beyond 0x100, unreachable through legacy CF8/CFC), and needs neither driver nor administrator. Coverage is reported as a fact: BAR sizes are powers of two, so a 12 GB card can only receive an 8 GB window; this is the specification, not a deduction.
- **SPI flash audit (three layers)**: (1) register flags — HSFSTS FLOCKDN/WRSDIS/FDOPSS (bits only, no verdict), FRAP BIOS-region write permissions, PR0–4 protected ranges; (2) the FREG map derives total flash size and BIOS region location; (3) SHA-256 of the BIOS region (single large read first, 4 KB chunks on failure, labelled "hash = readable surface"), comparable against a user-supplied reference image in 4 KB blocks (size mismatch is refused honestly). All of it needs the driver; without it, the known SPIBAR address is reported six-state.
- **ACPI tables**: header checksum verification; MCFG (PCIe ECAM base and bus range), HEST (hardware error sources), BERT (boot error record), SLIT (NUMA distance matrix), CEDT (CXL fixed memory windows, CFMWS fields: BaseHPA / WindowSize / InterleaveWays), HPET (timer present), FADT (PM timer block address). A platform without the table reports `NotApplicable` — no such hardware is not an error.
- **CMOS/RTC**: VRT battery voltage (0x0D), real-time clock (BCD and 12/24-hour decoding), century byte, PC-AT checksum; address 0x70 bit 7 preserves NMI (non-maskable interrupt) and 0x71 is never written.
- **UEFI boot settings**: Secure Boot four-state, AuditMode, DeployedMode, SetupMode (GetFirmwareEnvironmentVariableEx, with SeSystemEnvironmentPrivilege enabled and ERROR_NOT_ALL_ASSIGNED checked).
- **POST codes**: read from I/O port 0x80; 0xFF and 0x00 are not decoded (on most chipsets they mean "no new POST code" or an idle bus; decoding them invents progress).

### 6.5 Storage

Sources: SMART_RCV_DRIVE_DATA ioctl (proxied by disk.sys — deliberately not ATA_PASS-THROUGH, which was measured hanging an IRP; the class-driver path is long-proven and cannot wedge a user's disk), NVMe Storage Query Property, WMI Win32_DiskDrive.

- **SMART attributes and thresholds (failing-now)**: READ DATA (features 0xD0) for 30 attributes (ID, current, worst, raw six bytes); READ THRESHOLDS (features 0xD1) for the threshold table. "Current ≤ threshold and threshold ≠ 0" is failing-now, printed per item as "current X ≤ threshold Y." Threshold 0 means no threshold per the SMART specification and is excluded. Reallocated sectors (0x05), pending sectors (0xC5) and uncorrectable sectors (0xC6) get separate health verdicts.
- **NVMe**: full health log (page 0x02) — composite temperature, available spare, percentage used, data units read/written; WCTEMP warning (Identify Controller offset 0x14A, u16 compared with composite temperature; 0 = not provided); error log (page 0x01); power-state table (per-state power and entry/exit latency) compared with a measured "first 4K read after N ms idle" — only when both are the same order of magnitude is the delay attributed to power saving.
- **HPA hidden capacity**: ATA IDENTIFY maximum LBA (words 100–103) vs Win32_DiskDrive.Size — if the OS sees less than the firmware declares, HPA is active; converts to hidden sectors and GB. Direct evidence of a refurbished or capacity-faked drive. DCO (Device Configuration Overlay) needs vendor-private commands (no public usermode path), so it is `NotSupported` with the reason.
- **Surface scan**: sequential reads block by block (1 MB default, configurable), timing each; over 100 ms is slow, a read failure is an error. SMART is the firmware's account; this is what the program itself read. Opening `\\.\C:` needs no administrator and is read-only.
- **Fake-capacity write verification** (H2testw-style): writes a reproducible xorshift pattern (chunk index mixed into the seed) to a chosen limit or until full, flushes, reads back byte by byte, then deletes the temp file. "Cannot write" or "read-back mismatch" is direct evidence of a fake-capacity card (a 512 GB label on 8 GB of flash) or degrading media. Requires explicit consent: the write volume is large and may hasten a dying medium. Never wired into any automatic flow.
- Power-on hours and age estimate (SMART Power-On Hours + manufacturing date heuristic), QD (queue depth) performance curve, capacity/firmware/serial, NVMe identify data.

### 6.6 Graphics

Sources: NVML (NVIDIA Management Library), NVAPI, D3DKMT, EDID (Extended Display Identification Data), registry.

- **NVML**: temperature, clocks, power (mW), fan, thresholds, **retired pages** (nvmlDeviceGetRetiredPages_v2, single- and double-bit ECC — NAND defect retirement; the test machine's card does not support the report, so it is `ReadError` with the reason).
- **GPU measurement**: raster fill rate (full-screen triangles + sampling), texture sampling (single/eight-tap), H.264 encode (Media Foundation SinkWriter), compute pipeline (D3D11 buffer SRV). Every GPU output is read back and compared byte-for-byte with a CPU reference, so an all-zero output cannot pass as a measurement.
- **HDR capability**: EDID CTA-861 HDR static metadata (luminance range and flags) plus the Windows HDR toggle via DisplayConfig; unreadable means unknown, never a guess.
- **Display link truth**: connection technology, actual pixel clock, colour encoding (RGB / YCbCr 4:4:4 / 4:2:2 / 4:2:0), bit depth, required data rate. When bandwidth is short, the driver silently drops chroma while the Windows UI still says 4K144 — this page shows the live state.
- **TDR settings**: TdrLevel/TdrDelay/TdrDpcDelay registry values; when unset, the Windows defaults (Level 3, 2 s) are labelled as defaults, never as configured.
- **Overclocking**: NVML power/fan/temperature monitoring plus NVAPI clock offsets; the rule is snapshot → read-back verification → watchdog → automatic restore.

### 6.7 Networking

Sources: WMI (MSFT_NetAdapterStatistics, Win32_NetworkAdapter, MSFT_NetAdapterChecksumOffload/RSS), wlanapi, registry.

- **Interface statistics**: interfaces with non-zero errors or discards are listed individually (RX/TX errors, RX/TX discards) — the first fingerprint of a degrading driver, cable, or switch port. All-zero is explicitly "clean," distinct from "could not read."
- **MAC OUI mapping**: a subset of IEEE-registered prefixes (Intel, Realtek, ASUS, GIGABYTE, Microsoft Hyper-V virtual, VMware virtual, Apple, NVIDIA, …); unlisted = "not covered," malformed = "cannot parse." An OUI is not proof of ownership.
- **Wi-Fi**: RSSI (with interpretation from -30 excellent to -85 weak; intermediate values get no invented grade), channel (WLAN_BSS_ENTRY centre frequency per IEEE 802.11 Annex E — 2.4 GHz arithmetic, the ch14 special case, separate formulas for 4.9/5/6 GHz; a frequency outside the arithmetic returns null), authentication type (WPA2/WPA3 Enterprise/Personal, …), BSSID. A disconnected interface says "not connected," never the same as "no interface."
- Offload state (Checksum Offload, RSS — defensive property reads; an absent property says "property not provided," not "disabled"), adapter advanced properties, speed test, network latency measurement.

### 6.8 Security (Firmware Security and Protection)

Sources: MSRs, PCI config space, UEFI variables, WMI (Win32_DeviceGuard, Win32_Tpm), X509Store, event logs, registry, user-supplied list files.

**Firmware security registers** (each with a verdict; adverse ones in warning colour):

- BIOS_CNTL (0xDC): SMM_BWP, BLE, WE — combined BIOS write-protection verdict;
- SMRAMC (0x88): D_LCK, D_OPEN — SMRAM (System Management RAM) lock state; the illegal "locked with D_OPEN=1" combination is caught by a reconciliation rule;
- ME status (HFSTS1): decoded only after confirming PCI 0:16.0 is Intel HECI;
- IA32_FEATURE_CONTROL (0x3A): Lock and VMX;
- IA32_DEBUG_INTERFACE (0xC80): ENABLE / LOCK / DEBUG_OCCURRED (forensic clue: a debugger has attached to this machine);
- SPI flags and region permissions (see 6.4).

**Cross-reconciliation verdict cards**: 26 rules (defined in `FactRelationRules.All`, each with a SpecRef and a false-positive condition) check semantic and pipeline consistency across the same fact set. Three families: hardware semantics (microcode, SMRAM, the UEFI state machine — SB=1 with undeployed SetupMode keys, etc.), pipeline consistency (BIOS_CNTL ↔ write surface, FRAP ↔ exposure, SPI ↔ MMIO backend, MSR ↔ backend, microcode Windows ↔ CPU), and source crossing (Secure Boot two-source, UEFI variable vs registry platform). Verdicts: consistent (green) / contradiction (red, both sides named) / unverifiable (grey, any input missing). A real contradiction was caught on hardware: SMM_BWP=1 conflicting with the SMRAM lock.

> The external form `Rules/builtin.json` is historical and **is not shipped** (csproj does not include `Rules/`). Production uses the runtime rule set of 26; coverage reporting reads the runtime set on purpose, to avoid "file missing → 0 rules → everything covered." Chapter 19 records the documentation defect.

**Platform trustworthiness**: hypervisor-present bit (CPUID 1 ECX bit31) and signature (0x40000000, read only when bit 31 is set — an unsupported leaf returns the highest standard leaf and invents a vendor), VBS/HVCI (Win32_DeviceGuard: "configured" ≠ "running"; only Running counts), kernel-mode code integrity options (NtQuerySystemInformation 103), Invariant TSC. When VBS is running, the page states plainly that Windows is a Hyper-V partition, that MSRs may be intercepted, masked, or synthesised, and that MSR cards are reference only.

**BYOVD per-driver comparison**: loaded kernel modules against Microsoft's recommended driver block rules. The XML is supplied by the user (downloaded from Microsoft into `Rules\byovd-blocklist.xml` or passed on the CLI); zero network access. Two comparisons: filename (case-insensitive) and SHA-256/SHA-1 of the actual file (hash-cached). A hit lists the module path and the rule basis. The meaning is "this is an attack surface on Microsoft's recommended block list," not "you are infected" — most such drivers are legitimate vendor tools (overclocking, lighting, diagnostics) whose arbitrary read/write capability can be abused. Actual blocking belongs to Windows' vulnerable driver blocklist.

**Security forensics**:

- Defender exclusions (MSFT_MpPreference ExclusionPath/Process/Extension) listed individually — an exclusion means "scanning never looks here"; zero exclusions is explicitly "full coverage";
- Event-log clearing (Security log 1102): history count and most recent time; needs elevation, and "no privilege" is distinct from "query failed";
- Non-Microsoft local trust roots: X509Store Root/LocalMachine certificates whose subject is not Microsoft — the risk surface for Superfish-style MITM/monitoring certificates; subject and validity are listed without a malware verdict;
- USBSTOR traces: device installation records under `Enum\USBSTOR`, including devices long removed;
- Driver signature audit: Win32_PnPSignedDriver IsSigned, with unsigned entries listed.

**Blue Squadron (Protection)**: six defence lines — DMA and memory protection, firmware and boot chain, CPU mitigations and memory protection, storage and data, drivers and adversarial, system attack surface — each with a score, severity and summary, plus concrete hardening advice. A live ETW threat timeline (driver loads, suspicious processes). The guard is embedded in the main program; a toggle starts and stops it and records the choice in settings; when off, the read-only posture assessment still works.

**Deep access exemption**: generate a dedicated self-signed CA (RSA-4096, CA=TRUE, stored in an ACL-restricted ProgramData path) → install it into Root + TrustedPublisher (only this one, no machine-wide test-signing) → install and start the XsRegProbe service. Disabling stops and deletes the service and removes only its own CA thumbprint (if the certificate file is missing, it does not scan-and-delete). No changes are made without elevation. Once enabled, driver-dependent facts such as SPI flash and PCIe AER become real values without a reboot.

### 6.9 System and software layer (the software side of the Evidence Lab)

Sources: WMI, registry, event logs, LSA (Local Security Authority), COM (WUA), psapi.

- **Windows Update history**: WUA COM (Microsoft.Update.Session) — latest entry (title + install date), total count, last 30 days, failure/abort counts with the most recent title; no date means no date, empty history is "0 entries" not "unreadable"; COM unavailable (service stopped) is six-state.
- **Service inventory**: Win32_Service — total, running, automatic, disabled, **services outside the system directory** (image path not under `\Windows\`, quote-aware parsing, first three examples; this is third-party persistence surface, not a security verdict by count).
- **Event log summary**: System log, last 7 days, critical + error, top three "source event-ID × count"; stops at the 7-day boundary instead of scanning the whole log; exports canonical JSON.
- **Audit policy**: LSA LsaQueryInformationPolicy (PolicyAuditEventsInformation, read-only) — the master switch and nine category levels described per category (0 unset is omitted; out-of-spec levels print "level N"); x64 structure offsets computed by hand.
- **Machine policy file**: Registry.pol existence and last-write fingerprint — fingerprint only, no binary parsing; absent = `NotSupported` (it may simply never have been set by domain or local policy), not an error.
- **Optional features**: Win32_OptionalFeature — Hyper-V Hypervisor / Virtual Machine Platform / WSL / Containers as enabled/disabled/absent (mirroring InstallState); **a feature WMI does not report is "not reported," not "disabled."**
- **Kernel module list**: psapi EnumDeviceDrivers — modules under `\Windows\` are counted only (signature coverage comes from Win32_PnPSignedDriver); **every loaded module outside the system directory is checked with wintrust Authenticode (DRIVER_ACTION_VERIFY)** — failures carry the raw NTSTATUS, files that cannot be verified are named, passes are counted.
- **Boot options**: SystemStartOptions parsed verbatim — kernel debugging (DEBUG/DEBUGPORT), test-signing (TESTSIGNING); absence of the keyword means "not enabled" (a present negative), while a failed read is `ReadError`; the raw string is included for audit.
- **Boot timing**: Diagnostics-Performance Event 100 BootTime (milliseconds, measured by Windows; interpreted, not graded); last boot from Win32_OperatingSystem.LastBootUpTime. Missing event = `NotSupported`, failed query = `ReadError` — two different "nothings."
- **USB topology**: Win32_USBControllerDevice dependency pairs — controller count, device count, busiest controller (a fingerprint for power/bandwidth conflicts); WMI only exposes one level, deeper hub trees are not guessed.
- **Camera enumeration**: PNPClass Camera/Image, names and states mirrored — "present but in Error" and "absent" are different.
- **Enterprise storage**: iSCSI (MSiSCSI service state), MPIO (service present), FC HBA (WMI root\wmi), NVMe-oF (no public detection API, labelled "detection path not implemented") — no hardware is `NotApplicable`, no detection path is `NotSupported`, neither is an error.

### 6.10 The reconciliation engine

26 rules in three families:

- **Hardware semantics**: microcode consistency (Windows vs CPU), the illegal SMRAM locked + D_OPEN combination, the UEFI state machine (Secure Boot=1 with undeployed SetupMode keys; Audit × Deployed mutual exclusion);
- **Pipeline consistency**: BIOS_CNTL ↔ BIOS write surface, FRAP ↔ BIOS exposure, SPI controller ↔ MMIO backend, MSR ↔ MSR backend, MCHBAR base ↔ host bridge inventory, TjMax ↔ MSR backend, HVCI ↔ environment decision, SPI hash ↔ map, hash ↔ MMIO backend, PCIe AER scan ↔ ECAM;
- **Source crossing**: Secure Boot two-source (UEFI variable vs registry), UEFI variable present vs registry denial.

Methodology note: when "A is absent" and either side may be absent, one rule cannot cover both directions (the engine guard turns a missing key into `Unverifiable`) — so such cases are split into directional rules that each guarantee one `Present` side. Pipeline rules where "backend fact absent = contradiction condition" deliberately omit the key from their input list and decide inside the rule with TryLookup. Rules are defined in code with a SpecRef and false-positive condition; behavioural equivalence is pinned by 68 scenario tests at the Relation layer.

**A verdict is not a score**: the three verdicts describe the relationship between two accounts of the same fact, not whether the machine is safe. A green rule means that pair is consistent, not that everything is; a red rule means the sources disagree, not which one is right. That is exactly where a human has to look.

---

## 7. Deep Bench (38-item Run Session)

Deep Bench is the centralised measurement surface: choose the Quick or Full profile, set a storage root and a temp budget, and run items one by one with live progress.

**Reading rules, stated up front**: results are raw samples, confidence (High/Medium/Low/Insufficient) and limitations, side by side. There is no weighted composite. Cross-domain rankings do not hold (a GPU 200 and a disk 200 have no common unit). Samples from different configurations are rejected as pooled data — ladder metrics (STREAM kernel × threads, cache working sets, disk queue-depth ladders) are different configurations at each point, so pooling is forbidden and points are listed individually. All 38 items are registered; runnable and deferred items are both shown, and nothing unrun is described as measured.

### 7.1 Item list

| Item | Content |
|---|---|
| cpu.aes-sha | AES-NI and SHA-NI throughput |
| cpu.load-use / ilp / branch | Load-to-use latency, ILP (instruction-level parallelism), branch latency and misprediction |
| cpu.branch-matrix | Branchy matrix multiplication |
| cpu.rdrand-rdseed | Random-instruction throughput |
| cpu.pmu-topdown | Intel PMU top-down pipeline attribution (read-only sampling) |
| cpu.core-latency | Core-to-core latency matrix |
| cpu.core-to-core | Core-to-core transfer bandwidth |
| cpu.smt-sibling | SMT sibling contention |
| cache.bandwidth / latency | Cache ladder bandwidth and latency (L1/L2/L3/DRAM) |
| cache.lock-scaling | Lock-prefixed instruction scaling |
| numa.* | NUMA/TLB/large-page comparisons (TLB working-set scan with coprime strides) |
| memory.dram-mapping | DRAM address-mapping inference (sequential-stride curve; inference only, no claim) |
| memory.bandwidth | Multi-threaded STREAM bandwidth |
| memory.numa-tlb-largepage | Three independent sub-verdicts (large-page items report "not run" when the local Administrator lacks SeLockMemoryPrivilege) |
| ux.synthetic-workloads | Synthetic JSON round-trip, SHA-256, data conversion sequences |
| gpu.raster-texture | D3D11 full-screen triangle fill rate plus single/eight-tap texture (readback compared byte-for-byte with CPU reference colours) |
| gpu.codec-throughput | Media Foundation H.264 encode (in memory, no camera) |
| gpu.compute-pipeline | D3D11 compute pipeline (buffer SRV) |
| storage.composite | Temp-file flush → read-back → GPU FNV-1a → read-back compared element by element with CPU |
| storage.qd-ladder | Disk queue-depth ladder |
| storage.io-gpu-pipeline | Storage-to-GPU pipeline |
| audio.wasapi | WASAPI signal level (shared-mode silent sample; no recording) |
| gauntlet.multi-domain | CPU/memory/storage concurrent windowed sampling with early/late retention |
| confidence.engine | Statistics-engine self-audit (seven known-answer synthetic sets, case by case; any mismatch rejects the run) |

### 7.2 How to read it

- **Quick vs Full is not "accurate vs inaccurate"**; it changes sampling length, repetition and item mix. Quick answers "roughly what class is this machine"; Full is suitable for a verification report.
- **Every item carries confidence**: `Insufficient` means the samples cannot support the item's conclusion (timer resolution, sample count, failed self-validation). Do not cite an `Insufficient` number.
- **early/late retention**: throughput in the first versus the last window of a long run. A large drop suggests thermal or power limits; it is a fingerprint, not a root cause.
- **Cross-machine comparison** requires the same item, settings and version. Algorithms change between versions; numbers are not comparable across them.
- **No ranking**: the tool produces no total score. If you need one, choose the weights yourself and publish the weight table, or the ranking cannot be reproduced.

### 7.3 Resources and safety

The high-load warning is explicit: CPU, memory, GPU and storage items create `XinSpect.deepbench.tmp` and `XinSpect.loop.tmp`, never touching existing files; they are deleted on completion, exception or cancellation, and free space is checked first. History stays in the local settings folder.

Residual risk: the temp filename is fixed and the process is elevated. If the storage root is a user-writable directory, a medium-integrity process of the same user could theoretically pre-create a symbolic link. Exploitability has not been tested; see V-13.

## 8. Monitoring and measurement tools

**Sensors**. LibreHardwareMonitorLib engine; a full table of every sensor: temperature, clock, voltage, fan, load. Mini floating window and tray mode; threshold alerts (configurable, banner + balloon); CSV export; one-second updates. A sensor has no notion of a "correct" value — it reports what the board and chip vendors defined; the tool transports and labels it.

**History playback**. Weeks of temperature/load trends and statistics. History files carry a version check: a different field count causes the whole file to be ignored rather than misaligned into fake readings.

**Health**. A combined temperature/load/capacity health summary with an entry to the surface scan. It is a rule-based summary, not a model; every deduction traces back to a fact.

**Performance**. Benchmarks (chess perft checksum — the leaf-node count is a mathematical constant, so a different number means the machine computed it wrong), burn-in, cache latency, disk performance.

**Compute map**. Visualisation of CPU/memory/GPU/storage/NPU compute dimensions. It shows relative relationships within a domain, never across domains.

**WPF pipeline test**. Fill rate, 3D geometry and text rendering through WPF's software rasteriser — it measures the WPF pipeline, not the GPU, and complements the D3D11 GPU measurements.

**Frame-time monitor**. Real frame times and 1% low (the average of the slowest 1% of frames, more revealing than the mean) for any process, via ETW, without injecting into the target.

**DPC latency**. Identifies the drivers behind audio crackle and input stalls (ETW). DPC (Deferred Procedure Call) is work a driver queues after an interrupt; one long DPC stalls the whole system.

**Thread migration**. How often threads bounce between cores and which cache layer each hop loses. ETW context-switch events (no driver, needs administrator); each thread's previous core is recorded, a change is a migration, attributed at four topology levels: same physical core (SMT sibling, L1/L2 intact), same last-level cache, across last-level cache (L3 must be refetched), across NUMA (memory becomes remote). Counters are aggregated on the fly with no raw event retention, and the measurement's own threads are subtracted. Processes are ranked by migration rate, not absolute count. No verdict: migration rate depends on workload, not defect.

**Invisible stalls (SMI)**. System-management interrupt counts plus package/core C-state residency. SMIs are handled by firmware in a mode the OS cannot see: Task Manager stays at 0% while audio stutters. Hardware leaves only a count, not per-event duration, so the tool reports frequency and never multiplies by a guessed duration.

**NVMe power states**. The drive's declared power-state table (per-state power, entry/exit latency) versus a measured "first 4K read after N ms idle." Only when both are the same order of magnitude is the delay attributed to power saving.

**Resizable BAR live status**. Whether the GPU's memory window is actually opened. ReBAR needs BIOS support, driver support, CSM off, pure UEFI boot and a GPT boot disk; miss one and it does not apply, and Windows tells you nowhere. The tool asks Windows for the range actually assigned — the effective value, not the capability claim — with no driver or administrator.

**Large pages and address translation cost**. The same random pointer chase over the same working-set size on 4 KB and 2 MB pages; the only variable is page size, and the difference is the walk cost. "Cannot allocate large pages" and "large pages do not help" are separate statements.

**L3 misses and real DRAM traffic**. Architectural performance events count actual memory trips, validated first against a known-size workload; if validation fails, only raw counts are reported.

**Statistical sentinel**. Theil–Sen robust slope (trend with 95% confidence interval), CUSUM change points, and Pearson first-half/second-half correlation. It describes the series, never a causal claim about hardware health; insufficient data or missing sensors are reported honestly.

## 9. Evidence Lab

**Time capsule**. A whole-machine fact snapshot: every source's facts in one JSON (the model in 3.3), with a SHA-256 integrity envelope (byte-level hash of canonical UTF-8 JSON) and an anonymous machine identity. Sensitive values are redacted by default; "keep sensitive values" must be chosen explicitly. Cross-snapshot comparison refuses different machines (different anonymous IDs) so that two machines' differences cannot be mistaken for hardware changes. Changes are classified as added / disappeared / changed / unchanged, and **a source that cannot be read this time is listed as disappeared, never back-filled with the old value**. Asset lifecycle events are classified by key prefix (memory mem./spd., CPU cpu., GPU gpu., storage disk./nvme./smart., board board.); everything else is listed as "state" — a firmware setting change is not a hardware change. Comparisons are written to the audit log (hash chain: contiguous Sequence, chained PreviousHash, recomputed per entry; editing a middle entry fails).

**Raw register snapshot** (`.xinraw`). Raw bytes of PCI security registers, whole ACPI tables, MSRs, SPIBAR and MCHBAR; SHA-256 envelope, verified byte by byte on load — tampered or corrupted files are refused. Volatile bits (SMI counters, DEBUG_OCCURRED, HSFSTS status) are marked volatile. Raw snapshots are not anonymised; check before sharing.

**HTML report**. Single self-contained file with no external resources, a custom five-character escaper (WebUtility.HtmlEncode turns ° into a numeric entity and hurts readability, so the project writes its own), styles for unreadable/warning rows, and a trailing SHA-256 that `Verify()` recomputes offline over the whole document with values blanked.

**corpus contribution pack** (format v1 skeleton). Accepts only redacted snapshots (sensitive-retained versions are refused), excludes identity keys one by one (serial/uuid/mac/asset.tag/system.product/user., stricter than the Sensitive flag as a second lock), and carries a SHA-256 envelope. The upload path is deliberately unimplemented; reopening it requires four conditions: per-upload consent, local preview, a data-sovereignty statement, and a server that does not rewrite the data.

**Four kinds of file**:

| Format | Contents | Anonymisable | Diffable | Shareable |
|---|---|---|---|---|
| Time capsule `.json` | Fact set | Yes (redacted by default) | Yes | Carefully; read the redaction list |
| Raw snapshot `.xinraw` | Raw register bytes | No | No | Not recommended |
| HTML report `.html` | Human report + hash | Yes | No | Yes (hash verifiable) |
| corpus pack | Redacted snapshot | Yes | Yes | Yes (upload path not open) |

## 10. Risk and consent gates

The table lists every operation that writes or carries risk. Everything else is read-only.

| Feature | What it writes | Protection |
|---|---|---|
| Rowhammer stress probe | High-frequency read/write inside a self-owned buffer | Consent gate (UI checkbox + service-layer exception for unconsented calls); "unverified" label; no automatic wiring |
| Multi-round stress mode | Same, 10 independent probes | As above; "zero flips does not imply Rowhammer resistance" stated explicitly |
| PMU programming verification | IA32_FIXED_CTR_CTRL enable bit (OR-ed into the existing value, PMI bits untouched) | Consent gate; per-round `finally` restore; "multi-round test, not guaranteed usable"; S5 comparison not run |
| Fake-capacity write verification | Large writes to the target disk (to a limit or full) | Consent gate; danger statement (may hasten a dying medium); file deleted afterwards; no automatic wiring |
| Surface scan | Nothing (pure read) | Read-only; cancellable; no administrator needed for logical volumes |
| Deep access toggle | Installs a self-signed CA and a driver service | Refused without elevation; disabling removes everything (service + only its own CA); no machine-wide test-signing |
| CPU overclocking | Ratio/voltage (through the XTU bridge) | Snapshot → read-back verification → watchdog → automatic restore |
| GPU overclocking | Clock offset / power / fan | As above |
| System fan control | Super I/O fan registers | One-click restore; shares the sensing engine |
| System boot repair | Runs SFC/DISM/CHKDSK | Runs and records output; Microsoft's own tools |

**Three layers of consent**: the UI checkbox is only the first; the service layer throwing on unconsented calls is the second; the write audit (time, target, caller, value, success/failure for every MSR/PCI/I/O write) is the third. They serve different purposes: the first tells the user what will happen, the second prevents internal misfires, the third answers "what exactly was written after I said yes."

**What "read-only" means**: it means not changing device state. Some registers have side effects when read (read-to-clear flags); those paths are labelled individually. The EC (Embedded Controller) port is excluded because "read-only" there still requires writing the command port; the project closed that path (see chapter 15).

## 11. CLI and automation

```
XinSpect.exe --json evidence [--query <query>] [--out <file>]
XinSpect.exe --compare-flash <reference image> [--out <file>]
XinSpect.exe --verify-audit [log path] [--out <file>]
XinSpect.exe --errors [--out <file>]
XinSpect.exe --disk-usage [--deep]
XinSpect.exe --sbom [--out <file>]
XinSpect.exe --integrity-baseline
```

Exit codes: 0 = all facts `Present` (or the audit chain is intact); 2 = some facts are non-`Present` / the chain is broken; 1 = fatal. The CLI branches before single-instance logic — no named mutex, no multi-instance dialog — so it is script- and scheduler-safe. `--verify-audit` recomputes the audit hash chain entry by entry and prints `fileExists` / `chainValid` / `checkedEntries` plus the break reason (a missing log is labelled as such, never as "verified").

### 11.1 Query language

One clause per line; `#` starts a comment; a value containing spaces runs to end of line. Seven fields (key / category / source / value / availability / since / until), three operators (`=` exact, `~=` case-insensitive substring, `^=` prefix); availability accepts aliases (ok / error / no-permission). Clauses are ANDed. Syntax errors throw `ParseException` with correction guidance. Example:

```
key ^= spi.
category = firmware-security
availability = error
since = 2026-10-01
value ~ FLOCKDN
```

**Not SQL**: no joins, no OR, no aggregation. The semantics are "select matching rows from this machine's fact set," not "ask a database a new question." The limitation is deliberate: a language that can aggregate arbitrarily would make "where did this number come from" unanswerable again.

### 11.2 PowerShell and the local API

The `XinSpect.psm1` module wraps the CLI (`Get-XinSpectEvidence`); the file must carry a UTF-8 BOM, guarded by a machine check.

The local API handler: `GET /api/facts` returns all facts (canonical JSON, the same shape as a time capsule); `POST /api/query` takes the full query language (a single dotted token keeps the legacy prefix meaning; nonsense without an operator returns 400 with guidance). It is designed as loopback, read-only and anonymously identified. The HTTP listener shell is deliberately not started — the decisions register records the decision and its conditions, because opening a listener adds local attack surface and is the user's call. Headless output is the CLI's job, and out-of-app audit verification is `--verify-audit`.

### 11.3 Automation examples

```powershell
# All non-Present facts as JSON for the next stage
.\XinSpect.exe --errors --out .\errors.json

# Only firmware-security facts that failed to read
.\XinSpect.exe --json evidence --query "category = firmware-security`navailability = error" --out .\fw-errors.json

# Verify the audit log has not been edited (0 = intact, 2 = broken)
.\XinSpect.exe --verify-audit
if ($LASTEXITCODE -ne 0) { Write-Error "audit chain is not intact" }
```

## 12. Public specifications and documents

| Document | Content |
|---|---|
| `docs/spec/snapshot.schema.v1.json` | Time-capsule JSON Schema (2020-12): top level and fact definitions, six-state and trust enums; numericValue/unit conditional omission; machine-reconciled key by key against actual serialisation |
| `docs/spec/query-language.v1.md` | Query language v1: seven fields, three operators, alias table, "not found ≠ absent," `ParseException` instead of silence |
| `docs/spec/METHODOLOGY.md` | Fact-source contract: five honesty rules, five measurement-path layers, reconciliation, mutation testing, performance budget |
| `docs/MEASUREMENT-METHODOLOGY.md` | Measurement methodology: unmeasured is a state, not zero; no pooling; monotonic vs wall clock; documented thresholds; flaky handling |
| `docs/spec/LIMITATIONS.md` | Public limitations: seven design decisions, six capability boundaries, two environment dependencies |
| `docs/EC-RISK-ASSESSMENT.md` | EC port risk assessment: transaction interleaving, burst corruption, delayed symptoms; closed as not implemented, with reopening conditions |
| `docs/PMU-SANDBOX-PLAN.md` | PMU sandbox plan S1–S5 (minimal write/read-back, known-workload check, three clean-ups, coexistence with wpr) |
| `docs/DATA-SOVEREIGNTY.md` | Data sovereignty: locations, zero-network-API machine check, anonymisation, deletion is final |
| `docs/DEPLOYMENT.md` | Deployment and enterprise operations: forms, CLI, data locations, upgrade/rollback, HVCI limits |
| `docs/ITERATIONS.md` | Full iteration ledger: 60+ batches, per-round content, test counts, commit hashes |
| `docs/ROADMAP-G6.md` | G6 roadmap and honesty boundaries (EC direct writes, Rowhammer, PMU each have gates) |
| `docs/feature-backlog.md` | Inventory of 110 candidate features |
| `docs/FEATURE-REFERENCE.md` | Feature and data reference (Chinese): what each of the 63 screens answers, and the meaning, calibration and research use of 139 domains and 1,044 fact keys; includes measurement design and a reporting checklist. 47,291 Han characters, every key covered |

**Code beats prose**: the rule is "the program is fact, the document is claim." When they conflict, fix one or the other, but never leave the conflict standing. Every document above has a machine check (`PublicSpecTests`, `DataSovereigntyTests`, `FactKeyCatalogTests`, …); drift turns the suite red. Known remaining drift is listed in chapter 19.

## 13. Quality assurance

- **Unit tests**: 6,580, all passing (the badge and `TestSuiteBaseline.ProjectTests` share one source). Coverage includes golden vectors per decoder (hand-computed at byte level), six-state behaviour per service, every reconciliation rule scenario by scenario (68), and every query-language clause.
- **Mutation testing**: Stryker over `XinSpect.Decoders`, score 82%. It caught the documented-but-missing extended-family carry.
- **SpecRef coverage**: reflection checks every public method of every registered decoder for a specification citation (document, section, register, bits). Forty-six decoders are registered; a missing reference is red.
- **Property tests**: FsCheck on masking-difference never-in-list and MCFG encode/decode round-trips — it caught a hand-computed mask error, 0xA007 vs 0xA807.
- **Differential tests**: SMBIOS memory decoding compared with Microsoft's Win32_PhysicalMemory (two implementations of the same table: module count, total capacity, nominal and actual speed). A stale fixture (RAM replaced) turns red with a message telling you to check the fixture first.
- **Runtime reconciliation**: the app and the CLI run the same read-only path and compare the set of keys actually produced with the declared set key by key. On first run it found 214 keys that were produced daily and completely invisible to coverage reporting.
- **Document reconciliation**: README version, changelog and csproj must agree; spec/parser drift is red.
- **Golden-vector track record**: a TPM rc offset was wrong twice and caught by golden vectors; IPMI SEL Generator ID is a u16 and was caught by a length check; TPM2 big-endian count/eventSize at offsets 11/49 — the test vector was wrong twice and the tests caught each other.

### 13.1 Three things tests cannot catch

1. **Misread specifications**: implementation, tests, fixtures and docs can all be consistently wrong (TPM_PT). Only re-reading the source or external comparison finds it.
2. **Common sense never written as an assertion**: e.g. "XAML version literals are overwritten at runtime" — the compiler does not care, the screen hides it, and nobody guards it until someone thinks of it.
3. **Environment assumptions**: hard-coded developer paths, missing drivers, different privileges. These need real-machine testing and environment derivation, not more unit tests.

### 13.2 Baseline maintenance

`TestSuiteBaseline.ProjectTests` is a hand-written constant with exactly one source: the total printed by a full `dotnet test` run. **Never derive it from a "+N" in the ledger** — on 2026-10-15 the ledger wrote "tests +31" while 5476→5505 was actually 29. The guard can only verify "baseline ≥ number of test methods"; an off-by-one or two needs the release pipeline's trx counter.

## 14. Privacy and data sovereignty

- **Zero network APIs in fact collection and decoding**: `DataSovereigntyTests` scans all source in Services and Decoders; HttpClient, TcpClient, UploadString and friends turn it red. The only network features — AI review, feedback, speed test, network latency, update check — are user-initiated and on a fixed allow-list kept in sync with the data-sovereignty document.
- **Local only**: audit log at `%ProgramData%\XinSpect\Audit\audit.json` (hash chain, verifiable offline), driver certificates at `%ProgramData%\XinSpect\Driver` (ACL: SYSTEM/Administrators), time capsules and raw snapshots wherever the user chooses. Deleting the files deletes the data: no cloud copy, no backup sync, no remote residue.
- **Anonymisation**: the machine identity is a one-way hash (`sha256:` + 64 hex), never the serial number. Sensitive values are redacted by default; retaining them is explicit. The corpus pack accepts only redacted snapshots and excludes identity keys one by one.
- **Self-telemetry** is off by default, anonymous, numeric-and-time only, local, with a ring buffer of 500.
- **Audit log** records metadata only (action, scope, result summary, hash), never fact contents; no cloud, no blockchain.
- **AI boundary**: the endpoint receives exactly what the user previewed — the AI tab's "what will be sent" view is built by the same assembly path as the real request, not a mock-up. The API key is stored locally only after Windows DPAPI encryption (only this machine and this user account can decrypt it; an unreadable key is treated as "no key" with a stated reason and is never sent as ciphertext — the settings page shows the storage form actually on disk). Identifier redaction is on by default (the opposite of report export: a report stays with you, an AI request leaves the machine): UUIDs, MAC addresses, labelled IPs, `C:\Users\` paths, labelled serial numbers and host names are replaced; model names, capacities, clocks, temperatures and voltages are untouched. Requests have a character budget (60,000 by default): over-long messages are clipped, then the oldest whole messages are dropped, and the current question is always kept — what was dropped is stated to the model in the system prompt. Conversations can be exported to Markdown; the header states the time, model, provider, whether redaction was on and how many places it masked, and the endpoint-reported usage, while image content is excluded (text inside images cannot be redacted). When a request fails, is stopped, or returns nothing, a "Retry last" button replaces the failed bubble and resends the same question (image attachments included) instead of duplicating the user message; a successful answer never shows that button. A "Test connection" button in settings measures endpoint latency and lists the models — it queries the model list only and never sends a chat request, so it cannot cost tokens. Token usage is shown from what the endpoint reports (input/output/total, plus a session total); when an endpoint reports nothing the line says so instead of printing 0 — this program never estimates tokens, because an estimate assumes a tokenizer the model may not use. Timeouts are split three ways (45 s to headers, 90 s of stream idle, 300 s whole body) and 429/502/503/504/408 are retried a bounded number of times honouring `Retry-After`, but only before any text has been emitted. It is still "handing data to a third party," so it must be an explicit action and is off by default.
- **Embedded browser boundary**: In-page find (toolbar 🔍 or Ctrl+F; Enter next, Shift+Enter previous, Esc closes) drives `window.find` through a JSON-encoded literal so a search term can never become script — that page runs under the administrator token. DevTools is off by default (this process holds an administrator token; set `XINSPECT_BROWSER_DEVTOOLS=1` to enable it for debugging), and zoom is managed by the page itself between 50% and 200%. Only `http`/`https` are navigable — `file://`, `javascript:`, `data:`, `about:` and custom schemes are blocked with a stated reason (a page that can load a local path can display this machine's files). Downloads land in `%LOCALAPPDATA%\XinSpect\Downloads`, never overwrite an existing file, and the path is shown when the download starts and finishes. Device permissions (microphone, camera, location, notifications, clipboard read, sensors, multiple automatic downloads) are denied with a notice. A failed load names the cause (DNS, timeout, TLS certificate) and offers retry/home; certificate errors are never bypassed.

## 15. Known limitations

Three groups: design decisions (not a backlog), capability boundaries, environment dependencies. Full text in `docs/spec/LIMITATIONS.md`.

### 15.1 Explicitly not implemented

1. **EC (Embedded Controller) port access** — "read-only" still writes the command port and materially conflicts with acpi.sys transaction scheduling. See `docs/EC-RISK-ASSESSMENT.md`.
2. **Rowhammer / memory attack testing** — it breaks things. Only risk statements and ECC status; testing needs a dedicated machine and explicit consent.
3. **IPMI out-of-band paths** (KCS/SSIF/NCSI) — no BMC here to verify; only SEL/FRU/SDR **message decoding**, never a claim of IPMI support.
4. **MegaRAID BBU/VD/PD/foreign-config layout** — literature disagrees and no hardware is available to confirm; only the cross-implementation fields of the MFI frame header are decoded.
5. **MCHBAR memory timings (tCL/tRCD/tRP/tRAS)** — MRC training results, undefined in Intel's public datasheets; community reverse engineering does not meet the project's bar.
6. **PCH generation names** — LPC DID → marketing name needs per-file citation; no value without it.
7. **Registry.pol binary parsing** — existence and last-write fingerprint only.

### 15.2 Capability boundaries

8. **PCI BAR sizes are not reported** — they require a write probe (write all Fs, read the mask); this tool is read-only on PCI config space.
9. **Wi-Fi connection fields (RSSI/channel/BSSID)** — a disconnected interface says "not connected," no guessing. Channel is a lookup of the BSS centre frequency; frequencies outside the arithmetic return null.
10. **CPU generation tables cover only confident entries** — uncovered models say "not covered"; a wrong generation label is worse than none.
11. **Mutation testing covers pure decoders only** — the WPF/privileged main program is covered by injected tests and golden vectors.
12. **Snapshot hashes are integrity envelopes, not signatures** — they detect modification, not authorship.
13. **PMU programming verification has no reference-tool comparison** — S5 not run; labelled "multi-round test, not guaranteed usable."
14. **DCO needs vendor-private commands**; not implemented.
15. **ATA_PASS-THROUGH is deliberately unused** (measured hanging an IRP); SMART goes through the disk.sys proxy.
16. **Rule marketplace (needs a signing trust model), used-marketplace APIs, SDK/simulator** are deliberately out of scope.

### 15.3 Environment dependencies

17. **With HVCI/VBS on**: MSRs may be intercepted or synthesised and TSC may be offset — every MSR card's confidence is reduced, and the platform page says so.
18. **Microcode registry layout variants**: both the 8-byte and 4-byte layouts are decoded and labelled; ambiguous double-non-zero DWORDs are "not decoded."
19. **Group Policy blocking self-signed certificates**: installing the deep-access CA fails; the service reports that honestly.
20. **Unsigned executable**: SmartScreen may block it; the documentation does not teach users to turn SmartScreen off.

## 16. Building from source

```
git clone https://github.com/Xinglanclever/XinSpect.git
cd XinSpect
dotnet build XinSpect.csproj -c Release
dotnet test Tests/XinSpect.Tests.csproj
dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

- A Release build publishes and embeds both bridges (the net48 XTU bridge and the Blue Squadron guard). Debug builds do not embed, to shorten build times (the bootstrap searches upward through project outputs).
- Add `-p:BaseOutputPath=obj/_verify/` to the test command if the published `XinSpect.exe` is running: the apphost file lock blocks the default output path.
- XsRegProbe: C source and build notes in `XsRegProbe/` (`BUILD-給使用者.md`); requires WDK and signing (a narrow self-signed CA is enough, the same flow as the deep-access toggle).
- Layout: `XinSpect/` (main), `XinSpect.Decoders/` (pure decoders, the mutation target), `Tests/`, `BlueSquadron/`, `Bridge/`, `XsRegProbe/`, `docs/`, `Rules/` (historical external rule form, not shipped).

### 16.1 Build output and signing

- Single-file publish bundles the .NET runtime; `IncludeNativeLibrariesForSelfExtract=true` extracts native DLLs at runtime, which is why the first launch is slower.
- The project has no code-signing certificate. If you sign your own build, use your own certificate, not the deep-access CA — that one exists only for XsRegProbe and should be removed when deep access is disabled.
- Build warnings have a baseline guard (`WarningBaselineTests`): the main project must stay at zero warnings; a new one turns the suite red.

---

## 17. Anatomy of the distinctive features

This chapter is not a feature list. It takes eleven mechanisms that are hard to find elsewhere and opens each one: what problem it solves, what the data looks like, what failure looks like, where the boundary is, and **how you can verify it yourself**. A design that can be checked is a design; an unverifiable one is a claim.

### 17.1 The six-state lattice: giving "unknown" an algebra

**Problem**: most tools use `null`, `0` or `"N/A"` for unknown. All three collapse downstream: `null` is swallowed by `?? 0`, `0` is treated as a measurement, `"N/A"` enters string comparisons. Worse, the collapse produces no error message.

**Mechanism**: six states, a partial order, and conjunction propagation. `FactStateLattice.Combine(a, b)` returns the weaker state; `IsTrustworthy` is true only for `Present`. Deriving a conclusion from three sources therefore has the correct semantics by default: one unreadable input downgrades the result, without every caller remembering to check.

**Why the order is Present > Unknown > NotApplicable > NotSupported > InsufficientPrivilege > ReadError**: `Unknown` is "has a value but not vouched for," closer to usable than "this machine definitively lacks the hardware"; `NotApplicable` is an environment fact, more certain than "the platform offers no path"; `ReadError` is weakest because even the existence of a path is unclear. The order is not aesthetics; it makes `Combine` degrade consistently.

**Failure mode**: mixing `ReadError` with `NotApplicable` makes users think another machine would help; treating `Unknown` as `Present` turns a stale cache into a fact. Neither breaks the program.

**Boundary**: the lattice guarantees propagation, not source honesty. A hypervisor's synthetic MSR can still be labelled `Present`, which is why the platform page must show VBS/HVCI status and MSR confidence together.

**Verify**: `Tests/FactStateLatticeTests.cs` covers combinations; `Services/FactStateLattice.cs` is under a hundred lines and can be read in one sitting.

### 17.2 SpecRef: provenance as a compile-time contract

**Problem**: the most common decoder mistake is not miscalculation but "decoding a field with no specification basis." Such code looks perfectly normal.

**Mechanism**: every decoder method carries `[SpecRef(document, section, register, bits)]`; `SpecRefRegistry` reflects over every registered decoder and fails the suite when a reference is missing. Forty-six decoders are registered.

**Failure mode**: the citation exists but is wrong. TPM_PT's property numbers and revision semantics were "consistently wrong in implementation, comments, fixture and documentation" — reflection stayed green because everyone believed the same wrong source, and only a full re-read of TPM 2.0 Part 2 fixed it.

**Boundary**: SpecRef proves a citation, not its correctness. Correctness needs golden vectors (hand-computed bytes), external comparison, and human review.

**Verify**: the registry test lists uncovered methods; `docs/spec/METHODOLOGY.md` §1 states the contract.

### 17.3 Cross-reconciliation: contradiction as a first-class citizen

**Problem**: when two sources disagree, most tools pick one or average them. Both destroy information.

**Mechanism**: 26 rules, each with an id, a set of input keys, a relation, a SpecRef and a false-positive condition. Three verdicts: `Consistent`, `Contradicts`, `Unverifiable`. The engine guard converts a missing input key to `Unverifiable` instead of letting a rule return "consistent." Where either side may be absent, the case is split into directional rules that each guarantee one `Present` side — preventing the false green of "neither side read anything, therefore they agree."

**Track record**: a real contradiction between SMM_BWP=1 and the SMRAM lock was caught on hardware. A single-source dashboard would never show it.

**Failure mode**: a wrong rule yields a false red or green. The defences are 68 scenario tests and per-rule false-positive conditions; the semantics still need human review.

**Boundary**: a verdict describes a relationship, not a safety conclusion. Red does not say which side is right; green does not say the rest is consistent.

**Verify**: `Tests/FactRelationTests.cs` per rule; `FactRelationRules.All` is the only production source.

### 17.4 Time capsule and diff: replaying yesterday

**Problem**: a hardware change and a read failure look identical without a baseline.

**Mechanism**: all facts → canonical JSON (sorted keys, fixed number formatting, UTF-8) → SHA-256 envelope → anonymous machine identity. Diffs only compare two snapshots of the same machine; different machines are refused. Changes are added/disappeared/changed/unchanged, and **an unreadable source this time is listed as disappeared, never back-filled**. Asset classes map from key prefixes; everything else is labelled "state."

**Why canonical matters**: different key order or number formatting changes the hash, so a diff would see "everything changed." Canonicalisation is a prerequisite, not cosmetics.

**Failure mode**: reading "disappeared" as "hardware removed." The program can only say "not read this time," not why; the diff page separates disappeared from changed and keeps the raw state.

**Boundary**: SHA-256 is an integrity envelope, not a signature — it detects modification, not authorship. Proving origin needs signing and key management, which this project deliberately does not do (V-02).

**Verify**: create two snapshots, flip one byte in one file, and loading it is refused; `--json evidence` and the time capsule share the same shape.

### 17.5 Raw register snapshot: auditability down to the byte

**Problem**: facts are decoded conclusions; when you doubt the decoder you need the bytes.

**Mechanism**: `.xinraw` stores raw PCI security registers, whole ACPI tables, MSRs, SPIBAR and MCHBAR, with a SHA-256 envelope verified on load. Volatile bits (SMI counters, DEBUG_OCCURRED, HSFSTS status) are marked so they are not mistaken for a stable baseline.

**Failure mode**: sharing a raw snapshot as if it were anonymised. It is not; serials and UUIDs may be present.

**Boundary**: raw covers only what the tool can read; surfaces that need vendor commands (DCO) are absent, and the absence is itself information.

### 17.6 Audit hash chain: action records verifiable offline

**Problem**: a log can be edited, after which "no record" and "nothing happened" look the same.

**Mechanism**: each entry has `Sequence`, `PreviousHash` and `EntryHash = SHA256(canonical payload)`; the verifier recomputes every entry, so any edit breaks everything after it. `--verify-audit` runs outside the app and prints `fileExists` / `chainValid` / `checkedEntries` plus the break reason; a missing log is reported as "no audit events yet," never as a pass.

**Failure mode**: treating "chain intact" as "not compromised." An unkeyed chain defeats accidents and casual edits, not a privileged attacker who recomputes it.

**Boundary and fix**: V-02 (HMAC/DPAPI/external anchoring).

**Verify**: change any character in a middle entry of `audit.json` and run `--verify-audit`; the exit code should be 2.

### 17.7 Deep Bench: samples, not a score

**Problem**: benchmark "total scores" add different units and configurations, producing a number that is neither reproducible nor explainable.

**Mechanism**: 38 items, each reporting raw samples, confidence and limits; ladder metrics listed point by point; pooled configurations rejected; the statistics engine audited against known answers, with any mismatch rejecting the run. Quick/Full differ in sampling length and item mix, not "accuracy."

**Failure mode**: citing an `Insufficient` number. The program labels it; people ignore labels.

**Boundary**: no cross-domain ranking, no cross-version comparison, background work affects results.

### 17.8 Blue Squadron: posture assessment outside the process

**Problem**: security posture is a combination of six defence lines, and the assessment should not be influenced by the process being assessed.

**Mechanism**: a separate console process (`BlueSquadronBridge.exe`) speaking JSON over stdin/stdout; six lines (DMA, firmware/boot chain, CPU mitigations, storage/data, drivers/adversarial, system attack surface), each scored with advice; ETW supplies the timeline. No kernel driver, no network port.

**Failure mode**: treating the score as a guarantee. It is a rule-based summary of what was checked; unchecked areas do not lose points, they are simply absent.

**Boundary**: not antivirus, no blocking or isolation; ETW can miss events under load; with the guard off only the read-only posture remains.

### 17.9 Deep access: a key that opens exactly one door

**Problem**: some facts (SPI flash, PCIe AER) need a driver, but permanently relaxing machine-wide driver signing is too high a price.

**Mechanism**: generate a dedicated self-signed CA (RSA-4096) → install only that CA into Root and TrustedPublisher → install the XsRegProbe service → the driver accepts only allow-listed MSR/MMIO reads. Disabling stops and deletes the service and removes only its own CA thumbprint. Machine-wide test-signing is never enabled.

**Failure mode**: forgetting to disable it, leaving a long-lived self-signed CA in the trust store; or leaking the private key, which lets an attacker sign arbitrary drivers. Both are real; treat the key as a high-value asset and disable when done.

**Boundary**: an allow-list driver narrows "arbitrary read/write tool" but does not remove "the kernel driver itself may have a vulnerability." The driver code still deserves independent review.

### 17.10 Query language and CLI: auditable automation

**Problem**: a GUI cannot go into a script; an "anything goes" API makes numbers lose provenance again.

**Mechanism**: seven fields, three operators, AND semantics, `#` comments, values to end of line; `ParseException` with guidance; the CLI branches before single-instance logic; exit codes 0/2/1 mean all Present, some non-Present, fatal. There is deliberately no join, OR or aggregation — it selects rows, it does not invent facts.

**Failure mode**: querying `availability = ok` and treating the result as "the machine is fine." `ok` matches only `Present`; `error` matches `ReadError`; `no-permission` matches `InsufficientPrivilege`.

**Boundary**: the query sees this run's collected facts; uncollected keys do not appear, while the Fact Atlas shows the full catalogue — the two are complementary.

### 17.11 Fact-key catalogue and coverage: the denominator must be the catalogue

**Problem**: if the coverage denominator is "what this run found," coverage is always 100% — the unscanned items are not in the denominator. It is a completely normal-looking lie.

**Mechanism**: a compile-time static catalogue (1,167 keys) plus registered dynamic families (membership determined by the machine, but the prefix must be findable in source) plus runtime reconciliation (produced keys vs declared keys). A new key with no home turns the suite red. Coverage separates "received" from "measured"; absence is "not collected this run."

**Failure mode**: a stale catalogue (a feature adds keys without registering them). The guard catches "produced but undeclared," not "never written at all"; the latter needs human inventory.

**Boundary**: coverage means "handled," not "handled correctly." All 1,167 keys can be covered while half are decoded wrongly — that is the job of golden vectors and mutation testing.

## 18. Comparison with other tools

### 18.1 Method

This chapter does not rank or score. Different tools solve different problems, and putting them in one feature checklist only encourages everyone to do everything. Five design axes:

1. **Provenance**: can every number answer "how do you know?"
2. **Representation of unknown**: what appears when a value cannot be read?
3. **Cross-source contradiction**: what happens when two sources disagree?
4. **Automatability**: can it run in a script with auditable output?
5. **Self-verifiability**: can a user reproduce the conclusion from source or raw bytes?

### 18.2 Category comparison

| Category | Representative tools (generic) | Their strength | XinSpect's difference | Where XinSpect is weaker |
|---|---|---|---|---|
| Sensor monitoring | HWiNFO, AIDA64, LibreHardwareMonitor | Broad sensor coverage, mature graphs, long community validation | Every reading has provenance and six states; unreadable is never drawn as 0 | Sensor count and charting are below dedicated tools |
| Identification | CPU-Z, GPU-Z | Fast, complete model/process data, accurate identification | MSR measurements and cross-source reconciliation beyond the spec sheet | No mobile app, online database, or validation score |
| Drive health | CrystalDiskInfo, vendor SSD tools | Clear SMART presentation, mature notifications | Per-item failing-now, HPA, fake-capacity writes, NVMe power-state comparison | No vendor firmware update or warranty integration |
| Verification / fake capacity | H2testw-style tools | Simple, reliable write verification, widely understood | The same write verification plus HPA, SMART, chassis intrusion, microcode reconciliation | Less focused; more steps |
| Stress and stability | OCCT, Prime95, MemTest86 | Mature stress models, community baselines, long burn-in | Deep Bench reports samples and confidence, no composite score, wired to the fact layer | Less intense stress coverage |
| Security auditing | Autoruns, Process Explorer, CHIPSEC | Depth and ecosystems in their own domains | Firmware registers, reconciliation, evidence snapshots and audit chain in one fact model | Not endpoint protection; no blocking |
| Firmware / low level | CHIPSEC, RWEverything, vendor tools | Deep low-level coverage and direct hardware access | Read-only first, allow-list driver, a spec citation for every value | Deliberately does less (no writes, no bypasses) |
| Enterprise inventory | Asset-management and diagnostics suites | Large-scale deployment, reporting, policy integration | Single machine, portable, CLI plus offline-verifiable evidence | No central management, agent, or cloud dashboard |

**How to read it**: the rows are different problems, not one competition. XinSpect's difference is concentrated on axes 1, 2, 3 and 5; it is clearly behind on breadth, stress intensity and enterprise management.

### 18.3 Where it is clearly behind

- **Sensor count and update rate**: LibreHardwareMonitor is broad, but HWiNFO still goes deeper on rare boards and laptop ECs. XinSpect deliberately avoids EC (chapter 15).
- **Stress intensity**: Deep Bench measures, it does not torture. OCCT/Prime95/MemTest86 remain better for stability validation.
- **Hardware database**: CPU generations, PCH names and PCI device names cover only confident entries; unlisted says "not covered."
- **Report aesthetics**: the HTML report prioritises offline self-containment and verifiability over presentation design.
- **Localisation and accessibility**: three UI languages and basic keyboard navigation, but no full screen-reader optimisation.

### 18.4 When not to use XinSpect

- You want a **live game overlay**: a dedicated monitor is more responsive.
- You want **one-click repair**: this tool states facts; it will not decide to reinstall Windows for you.
- You want **central enterprise inventory**: no agent, no cloud; use an asset suite or pipe the CLI into your own pipeline.
- You want **certification or warranty evidence**: the output is not a legal or OEM certification; it helps you collect evidence, not replace a test lab.
- You **cannot grant administrator**: use the usermode features only; privileged facts will be honestly absent, never faked.

### 18.5 Capability matrix

| Capability | Typical sensor tool | Typical benchmark tool | Typical security tool | XinSpect |
|---|---|---|---|---|
| Provenance per fact | Some | No | Partial | Yes (SpecRef guard) |
| Six-state unknown | No (N/A or 0) | No | No | Yes |
| Named cross-source contradictions | No | No | Partial | Yes (26 rules) |
| Offline-verifiable evidence chain | No | No | Partial | Yes (hash chain + `--verify-audit`) |
| Replayable fact-set diffs | No | No | No | Yes (time capsule) |
| Raw register byte snapshots | Some | No | Some | Yes (`.xinraw`) |
| Query language / CLI | Some | No | Some | Yes |
| Reason when unreadable | Some | No | Partial | Yes (six states + source) |
| Cross-domain total score | Some | Yes | Some | **Deliberately none** |

## 19. Defect ledger

This chapter records the bugs this tree has had and the holes still open. Writing them down means the next maintainer does not step on the same rake, and users know where to look twice. **Fixed defects are not deleted from history** — the code is fixed, the memory is not.

### 19.1 How to read it

- `D-xx`: fixed (with version).
- `O-xx`: still open (design decision, unfixed, or unverifiable in the current environment).
- "Why the guard missed it" matters more than "how it was fixed": the same blind spot usually produces the next defect.

### 19.2 Fixed

| ID | Symptom | Root cause | Why the guard missed it | Fix / version |
|---|---|---|---|---|
| D-01 | README badge said 3,195 tests while the changelog on the same page said 3,524 | The badge was hand-written with no single source | The version was pinned; the test count was not | `TestSuiteBaseline` single source + badge guard across three READMEs (v2.38) |
| D-02 | All three READMEs still linked v2.2.0 at version v2.26 | Version written in many places, edited by hand | The guard only watched the badge | Download links and changelog head added to `ChangelogTests` (v2.36) |
| D-03 | The v2.85 executable reported FileVersion 2.84.0.0 | Only `<Version>` was changed, not `<FileVersion>` | FileVersion is a fourth location with no test | First-three-fields guard (v2.86) |
| D-04 | Window and About page each hard-coded a version literal thirty-odd releases stale | Runtime `AppInfo` overwrote them, so the screen never showed the stale value | Nothing counted overwritten literals | No version literals in XAML; writing one is allowed, writing an old one is red (v2.87) |
| D-05 | Download-table footnote stuck at v2.31 while links said v2.37 | The footnote is a present-tense sentence treated as decoration | Links were guarded, sentences were not | Footnote version guard (after v2.37) |
| D-06 | In a published build, coverage reporting read "0 fact keys" as "everything covered" | The denominator came from the filesystem; a missing file meant an empty denominator | Tests ran on a developer machine where the file existed | Compile-time catalogue + coverage guard (v2.37) |
| D-07 | `uefi.db` / `dbx` had never actually been read since release | Two of four privilege-enable implementations were broken; the attribute parameter was used as a size; wrong GUID | Every variable returned `null`, which looks like "this machine has none" | Consolidated into `FirmwareVariableAccess` + guard (v2.86) |
| D-08 | The NVMe health probe froze the whole collection chain; `taskkill /F` could not kill it | Stuck in open or the ioctl itself, so `CancelIoEx` never got a chance | Inner cancellation looked complete; there was no outer budget | Dedicated thread + 6 s budget + circuit-breaker cooldown (v2.86) |
| D-09 | The same drive showed different bus types on two screens (SAS printed as SATA) | Two bus-name tables, both shifted one entry, disagreeing with each other | Each table looked fine in isolation; nothing cross-checked them | Reconciled against the SDK enum, one table, with a guard (v2.87) |
| D-10 | A published single file could not read MSR/PCI/I/O/MMIO | The WinRing0 driver was not shipped alongside the executable | The developer machine had it on PATH or in the output folder | Embedded driver source (v2.43) |
| D-11 | Production code hard-coded developer paths (PaddleOCR-VL, CPU-Z variants) | Development-environment assumptions | Local tests always found the paths | Environment derivation + literal-path scan (v2.45) |
| D-12 | Eight assertions turned red the next morning | A test combined an absolute date with a `UtcNow` window | It was green on the day it was written | Time-bomb guard at method level (v2.44) |
| D-13 | Garbage bytes fed to the ETW parser crashed the test host | A TraceEvent finalizer defect | A normal `.etl` never triggers it | Validate the header before constructing (v2.42) |
| D-14 | The CLI entry point crashed on .NET 10 due to `StartupUri` | WPF startup model conflicted with CLI branching | The GUI path was fine | `Shutdown`-based startup + end-to-end test (v2.38) |
| D-15 | TPM_PT property numbers and revision semantics were wholly wrong | Implementation, comments, fixture and docs were consistently wrong | SpecRef only proves a citation exists | Full re-read of TPM 2.0 Part 2 (v2.85) |
| D-16 | Three fact services were written but never wired into production | No "services must be referenced" contract | Unit tests called the services directly, so everything looked green | Wired into the shared entry (v2.43) + `WiringDecisions` / orphan guard (v2.47) |
| D-17 | The troubleshooting wizard existed but no path reached it | A view is not the same as a navigation entry | Nothing scanned the navigation registry | `ViewEntryGateTests` (v2.86) |

### 19.3 Still open

| ID | Symptom | Nature | Current handling |
|---|---|---|---|
| O-01 | The v2.87 changelog says "29 reconciliation rules"; the code and `CoverageService.RuntimeRuleCount` say 26 | Historical text vs runtime | Published history is not rewritten; this document states 26 as the only fact |
| O-02 | `--verify-audit` verifies the chain, but `ev.chain.verify` (the mutual-verification input) is produced only by tests | Half-wired feature | Risk reports say "not obtained," never "no problem" |
| O-03 | The executable is unsigned; SmartScreen may block it | Supply chain and usability | Documentation never tells users to disable it |
| O-04 | The audit chain is unkeyed SHA-256 | Whole-chain recomputation possible | See V-02; HMAC not implemented |
| O-05 | The deep-access CA private key stays in `%ProgramData%\XinSpect\Driver` | Certificate abuse surface | Use-and-disable recommended; V-07 |
| O-06 | The embedded browser runs with an elevated token and DevTools on | Elevated browsing surface | V-01; no token separation yet |
| O-07 | The Deep Bench temp filename is fixed (`XinSpect.deepbench.tmp`) | Symbolic-link risk | V-13; random names not implemented |
| O-08 | PMU programming verification has no reference-tool comparison (S5 not run) | Verification gap | Labelled "multi-round test, not guaranteed usable" |
| O-09 | This round's new NVMe read path has no real-machine golden | Verification gap | Evidence mode does not launch scripts; labelled "source available, value pending manual scan" |
| O-10 | ARM64 untested | Platform coverage | No support claim |
| O-11 | The catalogue can fall behind new features (the guard catches "produced but unregistered") | Declaration vs reality | Reconciled each release; uncovered baseline needs reasons |
| O-12 | `Rules/builtin.json` is not shipped, while documentation once described it as a shareable external form | Documentation drift | Corrected here; code is the only rule source |

### 19.4 Three documentation defects fixed in this version

1. **Test count 3,094**: chapter 13 said "3,094 unit tests," less than half the badge's 6,580. Now 6,580, referencing the single source.
2. **Externalised rules**: 6.10 said rules were "externalised in `Rules/builtin.json`, directly shareable," but the file is not shipped and production uses 26 rules in code. Corrected, with the reason written into 6.10.
3. **29 vs 26**: the v2.87 changelog's 29 disagrees with the code's 26. The history stands; this chapter and 6.10 state 26 as the runtime fact.

### 19.5 Six defect shapes

| Shape | Signature | Examples | Corresponding guard |
|---|---|---|---|
| Silent error | No crash, no red, normal-looking screen | FileVersion, XAML literals, bus table | Pin present-tense claims |
| Guard blind spot | A tested, B untested | Unwired services, unregistered views | Coverage and orphan scans |
| Documentation drift | Code right, text stale | 3,094, `Rules/builtin.json` | Document reconciliation tests |
| Platform assumption | True on the dev box, false for users | Driver not embedded, hard-coded paths | Environment derivation, path scans |
| Concurrency/resource | Intermittent on the normal path | NVMe probe freeze, test-host crash | Outer budgets, header validation |
| Misdiagnosis | Fixed the wrong place, real cause remains | "The regex engine is broken" was a bad pattern | Keep the error record, mark the correction |

## 20. Hypothesised vulnerabilities and fixes

**Read this first**: this chapter is a threat model and a set of **hypotheses**, not a vulnerability bulletin. Each item states its status — confirmed / visible in code / hypothesised — and how to verify it. Nothing untested is written as "exploitable."

### 20.1 Assets, adversaries, preconditions

| Asset | Why it matters |
|---|---|
| Administrator token | The main program runs `requireAdministrator`; code execution in that process is administrator |
| Kernel drivers (WinRing0 / XsRegProbe) | Arbitrary MSR/MMIO/PCI access is the classic BYOVD target |
| Self-signed CA private key | Can sign arbitrary drivers — long-term kernel trust |
| Audit log and snapshots | Evidentiary value; once edited, worthless |
| Sensitive values in snapshots | Serials, UUIDs, MACs, usernames |

**Adversaries**:

1. a medium-integrity process on the same machine (ordinary user-level malware);
2. a malicious website the user visits in the embedded browser;
3. the supply chain (replacing the downloaded exe, a bridge, or a rule file);
4. physical access (brief control of the machine);
5. a malicious or corrupted snapshot/report delivered by social engineering.

### 20.2 Hypotheses

| ID | Hypothesis | Precondition | Impact | Current mitigation | Recommendation | Status |
|---|---|---|---|---|---|---|
| V-01 | The embedded browser browses arbitrary sites with an administrator token | User opens an unknown site in the browser tab | Renderer escape or browser bug → administrator | WebView2's Chromium sandbox/AppContainer; nothing relaxed in code; DevTools off unless `XINSPECT_BROWSER_DEVTOOLS=1` | move the browser to a separate medium-integrity process; longer term drop `requireAdministrator` and elevate per operation | Visible in code (`app.manifest`, `BrowserView.xaml.cs`) |
| V-02 | The audit chain can be recomputed end to end | Attacker already has write access | "Chain intact" no longer proves anything | Unkeyed SHA-256, no external anchor | HMAC with a DPAPI-protected key, or anchor the chain head in a read-only location; add `--verify-audit --anchor` | Confirmed (by design) |
| V-03 | Device strings may inject into the HTML report | A device name contains `<script>` or attribute-breaking characters | Script execution when the report is opened locally | Custom five-character escaper; report is offline and self-contained | Add a CSP meta tag, forbid scripts, encode by context (attribute/URL), add an injection corpus | Hypothesised (untested) |
| V-04 | CSV export may allow formula injection | User opens a CSV with `=`, `+`, `-`, `@` device strings in Excel | Formula execution / outbound link | No dedicated handling found | Prefix the first character with `'`; add tests | Hypothesised (untested) |
| V-05 | XML entity expansion in the BYOVD list | User loads a malicious XML | XXE / billion laughs | `XElement.Parse` does not process DTDs by default (.NET) | Explicit `XmlReaderSettings { DtdProcessing = Prohibit, XmlResolver = null }`; add a test | Visible in code (safe default, make explicit) |
| V-06 | Driver service install/uninstall race | Local attacker races the service operation | Service path replaced or residue left | Fixed service name; install needs administrator | Read back `ImagePath` and signature after creation; verify PID/image on uninstall | Hypothesised |
| V-07 | Deep-access CA private key exfiltration | Attacker gains administrator or a backup copy | Sign arbitrary drivers, long dwell | ACL to SYSTEM/Administrators; CA removed on disable | TPM/CNG-backed key, shorter validity, delete after use, add a certificate health check | Visible in code |
| V-08 | WebView2 profile (cookies) in the elevated context | User signs in to a site in the embedded browser | Cookie theft → account takeover | Separate profile folder, not specially protected | DPAPI-protect the folder, lower browser privilege, discourage signing in while elevated | Hypothesised |
| V-09 | Bridge extraction TOCTOU | Attacker can swap the file between verify and launch | Malicious exe executed elevated | SHA-256 + tightened ACL | Keep the file open with `FileShare.Read` and launch from that handle; re-verify before `CreateProcess` | Known, low risk |
| V-10 | The local API would lack authentication if enabled | User opens the listener | Any local process/web page reads facts | Not started by design; loopback, read-only | If enabled: random token, `Origin` check, tight CORS | Avoided by design |
| V-11 | The AI endpoint receives hardware summaries | User invokes AI review | Data egress, SSRF against internal hosts | Preview, default redaction, explicit action | Restrict schemes and private ranges, show the exact payload, DPAPI-protect the key | Partially mitigated |
| V-12 | A malicious snapshot causes resource exhaustion | User loads a `.json` from someone else | Huge arrays / deep nesting → memory or CPU exhaustion | SHA-256 verifies integrity, not size | Size limits, array-length limits, streaming parse, timeout | Hypothesised |
| V-13 | Fixed Deep Bench temp filename meets a symbolic link | Storage root is user-writable; attacker pre-creates a link | Elevated overwrite of an arbitrary file | Only the tool's own filename is created/deleted; no reparse-point check | Random name + `CreateNew` + check `FileAttributes.ReparsePoint` | Hypothesised (untested) |
| V-14 | The overclock watchdog depends on the main process surviving | Main process is force-killed | Voltage/clock not restored until reboot | Watchdog thread + `finally` | Move restore into a service or the bridge; detect unrestored state at boot | Hypothesised |

### 20.3 Fix priority

| Order | Item | Reason | Cost |
|---|---|---|---|
| 1 | V-01 browser token separation | The only high-impact surface reached in normal daily use | Medium (new process model) |
| 2 | V-02 audit-chain HMAC/anchor | Directly affects evidentiary trust | Low (key management + format version) |
| 3 | V-07 CA key lifecycle | Long-lived, machine-wide impact | Medium (TPM/CNG) |
| 4 | V-13 temp filename and link check | Low cost, removes a class of local attacks | Low |
| 5 | V-03/V-04 output encoding | Low cost, testable with a corpus | Low |
| 6 | V-12 snapshot resource limits | Important when receiving files from others | Low |
| 7 | V-14 external watchdog | Affects overclocking users | Medium |

### 20.4 Disclosure and repair principles

- The items above are **not** confirmed vulnerabilities. If you report a genuinely exploitable issue, include reproduction steps, affected version and environment, and avoid publishing user data.
- Fix the root cause and the guard, not just the one path. The lesson of D-07: the privilege-enable logic existed in four implementations; fixing two is the same as fixing none.
- Weakening a guard to make tests green is not an acceptable fix. False positives are fixed by improving guard precision, never by removing the guard.

## 21. Version iteration analysis

### 21.1 Where the numbers come from

Everything below comes from `Nav/ChangelogCatalog.cs` (the same record shown on the About page) and `XinSpect.csproj`; it can be recomputed from `git log` and the tests. It is a ledger, not marketing copy; gaps and inconsistencies stay visible in chapter 19.

### 21.2 Cadence

| Metric | Value |
|---|---|
| Entries | 100 (96 dated, 4 pre-repository stages) |
| Date range | 2026-08-27 to 2026-10-19 (54 calendar days) |
| Active release days | 28 |
| Average cadence | 3.4 releases per active day; 1.8 per calendar day |
| Peak | 12 releases in one day (10-08, 10-10, 10-11) |
| Change items | 630 (6.3 per release) |

**Reading**: this is not a steady "one release per day" rhythm but a sawtooth of concentrated batches, fixes, then another batch. October accounts for 66 of the entries, two-thirds of the total, so most features landed in a few high-intensity bursts. The advantage is speed; the cost is that guards and documentation lag — almost every defect in chapter 19 happened between batches.

### 21.3 Test growth

| Version | Tests | Note |
|---|---|---|
| v2.30 | 3,195 | First traceable full baseline |
| v2.42 | 3,590 | Steady period: +5 to +80 per release |
| v2.56 | 3,781 | File-forensics batch, +82 |
| v2.74 | 4,430 | Inventory and governance batch |
| v2.76 | 4,557 | +127 |
| v2.84 | 5,072 | Five-domain deepening |
| v2.85 | 6,393 | ULTIMATE-1000 phase one, 5,512 → 6,393 within one release (largest recorded increase) |
| v2.86 | 6,454 | +61 |
| v2.87 | 6,580 | +126 |
| v2.88 | 6,581 | +1 |
| v2.89 | 6,801 | +220 |
| v2.90 | 6,969 | +168 |
| v2.91 | 6,993 | +24 |
| v3.02 | 7,293 | +62 (AI usage and export, in-page find, view construction tests; rounds 292–299) |
| v3.01 | 7,231 | +82 (changelog reconciliation, AI and browser hardening; rounds 289–291) |
| v3.00 | 7,149 | +109 (guards, the knowledge inventory, and machine-recomputed README numbers; rounds 278–288) |
| v2.99 | 7,040 | +41 (EDID CTA-861 keys and the VIC knowledge pack, rounds 275–277) |

Across 62 versions from v2.30 to v2.91, tests grew by 3,798 (+105.9%), about +58 per release. **Test count is not a linear quality metric**: v2.85 added 881 tests within a single release (the ledger records 5,512 → 6,393), mostly from item-by-item tests for 105 contracts and skeletons, which does not mean fewer defects; conversely v2.87 added only 126 tests while fixing two defects that could freeze the entire collection chain.

### 21.4 Catalogue growth

| Version | Keys | Note |
|---|---|---|
| v2.39 | 136 | The catalogue becomes a fixed asset |
| v2.43 | 146 | Heat map and ESP scan |
| v2.56 | 221 | File forensics and self-integrity |
| v2.74 | 490 | Inventory batch |
| v2.84 | 602 | Five-domain deepening |
| v2.85 | 1,030 | ULTIMATE-1000 phase two begins |
| v2.86 | 1,037 | +27 dynamic families |
| v2.87 | 1,044 | Phase two: 75 landed, 24 aliased, 15 pending |
| v2.88 | 1,044 | No new fact keys this release: a documentation round (hardcore guides and dictionary) plus the Present pacing vtable-slot fix |
| v2.89 | 1,084 | ULTIMATE-10000 lands and round one closes: 40 keys and 14 dynamic families across four rounds (268–271) |
| v2.90 | 1,098 | Round two: self-load check and visualization tracking keys (rounds 272–273) |
| v2.91 | 1,099 | Value-level promotion: SMART lookup statistics and audit-chain cross-check (round 274, no new rows) |
| v3.02 | 1,131 | No new fact keys this release (rounds 292–299) |
| v3.01 | 1,131 | No new fact keys this release (AI and browser hardening, rounds 289–291) |
| v3.00 | 1,131 | Event-ID keys, provenance tiers for PCI names, and fact-shape guards (rounds 280–288) |
| v2.99 | 1,108 | EDID CTA-861 block keys plus a per-VIC family (round 276) |

From 136 to 1,131 is 8.3×. **The number is not "bigger is better"**: the catalogue is a declaration, and every extra key raises the cost of coverage reporting and runtime reconciliation. Removing duplicate and wrong keys is usually worth more than adding another one; v2.87's key-by-key reconciliation of the catalogue and dynamic families was exactly that kind of clean-up.

### 21.5 Features versus defects

Twenty of the 100 titles contain "fix, defect, hang, crash, misdiagnosis, behind, drift, correction." That does not mean the other 80 had no fixes — many are inside the item lists. The ledger shows fixes interleaved with feature batches: a big batch is usually followed by a release that repairs its wiring, documentation and platform assumptions.

Three recurring patterns:

1. **Feature first, guard later**: new pages, keys and services land, and the next release adds the guard for "is it wired / registered / reachable?" D-16 and D-17 are both this shape.
2. **Documentation is the last thing updated**: 3,094, `Rules/builtin.json`, 29 vs 26 — the code changed, the text did not.
3. **Platform assumptions fail only on real hardware**: UEFI privileges, the NVMe probe, embedded WinRing0 — all green on the developer machine, all black on a user's.

### 21.6 What this ledger cannot answer

- **Defect density**: there is no "defects per thousand lines" without reliable line counts and classifications.
- **User impact**: telemetry is off by default, so how many people hit a defect is unknown.
- **Performance trend**: only occasional releases record cold-start or warm-up times; there is no continuous series.
- **True test coverage**: test count is not coverage; the project publishes no line-coverage figure.

### 21.7 Three suggestions for the next release

1. **List every present-tense claim**: version, test count, byte sizes, rule count, key count, sensor count — every number in the documentation that changes over time needs one source and a guard. Most repeated defects in chapter 19 are this shape.
2. **Add real-machine goldens for high-impact, low-frequency paths**: NVMe, UEFI and driver loading cannot be covered by unit tests; they need a resettable machine and a scripted evidence mode.
3. **Keep a "consolidation release" between batches**: no new features, only wiring, documentation, guards and real-machine verification. v2.87 is already half of one (bus table, plan-library audit); make it a rule.

---

## 22. Version history

The ledger contains 116 entries (112 dated releases plus four pre-repository stages) from 2026-08-27 to 2026-10-31. The complete itemised ledger lives in the Traditional Chinese [README.md](README.md#二十二版本沿革) and in the About page; the table below lists every release from 2.60 onward with its one-line summary, followed by the earlier milestones grouped by era.

| Version | Date | Summary |
|---|---|---|
| 3.10 | 2026-10-31 | The overview now leads with what this machine is — capsule summary plus a details list; gauges off by default |
| 3.09 | 2026-10-31 | App icon replaced with the brand artwork (blue X over two window cards) |
| 3.08 | 2026-10-31 | The app now states dual licensing too, and says it applies to all versions |
| 3.07 | 2026-10-31 | The disclaimer now matches the licence (no more no-commercial-use claim), and the About page gains a commercial-licence route and a brand line |
| 3.06 | 2026-10-31 | The deployer finally draws a real download progress line; the part chip on upgrade suggestions is now dark grey |
| 3.05 | 2026-10-30 | Official site goes live: a primary button on the About page, the first card on the start page, a banner atop all three READMEs |
| 3.04 | 2026-10-30 | Making unreadable, imprecise and undated into facts: 22 rounds closing the third circle, plus a batch of already-shipping defects (rounds 309-330) |
| 3.03 | 2026-10-27 | Everything visible got fixed: home page and forum navigation, the sensor dashboard revived, two-layer M.2/U.2 reporting, and a mini overlay with 15 items and two styles (rounds 301-308) |
| 3.02 | 2026-10-26 | Visible completion and invisible wiring: AI usage and transcript export, in-page find, and 95 views actually constructed (rounds 292–299) |
| 3.01 | 2026-10-26 | Guards where they are actually walked: changelog reconciliation, AI redaction and split timeouts, a browser that only opens http(s) (rounds 289–291) |
| 3.00 | 2026-10-26 | Who watches the present tense: README numbers recomputed by machine, a built-in knowledge inventory, provenance tiers for PCI names, and four shape guards on facts (rounds 278–288) |
| 2.99 | 2026-10-27 | EDID CTA-861 extension block plus the VIC knowledge pack, and two defects that were quietly lying (rounds 275–277) |
| 2.91 | 2026-10-26 | Closing two registered-but-unfed keys: SMART lookup statistics now read real ATA attributes, and the audit-chain cross-check finally has a producer (round 274) |
| 2.90 | 2026-10-25 | Round two: self-load checks join the deep-bench suite, four visualization controls and a sensor dashboard page, export and report extensions (rounds 272–273) |
| 2.89 | 2026-10-25 | The 10,000-item catalogue lands and round one closes: quadruple dedup, 30 new items in three batches, SMART vendor knowledge pack, and two honest corrections to the verdicts themselves (rounds 268–271) |
| 2.88 | 2026-10-25 | Documentation is an interface too: trilingual hardcore guides, the fact-and-data dictionary, and a vtable slot no behavioural test could see (round 267) |
| 2.87 | 2026-10-19 | Proving rather than asserting: six firmware-risk facts, peripheral updatability declarations, and two silently wrong bus tables (rounds 264–266) |
| 2.86 | 2026-10-18 | Visible completion and invisible defects: Fact Atlas, three UEFI-variable gaps, an outer budget for the NVMe probe (rounds 259–263) |
| 2.85 | 2026-10-16 | ULTIMATE-1000 phase one: contracts, skeletons, provenance chain and guard methodology (105 items) |
| 2.84 | 2026-10-15 | Five-domain deepening: storage, security, ACPI, visualisation, scenarios (items 484–523) |
| 2.83 | 2026-10-14 | Engineering governance II (items 470–483) plus the NVMe log-page hang fix |
| 2.82 | 2026-10-14 | Offline analysis, product platform and Deep Bench methodology (items 371–399) |
| 2.81 | 2026-10-14 | Bus deepening, engineering governance and visualisation (items 346–370) |
| 2.80 | 2026-10-14 | Performance observation, Deep Bench wave two and governance close-out (items 323–345) |
| 2.79 | 2026-10-14 | Platform, storage and scenario close-out (items 303–322) |
| 2.78 | 2026-10-13 | Platform expansion (items 293–302) |
| 2.77 | 2026-10-13 | Firmware and boot auditing (items 253–272) |
| 2.76 | 2026-10-13 | Recovery deepening and security-audit supplement (items 223–252) |
| 2.75 | 2026-10-13 | Network security and final inventory (items 203–222) |
| 2.74 | 2026-10-13 | Inventory, governance and system register (items 173–202) |
| 2.73 | 2026-10-13 | Advanced supplement: caches, virtualisation, runtimes, topology, governance (items 163–172) |
| 2.72 | 2026-10-13 | Lifecycle, external interfaces and automation (items 113–142) |
| 2.71 | 2026-10-12 | Scenarios, multimedia and maintenance (items 94–112) |
| 2.70 | 2026-10-12 | Zero trust and data security (items 67–83) |
| 2.69 | 2026-10-12 | Licence changed to AGPL-3.0-or-later |
| 2.68 | 2026-10-12 | Vol2 items 31–55: maintenance, runtime, close-out, plus WPBT |
| 2.67 | 2026-10-12 | Vol2 items 21–30: PCIe capability chain, PPTT, server roles |
| 2.66 | 2026-10-11 | P0-09 offline knowledge-pack update (zero-network preserved) |
| 2.65 | 2026-10-11 | P0-02 FIT table (boot trust-root directory) and Boot Guard boundary |
| 2.64 | 2026-10-11 | P0-10 third-party notices; P0-07 error aggregation on the CLI |
| 2.63 | 2026-10-11 | P0-03 listening-port three-way comparison: socket × process × signature |
| 2.62 | 2026-10-11 | P0-01 TPM event-log replay comparison (third-party verification) |
| 2.61 | 2026-10-11 | P0-04 blue-screen archaeology: minidump reading and bugcheck decoding |
| 2.60 | 2026-10-11 | Two carry-overs from v2.56: event annotations on the timeline, explicit disk deep-scan entry |

**Earlier milestones**:

- **2.51–2.59 (2026-10-10 → 10-11)**: write-gate accounting across 17 hardware write call sites, the capability matrix (`cap.*`), render-snapshot guards, the release pipeline, audit-chain verification outside the app, write-audit cards, self-integrity, file forensics (Shimcache, Prefetch, USN, MFT), TPM replay groundwork.
- **2.38–2.50 (2026-10-08 → 10-10)**: the honesty foundation — six states with a lattice, coverage reporting, version guards across three READMEs, render heat-map rework, the WinRing0 embedding fix, time-bomb and path-literal guards, warning baseline, runtime key reconciliation, wiring decisions and orphan-service tests.
- **2.28–2.37 (2026-10-08)**: statistical sentinel, history schema v2, SPI entropy map, PCIe gap analysis, server/workstation views, IOCP queue-depth interpretation, audio endpoint buffers, the v2.36/2.37 coverage and document corrections.
- **2.0–2.27 (2026-09 → 10-08)**: the long build-out of hardware pages, deep registers, evidence lab, Deep Bench, Blue Squadron, CLI and the public specifications.
- **1.2–1.9 (2026-08-27 → 09)**: the first version-controlled releases; 1.0, 1.1, Release candidate and Beta predate the repository and carry only stage-level descriptions.

The full text for every entry, including each individual change and the test/catalogue counts recorded at the time, is in [README.md](README.md#二十二版本沿革). It is kept in one place on purpose: three hand-maintained copies would drift, and the guards would only catch part of it.

## 23. FAQ

**How is six-state different from N/A?** N/A collapses four different things: "this machine lacks it," "the platform offers no path," "I lack privilege," and "I tried and failed." Six states separate those, and add "has a value but unconfirmed" and "read and confirmed." With N/A you cannot tell whether another machine would help; with `NotSupported` you know it would not.

**Why do some items say "insufficient privilege"?** They need ring 0 (MSR/PCI config reads) or administrator (Security log). Run elevated and they become real values; the program never silently degrades or substitutes defaults.

**Why is the SPI flash audit all six-state?** Those registers live in SPIBAR and need MMIO. Once WinRing0 loads, `MmioBackendSelector` decides the path; if enterprise policy blocks WinRing0, enable deep access to use the allow-listed XsRegProbe driver. Six-state means "this path is not open," not "broken."

**Does a "contradiction" mean malware?** Not necessarily. It means two sources said different things about the same fact: firmware implementation differences, timing differences, or tampering. The card lists both values and sources; interpretation is yours.

**Why does the v2.87 changelog say 29 rules when the document says 26?** The runtime set is 26 (`FactRelationRules.All`; `CoverageService.RuntimeRuleCount` also says 26). 29 is historical text, recorded as O-01. This document treats 26 as the only fact and does not rewrite published history.

**Is an 82% mutation score enough?** Knowledge-table data files are deliberately excluded (asserting every string equals snapshot duplication); the main WPF/privileged program cannot be mutation-tested and is covered by injected tests and golden vectors. 82% is "within the tested scope," and the ledger records each batch.

**Does it collect my data?** No. The fact layer has zero network APIs (source-scan guard). The five network features — AI review, feedback, speed test, network latency, update check — all require you to initiate them.

**Is the Rowhammer probe safe?** It operates inside a self-owned buffer, but usermode cannot fully isolate neighbouring physical rows. The UI labels it dangerous and recommends a dedicated test machine. It is not a proper Rowhammer attack (no clflush), and the result is labelled unverified.

**Is Blue Squadron antivirus?** No. It is a read-only six-line posture assessment plus an observational ETW threat timeline. It does not isolate, delete, or block; it lays out what it sees.

**Why no total score?** Cross-domain numbers have no common unit. A total score would mean choosing weights for you, and weights are a value judgement, not a fact. Deep Bench gives raw samples and confidence; if you rank, publish your weight table.

**Why does the embedded browser run elevated?** Because the whole process starts with `requireAdministrator`, the price of "plug in and read MSRs." The risk is V-01 and the recommendation is token separation. It is not that the browser needs administrator; it is that they are not separated yet.

**Can the audit log be edited?** Editing a middle entry breaks the chain and `--verify-audit` exits 2. A privileged attacker can recompute the whole chain, so it defends against accidents and casual tampering, not a privileged adversary. See V-02.

**Why not use ATA_PASS-THROUGH for SMART?** It was measured hanging an IRP and could time out a disk command. SMART_RCV_DRIVE_DATA through the disk.sys proxy is long-proven and cannot wedge the user's disk. Reliability first.

**Why no EC reads?** An EC "read" still writes the command port and conflicts with acpi.sys transaction scheduling; symptoms can appear later (read today, fail tomorrow). The risk assessment closed it as not implemented, with reopening conditions in `docs/EC-RISK-ASSESSMENT.md`.

**Windows 7 or ARM64?** Windows 7 is not supported. ARM64 is untested and not claimed. The program uses .NET 10 and WPF; see chapter 4.

**Can I use it commercially?** Yes, but the AGPL-3.0-or-later network clause applies: if you distribute a modified version or run it as a network service, you must release the source under the same licence. See chapter 24.

## 24. License

This project is released under the [GNU Affero General Public License v3.0 or later](LICENSE) (AGPL-3.0-or-later): you may use, study, modify and distribute it, but **if you distribute a modified version or run it as a network service, you must release the source under the same license** (AGPL section 13). Bundled third-party components (Intel XTU SDK, LibreHardwareMonitor, TraceEvent, NAudio, WebView2, etc.) are governed by their own licenses and are **not covered by this project's AGPL grant**; see `THIRD-PARTY-NOTICES.md`. This project is **dual-licensed**, and this applies to all versions, not only the latest: pick one of two routes with identical functionality — AGPL-3.0-or-later (free, including commercial use; a modified version or a network service must release its source under the same licence) or a commercial licence (paid, for closed-source integration, hardware bundling, or organisations that ban copyleft, with no source-disclosure obligation). The two do not stack. See `COMMERCIAL-LICENSE.md`. WinRing0 is a dual-use driver; use this tool only on machines you own or are authorised to test.

**Two layers of third-party components**: runtime components shipped with the program (7) and build/test-time components (4) are listed separately. `THIRD-PARTY-NOTICES.md` is reconciled item by item against the csproj; anything unlisted is red. WinRing0 has two sources (bundled with LibreHardwareMonitor and embedded by this project), both stated.

## 25. Author

By: Xinglanclever

## 26. Community, contributions and sponsorship

This project has no support desk and no paid tier. Maintenance, interpretation and debugging all happen in public:

| What you want | Where |
|---|---|
| Ask what a number means, share interpretation experience | [Discussions](https://github.com/Xinglanclever/XinSpect/discussions) (if it is not enabled yet, open an issue first) |
| Report a crash, a blank view, or a value that contradicts the docs | [Issues](https://github.com/Xinglanclever/XinSpect/issues/new?template=bug_report.yml) |
| Report hardware that cannot be read or is decoded incorrectly | [Hardware report](https://github.com/Xinglanclever/XinSpect/issues/new?template=hardware_report.yml) |
| Suggest a new capability | [Feature request](https://github.com/Xinglanclever/XinSpect/issues/new?template=feature_request.yml) |
| Send code, a decoder or documentation | [CONTRIBUTING.md](CONTRIBUTING.md) |
| Security issue | [SECURITY.md](SECURITY.md) (**private report only; do not open a public issue**) |
| Code of conduct incident | [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) |
| Support maintenance | [GitHub Sponsors](https://github.com/sponsors/Xinglanclever) |

**The boundary of sponsorship.** Sponsorship does not affect issue ordering, feature selection, review standards or response time, and it does not buy "nicer-looking numbers". This project has no paid support; if you need a commercial support window, it does not exist here, and the documentation will not pretend otherwise.

**Before you report.** Do not include serial numbers, UUIDs, MAC addresses, asset tags, usernames or full paths. If you need to attach `--json` output, narrow it with `--only` and redact it yourself. The data-sovereignty position is in chapter 14.

## Appendix A: Utility tools

Every item under Utility Tools states its data source and privilege requirement:

- **Port usage**: which process owns a port (GetExtendedTcpTable/UdpTable matched against the process list).
- **Hosts editor**: edits the system hosts file; writing needs elevation, and a backup is taken first.
- **Blue-screen analysis**: decodes minidumps and stop codes locally, never uploaded. PAGEDU64 header offsets were verified against five real dumps; the code-to-symbol table follows Microsoft Learn, and unlisted codes print the raw code.
- **Junk clean-up**: temp, cache and update residue; sizes are listed before deletion, never silently cleaned.
- **Battery analysis**: health and cycle count (WMI BatteryStaticData/CycleCount).
- **Context menu**: enumerates and disables Explorer shell extensions.
- **Speed test**: up/down bandwidth and latency — one of the few user-initiated network features.
- **Memory trim**: releases working sets and the standby list (EmptyWorkingSet). This returns pages to the system; it does not make memory faster.
- **Startup items**: Run keys and Startup folders, with disable.
- **Driver audit**: signature state and driver date (read-only Win32_PnPSignedDriver); the audit page surfaces unsigned and old drivers in critical classes — information Device Manager only shows one Properties dialog at a time.
- **DNS switch**: one-click public DNS with latency measurement.
- **Large-file scan**: finds large files and folders.
- **Compute stability stress**: y-cruncher for silent computational errors under CPU/memory pressure; detects an installed copy and does not bundle it.
- **Frame-time monitor**: real frame times and 1% low for any process (ETW, no injection).
- **DPC latency**: identifies drivers behind audio crackle and input stalls (ETW).
- **Thread migration**: core bouncing and cache-layer loss (ETW, four-level attribution).
- **PCIe link**: negotiated speed/width against the device's capability (read-only config space).
- **Windows licence**: status, channel (OEM/Retail/Volume), reinstall and transfer feasibility (read-only).
- **Adapter advanced properties**: RSS queues and every driver-exposed setting (read-only WMI).
- **Sleep and wake**: wake sources, wake timers and power requests (powercfg data, read-only).
- **Display link**: encoding, bit depth and bandwidth (see 6.6).
- **NVMe power states**: declared versus measured (see 6.5).
- **USB link**: device capability against current speed — distinguishes port from cable when it drops to 480 Mb/s.
- **Boot repair**: runs SFC, DISM and CHKDSK and records output (Microsoft's tools; the program only invokes and records).
- **Hardware test**: monitor dead pixels, mouse (buttons, wheel, polling), keyboard (per-key, NKRO), speakers (channels, sweep), motion (ghosting, frame spacing) — native input events, no external dependencies, no network.
- **Browser / terminal**: embedded WebView2 browsing and a real cmd/PowerShell terminal. Both run with the elevated token; see V-01/V-08.
- **Advanced driver analysis**: class summary, age distribution, duplicate detection and known-problem drivers (read-only, built on the driver audit).
- **OS analysis**: version, licence, updates and security posture (Secure Boot/VBS/HVCI/UAC) on one page (read-only).
- **Display gamut**: EDID gamut coverage plus a CIE 1931 chromaticity diagram (2D).
- **Scenarios**: one-click quiet/balanced/performance — fan curve + power plan + GPU limit applied together.
- **Offering**: a green packet of Kuai Kuai to keep the machine behaving. Entertainment.

## Appendix B: Settings

- **Update interval**: sensor polling interval.
- **Appearance**: language (Traditional Chinese / Simplified Chinese / English), mini mode, theme.
- **Recording**: history retention and CSV export.
- **Alerts**: temperature/load thresholds and notification style.
- **AI**: endpoint, key, model and prompt.
- **One-click initialise**: re-runs module detection without rebuilding the per-second pulse.
- **Blue Squadron toggle**: starts/stops the guard and records the choice — if it was off last time, it does not come back at boot.
- **Fact-key coverage report**: the 1,167-key catalogue against this run; uncovered-but-reviewed baselines require a per-item reason.

## Appendix C: Audit log

Time-capsule creation and comparison are written to `%ProgramData%\XinSpect\Audit\audit.json`. The goal is non-repudiable metadata: each entry has Sequence (contiguous), Timestamp, Operator (a machine hash; the serial never reaches the log), Action, Scope, ResultSummary, ResultHash, and PreviousHash → EntryHash, starting from a GENESIS entry. The verifier checks contiguous sequence, chaining and recomputation; editing a middle entry fails, and recomputing that entry's hash still breaks the previous-hash link. Relationship to the evidence timeline: Timeline is the UI presentation, the audit log is the non-repudiable metadata chain; they coexist without overlap. Metadata only, no fact contents; no cloud, no blockchain.

**Boundary**: this is an unkeyed hash chain. It catches "edited one entry without recomputing the chain," not "a privileged party recomputed the whole chain." Stronger guarantees need HMAC or external anchoring (V-02).

## Appendix D: Troubleshooting

**"Insufficient privilege" at startup?** Run as administrator. Most usermode features work without it; privileged items are labelled individually rather than failing the whole page.

**SPI/MCHBAR facts are all six-state?** They need an MMIO path. Once WinRing0 loads, `MmioBackendSelector` decides (WinRing0 primary → XsRegProbe fallback → six-state); enterprise application control may block WinRing0, in which case deep access loads XsRegProbe.

**Reconciliation says "unverifiable"?** Any missing input prevents a verdict. It is not a contradiction, it is insufficient data. Fix the missing cause (load the driver, elevate) and the rule re-evaluates.

**Deep Bench fails to start?** Check that the storage root (default `%TEMP%\XinSpectDeepBench`) is writable and has free space; details go to `%APPDATA%\XinSpect\crash.log`.

**SMART cannot be read?** SMART_RCV_DRIVE_DATA goes through the disk class driver and works in most environments; NVMe uses Storage Query Property; USB enclosures usually do not forward SMART — a platform limit, reported honestly.

**UEFI variables are empty?** Read the state column: `InsufficientPrivilege` means `SeSystemEnvironmentPrivilege` was not enabled (usually non-elevated); `NotSupported` means this machine has no enumeration path. Before v2.86 there was also a defect that made every variable return null; it is fixed.

**The NVMe page hangs?** Since v2.86 there is a 6 s outer budget and a circuit breaker. If it still hangs, check `crash.log` and the drive model: some controllers never answer the health log, and `CancelIoEx` cannot reach a call stuck in open or the ioctl.

**The embedded browser will not open?** It needs the WebView2 Runtime; without it a fallback message appears and other tabs are unaffected. If multiple XinSpect instances run, the second uses a PID-suffixed profile folder.

**Tests fail with a file lock?** The published `XinSpect.exe` holds the apphost lock on the default output path; add `-p:BaseOutputPath=obj/_verify/` instead of killing the process.

## Appendix E: For contributors

- **New fact**: red → green TDD, six states plus reason, SpecRef (register decoders), non-invented provenance, full suite + Release green, Traditional Chinese commit and a ledger entry in `docs/ITERATIONS.md`.
- **New decoder**: put it in `XinSpect.Decoders` (pure functions, no WPF), give every public method a SpecRef, register it in `SpecRefRegistry.CoveredDecoders` (the registry test requires it), and add per-arm assertions to `DecoderHardeningTests`.
- **New reconciliation rule**: add it to `FactRelationRules.All` in `FactRelationEngine.cs` (with SpecRef and false-positive condition), register its input keys in `FactKeyCatalog`, and add scenario assertions in `FactRelationTests`. **Do not add a second rule table** — the v2.87 bus-table defect is exactly what happens when two tables drift.
- **New page**: register it in `PageRegistry`, or declare it in `WiringDecisions` as a standalone window/card; `ViewEntryGateTests` catches "written but unreachable."
- **New fact service**: wire it into `LoadUsermodeFacts` or the named driver entry; `ServiceOrphanGateTests` catches "implemented but never referenced by production."
- **Honesty contract is the acceptance criterion**: filling a failed read with 0 or a typical value, describing unrun as measured, or treating a capability claim as an active fact — any of these means rework.
- **Documentation changes**: every present-tense number (version, tests, bytes, rules, keys) needs one source; change the guard with the text, not just the text.

---

**About this document**: it corresponds to XinSpect v3.10 (2026-10-31). The Traditional Chinese version is the master, the Simplified Chinese version is generated from it with OpenCC (tw2sp), and this English version is a full translation sharing the same facts and guards. If a term in the Simplified version reads oddly, that is the converter's choice, not the original meaning; the Traditional Chinese master is authoritative.
