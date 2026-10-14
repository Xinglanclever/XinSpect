[繁體中文](WHITEPAPER.md) · [简体中文](WHITEPAPER.zh-CN.md) · English

# XinSpect Technical Whitepaper

**Version 2.84** · **Document date 2026-10-14** · **Corresponds to build `2.84.0.0`**

---

## About this document

This whitepaper is the complete technical description of XinSpect, a native Windows hardware verification, monitoring and security-auditing tool. It is written for three audiences: professionals assessing whether the tool fits their verification or audit workflow; contributors extending the fact sources or reconciliation rules; and auditors who need to understand the semantics and limits of the measurements before citing them.

It is not marketing material, and it does not repeat the usage instructions in `README.md`. Its subject is *intent, data model, decision semantics and engineering constraints* — why the tool is built the way it is, and what that costs.

Every figure quoted here is reproducible. Where the program can be machine-checked, this document cites it in a reproducible form and lists the full set in the appendices.

**Language editions.** This English edition is a complete rendering of the same document at a more compact level of detail; the Traditional Chinese edition is the originally authored text and the Simplified Chinese edition is a character-level OpenCC conversion of it. The appendices are generated from the repository and shared by all three editions. Appendices B and D carry rationale text authored in the source tree, which is Chinese; those entries are reproduced in full in the Chinese editions, and this edition summarises the categories of rationale and translates the dynamic-family declarations.

## Summary

XinSpect is a WPF (.NET 10) application for Windows x64, distributed as a single portable executable with no installation. Its central claim can be stated in one sentence: **it does not draw conclusions about your hardware; it lays out what each source says and names that source, together with whether it could be read at all.**

That claim is enforced at every layer by the same set of constraints:

- **One fact model.** Every observation becomes a single `HardwareFact` with ten fields: key, category, name, value, numeric value, unit, source, trust level, availability and measurement time. Time capsules, the query language, the local API and the HTML report all consume the same model, so what the interface shows and what the export contains cannot diverge.
- **Three-state honesty.** Every fact must be able to say *why* it could not be read. Availability distinguishes `Present`, `NotSupported`, `InsufficientPrivilege`, `ReadError` and `NotApplicable`. Filling an unreadable field with `0` or a typical value is classified in this project as a defect, not a trade-off.
- **Auditable sources.** Every fact records its origin (register address, WMI class, API). Register decoders must carry a specification reference, enforced by a reflection-based machine check.
- **Self-validating measurement.** Anything that measures must prove the measurement works before reporting: chess benchmarks use perft as a mathematical checksum, bandwidth conversions self-verify against a known-size workload, and PMU programming verifies write-readback each round.
- **Reconciliation, not scoring.** Cross-source checks emit exactly three verdicts — match, conflict, undecidable. No score, no "genuine" verdict. A conflict names both sides and lists the evidence.
- **Exemptions carry reasons.** Facts that are deliberately not cross-checked must be registered with a reason. Coverage reporting reports; it does not enforce. Forcing full coverage would manufacture rules that emit false "match" verdicts, which is worse than an honest number.

## Statistics snapshot

All figures below are produced by scanning this build's sources with `work/wp_gen.py` (`python work/wp_gen.py`).

| Item | Count | Note |
|---|---:|---|
| Static fact keys | 844 | Fixed at compile time in `FactKeyCatalog`, cross-checked against the source by a test |
| Dynamic key families | 23 | Membership determined by the machine; each declares why |
| Fact-key prefixes | 107 | Grouped index in Appendix A |
| Reconciliation rules | 18 | 6 per-disk, 12 machine-wide; see Appendix C |
| Explicit exemptions | 562 | Each carries a reason for not reconciling; see Appendix D |
| Service modules | 416 | Implementation units under `Services/`; see Appendix E |
| Test files | 433 | 4,568 `[Fact]`/`[Theory]` markers in total; see Appendix F |
| Documents | 51 | Specs, methodology and research notes under `docs/`; see Appendix G |
| Chapters | 58 | Parts I–VII plus Appendices A–H |
| Keys with no producer | 0 | Every catalogue key is produced somewhere in the source; an orphan key fails a test |

Runtime fact sources not counted above — WMI, the registry, event logs, performance counters, ETW and the X509 certificate store — vary with the machine and so are not fixed keys.

## Reproducibility

The appendices are not hand-transcribed tables. They are extracted from the sources in version control by a generator committed alongside the document:

```
python work/wp_gen.py
```

The generator reads `Services/FactKeyCatalog.cs` (the full static key set), `Services/FactKeyDynamicCatalog.cs` (dynamic families and their declared reasons), `Services/VerifyRules.cs` (rules, partitions, titles, required facts and scope) and `Services/FactCoverageReport.cs` (exemption prefixes and reasons), then walks `Services/`, `Tests/` and `docs/` to emit the appendix fragments and `stats.json`. `work/wp_build.py` assembles the editions.

The appendices therefore cannot drift from the code. If someone adds a fact and forgets the catalogue, `FactKeyCatalogTests` fails. If a rule is added so that a key is no longer uncovered, re-running the generator shows the difference. Consistency is delegated to tests and generators, not to the memory of whoever writes the documentation.

## How to read this document

- For the tool's position and its boundaries, read Part I (chapters 1–3).
- For how facts are obtained and stratified, read Part II (chapters 4–7).
- For how conflicts are decided and which facts are deliberately left unchecked, read Part III (chapters 8–10) against Appendices C and D.
- For specific capabilities, read Part IV (chapters 11–21).
- For engineering constraints, performance budgets and limitations, read Part V (chapters 22–28).
- For a field-by-field account of every fact domain, read Part VI (chapters 29–40).
- For implementation conventions, measurement methodology and the design-decision record, read Part VII (chapters 41–58).
- For the full sets, use Appendices A–H.

## Licence

XinSpect is released under AGPL-3.0-or-later. This document is part of the project and carries the same licence.

---

# Part I — Position and philosophy

## 1. Position and scope

The name combines a project code (`Xin`) with the Latin *spectator*, observer. That is not decoration but a technical description of the job: the program presents, side by side, what the hardware says, what the firmware says and what the operating system says, and names the conflict when two of them disagree. It does not conclude that hardware is genuine, modified or worth buying.

There are three practical reasons for that position.

**First, a single-source conclusion has no evidentiary value.** The processor model in a machine has at least four possible origins: the CPUID instruction's direct report, the SMBIOS processor structure, the registry's processor description and a system API summary. Those sources may copy one another or be independent. If the tool prints one number, the reader cannot tell where it came from and cannot assert anything in a dispute. XinSpect prints the source field alongside the value: the base frequency reported by CPUID leaf 0x16 and the base frequency derived from the silicon's maximum non-turbo multiplier are two independent facts, and their disagreement is itself a checkable lead (rule `R-CPU-05`).

**Second, meaning depends on context the user holds.** The same inconsistency is evidence of different things in different situations. Tools can establish the fact of disagreement; what it means depends on what the user knows. Hence the reconciliation engine emits three verdicts and no score, grade or truth claim.

**Third, a tool cannot validate its own conclusions.** If XinSpect printed "this machine appears modified", the user could only believe it or not. If it prints "the module serial field is empty, and the ratio of power-on hours to data written falls outside the plausible range; sources: SMBus SPD and NVMe SMART log", the user can re-check both leads and take that statement to a seller. A checkable statement is worth far more than a conclusion.

### Three source layers

Facts are stratified by origin, and the stratification runs through the whole program — the `source` field, the choice of rule inputs, and the ordering of the report all follow it.

The **hardware layer** is read by bypassing the operating system: model-specific registers (MSR), PCI/PCIe configuration space, SPD and TSOD on SMBus, ATA SMART attributes, NVMe health logs, Super I/O registers and CMOS. This requires Ring-0 access, obtained through a purpose-written fact-reading layer and the whitelist read-only driver XsRegProbe (chapter 6).

The **firmware layer** is produced by platform firmware at build time and handed to the OS at boot: ACPI tables (MCFG, HEST, BERT, SLIT, CEDT, HPET, FPDT, FADT and others), SMBIOS structures and UEFI variables. It describes what the firmware *claims* the machine is, which may lag the hardware.

The **operating system layer** is the OS and its drivers' understanding of the hardware: WMI classes, the registry, event logs, performance counters, ETW and the X509 certificate store. Easiest to obtain, easiest to rewrite — so every fact from this layer is `Reported`, never `Measured`.

The gaps between the three layers are the tool's main source of insight. A GPU's PCIe link can be read at its current negotiated width from configuration space while the device capability register reports its maximum; the difference is a stateable fact (rule `R-LNK-01`), and the OS layer typically just says "working", because a down-negotiated link still functions.

### Intended scenarios

**Used-equipment verification.** The buyer must judge, before paying, whether a device has been modified, refurbished, or has consumables near end of life — with limited time, no disassembly and the seller present. XinSpect concentrates on evidence obtainable without opening the case: flash image hashing and comparison, microcode revision consistency, SMART/NVMe cumulative counters, NVMe percentage used, capacity lost to HPA/DCO, and consistency between controller and label identity. These are hard to falsify undetectably because they live in the device's persistent records.

**Pre-deployment baselining for servers and workstations.** A configurable, long-lived snapshot for change management and warranty disputes: time capsules, a hardware change timeline and a hash-chained audit log verifiable without the application (chapter 48).

**Tuning and troubleshooting.** Distinguished as three separate values: what is configured, what is measured, and what the hardware claims. Configured values come from the registry or API, measured values from sensors or benchmarks, claimed values from SMBIOS or SPD. Machines where all three agree are rare, and the disagreements are where the tuning headroom is.

**Security auditing.** Structured, citable evidence rather than screenshots: machine-readable export (`--json evidence`), a query language for asking structured questions of a snapshot, and the per-mechanism state of TPM, Secure Boot, BitLocker, memory encryption and blocklisted drivers. Crucially, **"could not read" is itself recorded**: a machine whose TPM state is unreadable and one whose TPM is disabled are different findings, and conflating them produces misleading reports.

### Deliberate non-goals

**No unsolicited hardware state changes.** Reads are read-only. The few features that must write are placed behind a consent gate with the risk and scope stated in the interface; write paths are funnelled through `WriteGate` and `CeremonyService` so they remain auditable.

**No network traffic without a request.** No data is collected or uploaded. The only features that generate traffic are AI commentary (chapter 21) and update checks, both user-initiated, both with configurable endpoints. The local API is loopback read-only and its HTTP listener shell deliberately does not start — opening a listener adds local attack surface and is the user's decision, not the tool's default.

**No fault diagnosis.** XinSpect can point out that a hardware error event is associated with this boot, or that a drive's percentage-used is inconsistent with its bytes written, but it will not print "the motherboard is faulty". Diagnosis needs a full understanding of design and history that a tool does not have.

**No bypass capabilities.** Memory attack-surface inventory, Rowhammer probing and vulnerable-driver comparison exist so that users know what their machine is exposed to, not to provide attack tooling. Their output is limited to whether an exploitable condition exists and whether mitigations are enabled, and contains no exploit code. Likewise the REST example set (spec item XT-003) was evaluated and explicitly declined, with the reason recorded in the exemption list: publishing a usable example means shipping code that targets an uncommitted endpoint; the alternatives are the CLI's `--json` output and file exchange.

**No claim of completeness on any platform.** Development and validation are on Intel platforms. AMD-specific facts (SMU, PSP) are implemented but unverifiable on the development machine, and the exemption entry states plainly that a rule must be added when real hardware is available and the exemption removed then. Presenting "unverified" as "verified" is the most serious error class in this project.

## 2. Design principles

### 2.1 Three-state honesty

**Failure mode.** The most common and least noticeable defect in a hardware tool is making "unmeasured" indistinguishable from "zero". A user seeing 0 memory slots reads "this machine has no slots", not "the WMI query failed". On a single machine this is merely awkward; in batch auditing it produces wrong conclusions: a fleet-wide column of zeros may mean the fleet's driver version consistently lacks the query, not that the fleet lacks the hardware.

**Constraint.** Every fact carries `FactAvailability` with the values `Present`, `NotSupported`, `InsufficientPrivilege`, `ReadError` and `NotApplicable`. The field enters the data model, so it cannot be dropped in output. The interface shows a reason string rather than a blank or a dash, and `--json evidence` preserves the same field.

**Implementation.** Failures are recorded with `Diag.Swallow(site, ex, consequence)`, where `site` is a short location label and `consequence` is the effect on the user (for example, "service state not queried; roles report installation only"). Entries go to both an in-memory ring buffer and `diag.log`. The third parameter is deliberate: it forces the implementer to state the scope of the degradation rather than merely that an exception occurred.

`Diag` is itself constrained never to throw — a diagnostic mechanism that becomes a new source of failure defeats its purpose. `Diag` and `CrashLog` divide the work: the former records exceptions that were caught, degraded the feature and let the program continue; the latter records unhandled exceptions. Both live in the same folder so a bug report can attach both.

### 2.2 Auditable sources

Every fact must carry a `source` field identifying its origin (`MSR 0x1A0`, `WMI Win32_Processor`, `CPUID 0x16`, `SMBIOS Type 17`, `ACPI MCFG`). Register decoders additionally carry a `SpecRef` annotation pointing at the specification section, enforced by a reflection-based machine check on the decoder assembly. This raises the cost of casually adding a decoder — deliberately.

### 2.3 Self-validating measurement

Measurement's most dangerous failure is not an inaccurate value but a *silently invalid measurement*, which reports a stable, plausible, wrong number. Three mechanisms address it: chess benchmarks run perft to a known node count before timing and refuse to report if it disagrees; memory bandwidth self-verifies against a known-size workload to rule out cache effects; and PMU programming verifies write-readback each round because counters can be reset by other users such as BIOS or another monitor.

### 2.4 Reconciliation, not scoring

Scoring is the easiest output to implement and the easiest to mislead with: the difference between an 87 and a 92 may lie entirely within measurement noise, yet a score invites action and suppresses follow-up questions. The engine emits only match, conflict or undecidable. `VerifyRule.RequiredFacts` is the engine's precondition: if any required fact is missing the engine returns undecidable and explains what is missing and what would make it readable. Rule implementations therefore never contain missing-value branches, and missing values can never be mistaken for a match. Conflicts must name both sides and list evidence.

### 2.5 Exemptions carry reasons

Where hundreds of fact keys meet a dozen rules, "no rule considers this key" is the norm, and the danger is that the state *looks completely normal*: the fact is in the snapshot, shown in the UI, exported in the report, and never cross-checked. Coverage reporting sorts each key into covered, explicitly exempt (with a reason 562 times in this build) or uncovered. Uncovered is not an error, but it must be a *known number*. The threshold for exemption is "no second source exists to cross with", not "the rule was tedious to write". The project sets no coverage target: forcing full coverage manufactures rules whose "match" verdicts are false signals.

### 2.6 Read-only first, consent gate for writes

Reads are read-only by default. Features that write must go through a central write gate, state their risk and scope in the interface, and require per-instance consent. At the driver layer, XsRegProbe accepts only register accesses declared in a compile-time whitelist; the cost is expansion effort, and what that buys is an enumerable attack surface.

### 2.7 Mutation testing as the yardstick

Coverage measures what was executed, not what defects would be caught. Pure-function decoders live in a separate `XinSpect.Decoders` library under Stryker with an 82% target — and this has already caught a real defect, where extended-family carry was documented but not implemented, invisible to functional tests because the affected input combination is rare on real hardware.

### 2.8 Single executable, offline capable

The program ships as one portable executable: no installation, no registry writes, no background service, and core functionality (collection, reconciliation, reporting) works fully offline. Verification often happens where installing software is not possible, and a tool with zero removal cost lowers the barrier to using it on someone else's machine. The cost: the running program cannot scan its own sources — the constraint that produced the compile-time key catalogue of chapter 3.

## 3. The fact model and trust levels

### 3.1 One model

| Field | Meaning |
|---|---|
| `key` | Unique dotted identifier, e.g. `role.surface` |
| `category` | Grouping for the interface and report |
| `name` | Human-readable name |
| `value` | Rendered value |
| `numericValue` | Comparable part, empty for textual facts |
| `unit` | e.g. `MHz`, `MiB`, `°C` |
| `source` | Origin identifier (2.2) |
| `trust` | Trust level (3.3) |
| `availability` | Availability (2.1) |
| `measuredAtUtc` | Measurement time |

Two consequences follow. **Consumer consistency**: time capsules, the query language, the local API and the HTML report consume one model, so the interface and the export cannot diverge — the usual source of documentation defects. **Fixed cost per fact**: adding a fact adds one `HardwareFact`; four outputs, the query language and the reconciliation engine see it automatically.

Splitting `numericValue` from `value` matters: memory speed renders as `3200 MT/s` while comparing as 3200. Textual facts leave `numericValue` empty, and comparisons on them are undecidable rather than zero.

### 3.2 Key naming and stability

Keys are a property of the *program*, not of the machine: which keys exist depends on which services exist. Keys use dotted hierarchy, the first segment being one of 107 domain prefixes. A published key is never renamed; a change of meaning requires a new key; removing a key requires updating the catalogue, or a test fails.

### 3.3 Trust levels

| Level | Meaning | Example |
|---|---|---|
| `Measured` | Directly measured by this tool | MSR-read microcode revision, PCIe link width from configuration space |
| `Reported` | Self-reported by the system or firmware | SMBIOS module manufacturer, WMI processor description |
| `Derived` | Inferred from other facts | Silicon base frequency from multiplier and BCLK |
| `Unknown` | Unavailable or unclassifiable | Read failed with no other lead |

Levels are not quality grades but statements about the length of the inference chain. A `Reported` value is not "worse" than a `Measured` one — an SMBIOS serial field has no alternative source and can only be `Reported`. When a `Derived` fact conflicts with a `Measured` one, suspect the derivation; when two `Reported` facts conflict, suspect that one source was rewritten.

### 3.4 Orthogonality of availability and trust

The two dimensions are orthogonal, so "this machine does not have the hardware" and "it has it but it could not be read" are distinguished in the data model, not by output convention. That orthogonality is the structural answer to the failure mode of 2.1: the information needed to conflate the two has nowhere to be lost.

---

# Part II — Architecture

## 4. Overview

Written in C# against `.NET 10.0-windows`, with a WPF user interface following MVVM. WPF rather than a newer cross-platform framework for three reasons: Ring-0 access (MSR, PCI configuration space, I/O ports and MMIO through a custom driver and `DeviceIoControl`), heavy use of WMI and Win32 APIs, and hardware sensing integrated with LibreHardwareMonitor alongside an Intel XTU bridge and NVIDIA NVML/NVAPI. An embedded WebView2 host serves the built-in browser and parts of the HTML reporting.

| Directory | Responsibility | Size in this build |
|---|---|---|
| `Services/` | Collection, decoding, reconciliation, output, infrastructure | 416 modules |
| `Models/` | Data models and domain entities | 24 modules |
| `ViewModels/` | Screen state, commands, startup orchestration | — |
| `Controls/` | Custom-drawn gauges, history graphs, oscilloscope, donuts | — |
| `Views/` | Screen definitions | — |
| `XinSpect.Decoders/` | Pure-function decoders, separate library for mutation testing | — |
| `XsRegProbe/` | Whitelist read-only kernel-mode driver | — |
| `Tests/` | Test project | 433 files, 4,568 markers |

`Services/` dominates the module count, which reflects where the complexity lives: not in the interface but in obtaining facts correctly from dozens of sources and stating honestly when they cannot be read.

**Startup sequence.** Orchestrated by `StartupSequence`, advancing in steps and updating progress. Three considerations fix the order. Platform identification (processor, board, memory layout, OS build) comes first because later interpretation depends on it. Operations that may block — device IOCTL, WMI — are placed late so the interface becomes usable before they complete, and so a timeout shows a reason rather than an indefinite spinner. Expensive but optional work (the deep-test centre, evidence-lab collection) is user-initiated and outside the startup path.

**Collection organisation.** Collection is centralised in `EvidenceCollection`, whose `Load*` methods each correspond to a source or subsystem (`LoadPlatformFacts`, `LoadSbom`, `LoadAdvancedSupplement`, `LoadOperationsInventory`, `LoadNetworkSystemInventory`). Each has the same shape:

```
try { …collect and emit HardwareFact… }
catch (Exception ex) { Diag.Swallow("location label", ex, "effect on the user"); }
```

A single source's failure never prevents the remaining facts from being produced, and never passes unrecorded.

**Three levels of failure isolation.** *Fact-level degradation* presents the affected group with a read-error availability while everything else proceeds — by far the most common case. *Subsystem-level circuit breakers* (`StorageProbeBreaker` among them) stop retrying a source within the current run once repeated failure or timeout is detected, so one pathological source cannot stall the whole load. *Process-level exception logging* sends unhandled exceptions to `CrashLog`, filed next to `diag.log`. The distinguishing criterion is blast radius, and any new failure handling should join one of these three rather than inventing a fourth mechanism.

## 5. The collection pipeline

```
source read → decode → fact emission → snapshot aggregation → reconciliation → presentation/export
```

**Source read** is the most platform-dependent stage: driver IOCTLs, WMI queries, API calls, and file parsing (event logs, the registry, `setupapi.dev.log`). The rule here is to acquire raw bytes or strings without interpreting them.

**Decode** is pure: raw input, structured output. It is isolated in `XinSpect.Decoders` because it is the only stage that can be tested entirely without an operating system, and therefore the only stage where mutation testing applies (chapter 42).

**Fact emission** converts decoded output into `HardwareFact` and assigns trust and availability. This is where measured and self-reported part company: the same number read directly from a register is `Measured`; read from an SMBIOS field it is `Reported`.

**Aggregation** collects the load into a snapshot with a measurement time — the unit of currency for time capsules and historical comparison. **Reconciliation** checks cross-source consistency rule by rule (Part III). **Presentation** covers the interface, HTML report, CLI JSON and local API.

### 5.2 A real ordering dependency

The order is not arbitrary, and at least one genuine dependency exists — one that constitutes a known architectural defect.

`LoadServerCompliance()` takes `RoleFacts` as input, and the sole writer of `RoleFacts` is `LoadPlatformFacts()`, which sits **later** in the sequence. Under the current sequential load, server compliance therefore sees the role facts produced by the *previous* load rather than the current one. In the load where a role changes, the compliance verdict's input and the role facts in the same snapshot can disagree.

Under sequential loading this is stable and reproducible rather than random, but it is an internal contradiction inside one snapshot, which breaches the consumer-consistency principle of 3.1. The fix is to hoist role facts ahead of the compliance check, or to read the current load's role facts there. It is listed here because of the project's policy on defects: **record on discovery, even when the fix is out of scope for the current round.**

### 5.3 Load-time budget

Post-warm-up measurement of the full user-mode path on one machine puts the 45 load methods at about 5.6 seconds together, led by `LoadPlatformFacts` (~1.7 s), `LoadSbom` (~1.0 s) and `LoadAdvancedSupplement` (~0.7 s). Optimisation follows that ranking rather than intuition (chapter 23).

## 6. The read layer

The read layer is the only channel between the program and bare hardware, and the highest-risk part of the project.

**Driver and bridge.** Hardware-layer reads need kernel-mode privilege. Rather than depend on a third-party driver, XinSpect ships its own whitelist read-only driver, `XsRegProbe`, with a WinRing0-style fact-reading layer above it. The whitelist means the driver handles only register accesses declared in a compile-time list, with the contract defined in `XsRegProbeContract` and verified by tests. Three consequences: the accessible surface is enumerable and reviewable; expansion costs effort, which is deliberate because it forces each addition to be recorded and reviewed; and the capability boundary is explicit, which is a prerequisite in regulated environments.

The bridge (`WinRing0Bridge`) provides typed access and splits into MMIO (`MmioAccess`, `WinRing0MmioReader`, `DriverMmioReader`) and I/O port paths (`IoPortAccess`, `DriverMsrReader`); `MmioBackendSelector` chooses the MMIO backend because memory-mapped access differs across platforms.

**SMBus.** SPD and TSOD live on SMBus, handled by a group of modules for the controller layer (`SmbusController`, `ImcSmbusController`), bus arbitration (`SmbusBusLock`), reading (`SpdReader`/`SpdBus`), decoding (`SpdDecoder`/`SpdDetailDecoder`), TSOD survey (`TsodSurveyor`) and controller discovery (`ImcSmbusDiscovery`). Bus locking is a correctness requirement, not an optimisation: SMBus is shared, and concurrent access yields unreliable readings. It must work within the process, because other monitoring software on the same machine may access the same bus — a situation encountered on real hardware during this project.

**Timeouts and irreversibility.** The practical risk is not reading a wrong value but a read that hangs. Device IOCTLs can fail to return, and some hardware accesses cannot be cancelled once triggered. Hence every possibly-blocking read has a timeout and reports `ReadError` on expiry; high-risk sources are protected by circuit breakers; and the startup sequence places risky queries late.

**Read-only and the write gate.** Writes go through `WriteGate` and are authorised through the consent flow in `CeremonyService`, with the impact and risk stated in the interface (2.6).

## 7. Source layers and what they mean for interpretation

### 7.1 Hardware layer

`Measured`, since nothing mediates them: MSR (per-core microcode revision, frequency and multiplier, power state, temperature limits), PCI/PCIe configuration space (device identity, link width and speed, capability registers, BAR resources), SMBus SPD (module manufacturer, part number, serial, rated timings), ATA SMART (power-on hours, bytes written, power cycles, the full attribute set), NVMe health logs (percentage used, data units written, unsafe shutdowns, critical warning), Super I/O (fan speeds, temperatures, voltages) and CMOS. These are the hardest to falsify undetectably, because they live in persistent device records or in the silicon itself.

### 7.2 Firmware layer

`Reported`. ACPI tables (MCFG, HEST, BERT, SLIT, CEDT, HPET, FPDT, FADT and more, each decoded with per-table checksum verification), SMBIOS structures (system, board, chassis, processor, Type 17 memory devices, memory arrays, Type 38 out-of-band management) and UEFI variables (Secure Boot state, boot options, variable attributes). What this layer describes is *what the platform claims the machine is*, which need not be what it is — hence its disagreements with the hardware layer are primary reconciliation input. `FADT` and `HPET` declarations can be compared against actual timing behaviour, and `BERT` content against the boot error timeline.

### 7.3 Operating system layer

`Reported`, and the most readily rewritten: WMI (CIM and MSFT advanced classes), the registry, event logs, performance counters, ETW and X509 certificate stores. Table 7.3 in the Chinese edition rates each source's rewrite difficulty. The interpretive point is that this layer is easiest to obtain and most in need of cross-checking: a rewritten system may consistently report the same false value through both the registry and WMI, because both may derive from one modified store. Rules therefore prefer crossing this layer against the hardware or firmware layers, and a conflict between two OS-layer sources is far less informative than one between the OS and hardware layers.

### 7.4 Worked cross-layer example: memory

- Hardware: SPD gives each module's manufacturer, part number, serial, rated speed and (via the memory controller) the configured speed.
- Firmware: SMBIOS Type 17 describes each device; Type 16 declares the array's maximum capacity, slot count and error-correction type.
- OS: WMI gives total capacity and some configuration.

The cross-layer checks that follow are: whether modules are from one batch (hardware), whether serial fields are empty or duplicated (hardware), whether configured speed falls below rated (hardware-internal), whether physical device width exceeds data width, indicating ECC (hardware), and whether the firmware's declared ECC type matches that (firmware vs hardware). These map to rules `R-MEM-01` through `R-MEM-05`, detailed in chapter 9.

# Part III — Reconciliation

## 8. The engine

The engine's job is to compare what several sources say about the same thing, given that the needed facts are present, and to emit match, conflict or undecidable. Its non-jobs are as explicit: no score, no grade, no composite judgement; no prediction or extrapolation; no guessing when facts are missing; and never treating "not applicable" as "passing".

The last point matters. A desktop with no battery has `NotApplicable` battery facts; the engine must report undecidable with the reason, not "match". In batch auditing, counting not-applicable as no-anomaly systematically overstates fleet health.

**Rule type.** A `VerifyRule` carries an `Id` (`R-MEM-01`), a `Part` (memory, storage, battery, processor, PCIe link), a `Title` stating the proposition, `RequiredFacts`, an `Evaluate` function, and a `Scope` (machine or per-disk). Centralising the precondition in the engine means rule implementations only handle the complete-data case, and missing values are handled uniformly rather than ad hoc — avoiding both inconsistent handling and rules that fall into a branch and emit a wrong verdict. `VerifyFacts` provides typed access (`Get`, `Num`, `Text`) and existence checks (`Has`); missing facts are reported using `FactCatalog`'s display names and privilege requirements, so an undecidable verdict states what is missing and how to obtain it.

**Scope.** Machine-scope rules run once; per-disk rules run once per storage device, because storage health is inherently per-device — one machine may hold a brand-new SSD beside a refurbished one, and per-disk scope lets the problematic one be named instead of averaged away.

**Verdicts.** *Match*: facts present and consistent, with a brief reason. *Conflict*: facts present and inconsistent, with an explanation and a named evidence list. *Undecidable*: facts missing, with the missing facts and the conditions to obtain them. Conflict output must include evidence — the raw values that drove the decision — so a user can inspect them without trusting the verdict.

## 9. The rules, one by one

**Memory.** `R-MEM-01` (modules not from one batch): inputs are module count, manufacturers and part numbers; fewer than two modules is a match, otherwise any disagreement in manufacturer or part number is a conflict. Mixed batches are evidence of expansion, replacement or assembly — the rule states the inconsistency and makes no value judgement. `R-MEM-02` (anomalous serials): empty or duplicated serials across modules indicate non-genuine parts or rewritten SPD. `R-MEM-03` (memory not at rated speed): compares SPD rated speed against the configured value; causes include BIOS settings, platform limits, mixed modules and controller downclocking, and the tool states the gap without inferring the cause. `R-MEM-04` (array declaration vs installed): compares installed total, declared array maximum, slot count and module count; an installed total above the declared maximum points at a stale or rewritten SMBIOS. `R-MEM-05` (declared ECC vs actual bit width): the highest-value rule in this partition, because the cost difference between ECC and non-ECC memory is material, and it compares the firmware's declaration directly against a hardware measurement.

**Storage** (per disk). `R-SSD-01` (power-on hours vs bytes written): detects counter resets, because a controller need not reset both counters together. `R-SSD-02` (percentage used vs bytes written): "0% used" is the most common refurbished-drive claim. `R-SSD-03` (unsafe shutdowns exceeding power cycles): a logically impossible relationship, therefore more reliable than any estimate of a normal range. `R-SSD-04` (critical warning): the device's own statement, presented without further interpretation. `R-SSD-05` (claimed vs addressable capacity): detects HPA/DCO hiding and mislabelling, reporting the specific gap so the user can judge. `R-SSD-06` (rated rotation rate vs attribute set): detects a device masquerading as another class, since the attribute set is a hardware measurement.

**Battery.** `R-BAT-01` (full-charge capacity well below design capacity) — a purely internal cross-check requiring no second source.

**Processor.** `R-CPU-06` (inconsistent microcode revisions across cores, read per core via MSR): microcode is loaded uniformly at boot, so disagreement points at mixed steppings, an anomalous load, or a faulty read path; one of the most valuable verification findings because it is a hardware-layer fact. `R-CPU-02` (engineering-sample marking in the brand string). `R-CPU-03` (cache-level anomaly, accounting for hybrid core configurations). `R-CPU-04` (hypervisor detected, readings less trustworthy) — this rule's semantics differ from the others: it emits a *methodological warning*, not a hardware anomaly, because in a virtual machine the hardware-layer readings come from the hypervisor and may not correspond to physical hardware. It is an instance of the project treating the tool's own trust conditions as facts. `R-CPU-05` (silicon-derived base frequency vs CPUID's claim) — the key relabelling detection, since faking a model requires editing both the brand string and the frequency fields, while the silicon's actual multiplier table is hard to modify.

**PCIe link.** `R-LNK-01` (link narrower than the device's capability), taking the worst link in the machine. Down-negotiated links still function, so the OS layer usually reports "working"; causes include physical slot wiring limits (an x16 slot wired for x4), power-management settings, signal integrity and risers. Its value is that it is a direct configuration-space measurement, needing no performance test.

## 10. Coverage, the key catalogue and dynamic families

### 10.1 Why the catalogue exists

The full key set is fixed at compile time in `FactKeyCatalog` — 844 keys in this build. The design has a direct cause: an earlier implementation scanned `Services/` at runtime. That works in development and *necessarily fails in a release*, because the shipped directory holds only `Assets/` and the executable. A failed scan reported "0 keys", and coverage reporting read 0 as "nothing uncovered" — a failure read as an absence of problems, the most serious error class this project defines. The fix moved the key set to compile time and added `FactKeyCatalogTests`, which rescans the source and compares key by key.

### 10.2 Dynamic families

Some keys cannot be enumerated at compile time because their *membership is determined by the machine*: how many cards sit on the bus, which Super I/O ports were found, how many fixed-function counters the PMU has. This build registers 23 such families in `FactKeyDynamicCatalog`, each declaring why membership is not statically enumerable and what determines it (Appendix B). Examples: `pci.dev.` (one entry per device present on bus 0), `sio.0x` (the indicator ports actually detected, 0x2E/0x4E or others), `pmu.fixed.` (fixed-function counters, whose count comes from CPUID leaf 0x0A — two, three and four all exist in the wild), `net.listen.` (one entry per listening port), `tpm.replay.pcr.` (per-PCR replay results) and `reconcile.` (one conclusion key per rule).

`reconcile.` has a deliberate design point: a reconciliation *conclusion* is not a fact being reconciled, so it is excluded from the coverage denominator to avoid self-reference, but it is a genuine runtime output and is registered as such.

Two gates protect the mechanism: runtime reconciliation requires every runtime key to be either in the static catalogue or matched by a registered family prefix, and the zombie-prefix check requires every registered prefix to have a real producer in the source. Together they make the key space a closed, verifiable set.

### 10.3 Coverage reporting

Each of the 844 static keys is covered, explicitly exempt, or uncovered. Uncovered is not an error, and **no coverage target is set** (2.5). The categories among this build's 562 exemptions: *single source* (no second path exists — asset identity fields, audio endpoint buffers, role.service state); *deliberately separate* (virtualisation's three observations — CPUID, optional feature, service — are intentionally distinct statements, and binding them into one rule would judge the normal "installed but not loaded" state as a conflict; likewise NIC "shortfalls" already place the PCIe and line sides side by side in the interpretation layer); *specification decisions* (records of explicitly declining to do something, with a reason, such as spec item XT-003); and *platform limitations* (AMD-specific facts unverifiable on an Intel development machine, with the exemption stating that a rule must be added when real hardware is available).

### 10.4 Why report but not enforce

Where 844 keys meet 18 rules, demanding that every key be considered is unrealistic. Making "uncovered" a failure condition would push developers to produce rules that exist to lower a number: their inputs slightly contrived, their logic slightly strained, their "match" verdicts false signals that nonetheless create a sense of having been checked. So the project makes the uncovered count *visible and honest*. A reader can see that N keys are considered by no rule, and judge accordingly whether a specific field was cross-checked.

---

# Part IV — Capabilities

## 11. The deep-test centre

The deep-test centre holds user-initiated testing, separated from startup collection by *cost*: startup collection completes in seconds, whereas these items may run for minutes and impose significant load (all-core compute, sustained disk I/O, GPU load). Each item is a "run session", started individually or as a group.

Items fall into four classes. **Correctness verification** — memory testing, disk surface scanning, fake-capacity testing — output a binary result plus the specific failure location. **Performance measurement** — chess benchmarks, memory bandwidth, cache latency curves, disk sequential and random performance, GPU compute — output a value with its error margin and conditions. **Stability testing** — CPU all-core stress, GPU stress, power-transition tests — output a time series of frequency, temperature, power and error counts rather than a single number. **Security probing** — Rowhammer probing, memory attack-surface inventory — output whether an exploitable condition exists and whether mitigations are enabled, and contain no exploit code (1.4).

Every session is stored as a `BenchRecord` with the measured value, the conditions, the time and the environment. The reasons are threefold: historical comparison (same machine over time, to detect degradation); explanation of environment-sensitive differences; and error-range computation via `BenchErrorMargin`, which stops users treating differences inside the noise band as real.

## 12. Monitoring and measurement

**Sensors.** LibreHardwareMonitor is the engine, coexisting with the project's own sources (Super I/O, MSR, SMBus TSOD, NVMe temperature). Coexistence is not redundancy but a *cross-validation* source: the same temperature may come from an on-die MSR, the board's Super I/O and the NVMe device, and divergence between them is diagnostic (a poorly seated heatsink widens the gap between internal and external readings). `SensorSanity` judges whether a reading is credible — extreme, unchanging or implausibly jumping values — because some board sensors report a fixed value when unused, which without identification would be presented as "a steady 40 °C".

**Latency.** This is where the project invests more, because latency is harder to measure and explain than throughput. `DpcLatencyService` measures deferred procedure call duration, for diagnosing audio glitching and real-time problems. `FrameTimeService` measures per-frame presentation time and its variation — averages are inadequate here, so distributional output is produced. `AudioLatencyService` measures audio endpoint buffer configuration and effective latency. `CoreLatencyService` and `LatencyCurveService` measure inter-core communication latency and memory latency curves against working-set size. `FsMetadataLatencyService` measures file-system metadata operation latency. `InvisibleStallService` measures stalls that cannot be attributed to a single process or component: the kind a user perceives as "the system occasionally hitches" while CPU utilisation and disk queue metrics look normal — hence a direct measurement of scheduling delay rather than of proxy metrics.

**Jitter and its data boundary.** Network jitter measurement (spec item TU-008) uses the same opt-in target allowlist as `ms.ping`: with no target specified it **sends no traffic and reports no zero** — the latter would disguise "not measured" as "zero jitter" — and both the sample count and the definition of jitter must be stated.

**History and trends.** Live display uses an in-memory ring buffer; cross-session storage is handled by `HistoryStore`, watched by `TrendSentinelService`, with baselines and forecasts from `BaselineLearningService` and `BaselineForecastFactsService`. Storage location and size limits are the user's choice, and nothing is uploaded.

## 13. Benchmarks and self-validating measurement

**Chess perft checksum.** Legal node counts for a given position and depth are known, so `ChessBenchService` runs perft before timing and compares against the published value; if it disagrees, the move generator is faulty and no timing is reported. This turns "is this benchmark valid?" into a decidable question. A separate Chinese-chess engine (`XiangqiEngine`) exists because the search-tree characteristics differ enough to exercise the processor differently.

**Bandwidth self-verification.** `MemBandwidthService` and `MemBandwidthMath` run a known-size workload first, verifying that transferred bytes and elapsed time relate as expected — guarding against the failure mode where a working set smaller than the last-level cache makes cache bandwidth masquerade as memory bandwidth. Cache bandwidth is itself a valid measurement (`CacheBenchService`, `SlcCacheBenchService`), but the two must remain distinguishable.

**PMU write-readback.** `PmuProgrammingService` verifies that programmed counter configuration reads back as written each round, because BIOS, other monitoring software or the OS itself may reset the counters, leaving readings that correspond to no intended event yet still look plausible.

**Condition recording and error margins.** Every performance measurement records processor frequency state, memory timings, power policy, background load level and time, because cross-session comparison is only meaningful under comparable conditions. `BenchErrorMargin` supplies the uncertainty estimate so it can be printed alongside the value — uncertainty must accompany the number.

**Special-purpose units.** `NpuDetectionService` and `NpuComputeBenchService` handle neural accelerator detection and measurement; unlike classical benchmarks, performance depends heavily on model and quantisation, so the model and precision must be stated or the numbers are not comparable.

## 14. Security auditing

The goal is to answer one question: what state is this machine's chain of trust in, and can it be verified?

**Boot chain of trust.** `TpmSecureBootService` observes Secure Boot enablement and the associated UEFI variables; `UefiSignatureFactsService` observes UEFI image signatures; `UefiFvFactsService` observes firmware volume composition. Together they answer whether each boot stage was verified.

**Platform root of trust.** `TpmFactsService` (presence, version, state), `TpmReplayFactsService` (per-PCR replay of platform configuration registers, to check that recorded measurements are self-consistent), `FirmwareAuditFactsService` and `FirmwareDeepFactsService` (firmware-layer configuration).

**Runtime protection.** `MemoryEncryptionFactsService` (Intel TME / AMD SME series state), `VolumeEncryptionFactsService` and `BitLockerRecoveryFactsService` (volume encryption and recovery information), and `ByovdCompareService` (loaded drivers against Microsoft's vulnerable-driver blocklist).

**TPM PCR replay and its limits.** PCR values are extended in sequence, so given the measurements and their order, the resulting value can be recomputed. The limit must be stated: replay needs the complete list of stage measurements, and some platforms' firmware does not provide a complete event log. A failed replay therefore has two possible causes — an actual configuration change, or an incomplete log — and the output must distinguish them or it will produce false alarms.

**Vulnerable drivers.** Comparisons rest on Microsoft's blocklist, which has no second list to cross-check against (the recorded exemption). This also exposes a boundary: the list is an external asset whose completeness is outside the tool's verification scope, so the output reflects the list at the time it was obtained.

**Memory attack surface and Rowhammer.** `MemoryAttackSurfaceService` inventories exposure (ECC, memory encryption, Rowhammer mitigations) and `RowhammerProbeService` probes for exploitable repeated-access conditions — both limited to existence of exploitable conditions and mitigation state, never exploit code.

**Zero trust and compliance.** `ZeroTrustFactsService`, `EndpointPolicyFactsService`, `DeviceComplianceFactsService` and `AuditPolicyService` cover endpoint policy, including MDM enrolment state (ZR-001, status only, no identity data read), Entra join state (ZR-002), Passkey support (ZR-005, a file-exists plus build-number conditional statement) and device compliance baselines (ZR-004). The last is worth expanding: the typical failure mode of compliance checking is that when data is unavailable, the comparison is judged "no violation found", producing a report that looks like a pass. XinSpect requires unreadable items to be marked undecidable and the snapshot date to be printed so the reader can judge the data's currency.

## 15. Memory and storage reliability

Storage health evidence comes from three levels: **device self-report** (ATA SMART via `AtaIdentify` and `StorageSmartService`; NVMe health via `NvmeHealth` and `NvmeLogDecoder`), **controller behaviour** (attributes defined by vendor, so the tool prints attribute numbers and raw values for cross-reference against vendor documentation), and **file-system behaviour** (`DiskLatencyStats`, `FsMetadataLatencyService`, `FileSystemVolumeFactsService`), which diagnoses "the device is healthy but the experience is poor" — a classic case being an SMR drive whose SMART attributes are entirely normal while sustained writes collapse.

Per-disk reliability counters form the `storage.reliability.` dynamic family: one entry per disk per field, nineteen fields per disk, because which disks exist and which fields the provider supplies are machine and firmware facts.

`SmartFailingNowFactsService` handles "this drive is failing now", which differs from general health judgement in time scale: health uses cumulative counters, whereas imminent-failure detection looks at recently changing attributes.

Memory reliability spans module level (SPD over SMBus) and array level (SMBIOS Type 16), with `SpdConsistencyAuditService` auditing whether the modules' SPD contents are mutually compatible — mixing mutually incompatible modules is a common cause of instability. Memory testing itself is a correctness item in the deep-test centre.

## 16. Firmware and platform auditing

**Flash analysis.** `SpiFlashService` with `SpiFlashHashService`, `SpiFlashCompareService`, `SpiEntropyService` and `SpiFitFactsService` covers platform flash reading and analysis — among the most valuable verification evidence. Four techniques: hashing (for comparison against known-good content and for version control across time), comparison (per-region difference lists, not merely "differs"), entropy analysis (identifying compressed, encrypted or unwritten regions) and structural parsing (`SpiFitFactsService` parses the Firmware Interface Table for stage addresses and types, cross-checkable against other facts such as the presence and version of the microcode region).

**Management engine and platform security processor.** `BiosMeService` and `BiosMeDecoder` cover Intel ME/CSME presence, version and flash regions; `AmdSecurityFactsService` covers the AMD equivalents including PSP presence and SEV-series support. The technical significance is that these execute outside the OS, so their state is not reflected in OS-layer facts — firmware analysis is the only route. The verification limit must be stated: the development machine is Intel, AMD-specific facts are unverifiable, and the exemption records it.

**ACPI depth.** Tables are decoded structurally, not merely listed: MCFG (complete memory-mapped configuration list), HEST (hardware error sources), BERT (boot error records), SLIT (locality distance matrix), CEDT (CXL early discovery), HPET (complete configuration including base, sequence and minimum tick), FPDT (firmware performance data for boot-stage timing), PPTT (processor topology) and FADT plus associated third-party tables. Decoding is layered, with `AcpiTableService` handling acquisition and structure and dedicated decoders handling semantics. Per-table validation is built into the decoders, which is why ACPI facts are exempted from separate cross-source rules.

**Firmware performance data.** FPDT records per-stage firmware boot timing; combined with OS-layer boot timing (`BootTimingFactsService`, `BootBreakdownDecoder`, `BootBreakdownService`, `UefiBootFactsService`) this decomposes boot into firmware and OS phases. The value is localisation: firmware settings and device initialisation are handled quite differently from driver and service startup.

**SMBIOS depth.** `SmbiosService`, `SmbiosFacts` and `SmbiosDeepFactsService` parse system, board, chassis, processor, memory device (Type 17), array and out-of-band management (Type 38) entries. A design requirement is that *missing fields must have somewhere to record the absence*, which produced the `asset.field.` dynamic family: the field set is a property of the specification rather than a constant, and absent fields need a place to be marked absent. Without it, a field absent from SMBIOS simply would not be emitted and would vanish from coverage and reporting, leaving users unable to distinguish "this machine has no such field" from "the tool does not read it".

## 17. Servers, workstations and compliance

Server and workstation fact needs differ from consumer machines in kind, not merely amount. The *sources* differ: out-of-band management structures (SMBIOS Type 38), chassis physical security state (Type 3 intrusion detection and lock state), PSU PMBus data, and enterprise storage paths (iSCSI, MPIO, Fibre Channel). The *meaning* differs: "virtualisation installed" on a consumer machine usually means virtual machine software; on a server it may mean the host runs the Hyper-V role. The *completeness requirement* differs: on a consumer machine a missing field is inconvenient, whereas a missing field in a server configuration snapshot can fail change-management review.

The response is not a duplicated feature set but `ServerModeFactsService`, which determines whether the machine is in a server operating mode, with conditional collection (`ServerRoleConditionalFactsService`, `ServerRoleDeepFactsService`) and compliance checking (`ServerComplianceFactsService`) expanding in that mode.

`RoleSurfaceFactsService` inventories installed roles and features from `Win32_OptionalFeature` (installation state) and `Win32_Service` (running state), producing "installed, N services running, M installed but not running". Two design decisions: service state is queried **once** and indexed, rather than per feature, which otherwise slows the load when there are many roles; and "installed but not running" is stated as a state rather than an anomaly, because it has legitimate causes. Role *compliance* is a separate layer, and this is where the known ordering defect of 5.2 bites.

Enterprise storage covers iSCSI sessions (`IscsiSessionFactsService`), MPIO and Fibre Channel presence (`EnterpriseStorageFactsService`) and related service existence — an environment fact, hence exempt. Network inventory is handled by `NetworkSystemInventoryFactsService` and `NetworkSecurityInventoryFactsService`, covering adapters, advanced properties, offload capabilities (`NetOffloadFactsService`), link state (`NicLinkFactsService`) and link health (`NicHealthFactsService`). `OobFactsService` probes out-of-band management presence from SMBIOS Type 38; its exemption notes that it is simply not applicable where the hardware is absent — and that not-applicable must be distinguished from read failure.

**Service readiness timeline (PO-005).** Per-service start times relative to boot, with a recorded constraint worth quoting: time is a fact, cause is not; no grading or judgement of slowness, and no service names. The constraint expresses the boundary between measurement and interpretation: a tool can establish that something happened at a time, not that it should have happened at another — that depends on deployment knowledge. Not listing service names is a privacy decision, since a service list reveals an organisation's software deployment.

**Background service mode (AG-005).** Inventories what would be missing if the program ran as a service, with a recorded constraint: capability inventory and rough cost only; it creates no service and registers no schedule. "Could be done" and "is being done" are different things.

**Decision registry.** `OperationsInventoryFactsService` and `InventoryGovernanceFactsService` touch a higher-level pattern: `WiringDecisions`. When the program contains a fork requiring human judgement — such as whether to start the HTTP listener shell — the decision itself is registered as data with its content, reason and conditions, and the environment check consults that registry rather than relying on implicit code. The benefit is that reasons do not evaporate with time and staffing, and users can look up the specific reason the tool does not do something.

## 18. Virtualisation and confidential computing

**Three deliberately separate observations.** `VirtualizationFactsService` reports CPUID's hypervisor bit, the Windows optional virtualisation feature, and the presence of virtualisation-related services. Separation is deliberate, with the recorded reason: these are intentionally distinct statements, and binding them into one rule would judge the normal "installed but not loaded" state as a conflict. The combination has six possible states mapping to different realities — and importantly, CPUID reporting a hypervisor means *this machine is running above a virtualisation layer*, so every hardware-direct reading (MSR, PCI configuration space, sensors) must be understood as supplied by that layer, with reduced trust. This is exactly the methodological warning of rule `R-CPU-04`.

**Hyper-V, NUMA, large pages, RDT.** `HyperVInventoryFactsService` inventories host-level configuration including virtual switches and virtual machines. `NumaTopologyService` obtains topology from kernel32 and the ACPI SLIT table; its exemption notes that the two sources are already placed side by side at the decode layer, and that single-node machines have nothing to measure across nodes — an unmeasurable item is not a failed measurement. `LargePageService`/`LargePageDecoder` cover large-page support and `RdtService` covers Intel Resource Director Technology.

**Confidential computing.** Intel paths (`PlatformSecurityMsrFacts`, `MemoryEncryptionFactsService`, `TmeFactsService`, `TdxFactsService`) and AMD paths (`AmdSecurityFactsService` for SEV, SEV-ES, SEV-SNP, SME support and enablement, plus PSP presence). The design point is the separation of *supported* from *enabled*: `sev.supported` and `sev.enabled` are independent facts, because hardware support with firmware enablement absent is both common and important. The verification limit must again be stated: the development machine is Intel, so AMD facts are unverifiable, and the exemption records the limit and its removal condition.

**CXL.** `CxlFactsService` reads CEDT and CHBS; the exemption records a single source and no CXL hardware on the development machine — a case of implemented but unverifiable, handled by registering the limit rather than claiming the feature works because tests pass.

## 19. The evidence lab and querying

The evidence lab (`EvidenceLabService`) is the deep-collection and analysis surface; it supplements rather than replaces the startup path. Its unit of organisation is the **snapshot**: one complete collection with a measurement time, which is the input to all downstream analysis.

**Time capsule.** The serialisation container for a snapshot, spec'd by `docs/spec/snapshot.schema.v1.json`. Three design points. *Self-sufficiency*: a capsule carries the complete readable fact set and its context (time, source, trust, availability), depending on nothing about the reader, so it can be analysed on another machine or re-interpreted months later. *Canonicalisation*: the serialisation is canonical, so identical facts produce byte-identical output, which makes hash comparison a workable change-detection method. *Link to coverage*: a capsule's key set can be compared against the full catalogue, showing which of the keys it covers and distinguishing "not covered" from "covered but unreadable".

**Hardware change timeline (HM-007).** `EvidenceTimelineService` compares two snapshots, emitting per-key differences with old value, new value and change time. Its exemption notes that the comparison is itself a third-party cross-check, so no further rules are stacked on it. Uses include warranty disputes, change management and fault localisation.

**Query language.** Spec'd in `docs/spec/query-language.v1.md`. One clause per line, `#` for comments, values with spaces extending to end of line. Seven fields (key, category, source, value, availability with aliases `ok`/`error`/`no-permission`, since, until) and three operators (`=` exact, `~=` case-insensitive substring, `^=` prefix), clauses joined by AND. Syntax errors raise `ParseException` with corrective guidance. Two principles matter. **Found and readable are different things**: entries that exist but cannot be read still match, with the reason attached, so a query can deliberately enumerate everything unreadable through insufficient privilege — which auditing requires. **Disguising a query error as an empty result is an honesty violation**: a syntax error returns an explicit error, never an empty set.

**Knowledge packs.** `KnowledgePackService` and `KnowledgeCoverage` load external knowledge as data tables (device compliance baselines, CVE cross-references, organisation-specific checklists). `QueryInsightFactsService` and `QueryGovernanceFactsService` cover query governance, including help coverage (QS-005, comparing the two project-owned assets `HelpCatalog` and `PageRegistry`).

## 20. CLI, local API and reporting

**CLI.** The command line exists for automation and headless environments. Its core outlet is `--json evidence`, emitting the complete fact set as canonical JSON shaped exactly like a time capsule, which lets XinSpect be embedded in existing asset-management or audit pipelines without using its interface. `--verify-audit` verifies the audit log's signature chain without starting the application, so verifying a log does not require trusting the program that produced it.

**Local API.** `LocalApiHandler` provides `GET /api/facts` (all facts, canonical JSON, same shape as a capsule) and `POST /api/query` (query language text, matching results). Constraints: loopback, read-only, anonymous machine identification — meaning machine identity is not required of the request and the response leaks nothing beyond the fact set. The HTTP listener shell **deliberately does not start** (17.7): the implementation exists, the listening behaviour is off by default because opening a listener adds local attack surface, and headless access is served by the CLI.

**Reporting.** `HtmlReportService` produces an organised presentation of facts, reconciliation results and interpretation notes, with a requirement of *methodological visibility*: every value is labelled with its source and trust level so a reader can judge the nature of the evidence without going back to the interface. `ExternalReportService` handles interchange formats. Report signing (RS-010) covers the audit chain head and verification result; the exemption notes that chain integrity is guarded by `AuditVerifier`'s dedicated tests rather than by a reconciliation rule.

**Audit log.** `AuditLogService` records operational events (collection, report generation, any write). Entries are hash-chained so integrity is verifiable independently of the application, which lets the log serve as evidence in a dispute rather than merely as debugging information.

**Consistency.** The interface, HTML report, CLI JSON and local API all consume the one `HardwareFact` model (3.1). Consistency is architectural rather than conventional: adding a fact requires no per-output code, so no output can be forgotten. This is the direct benefit of the single model and the main reason the output layer has the fewest defects in this project.

## 21. AI commentary and the data boundary

The AI commentary feature hands a local hardware summary to a language model for commentary. It supports Ollama (local models) or any OpenAI-compatible endpoint; prompts are configurable; endpoint and key are user-configured (`SharedAiEndpoint` for endpoint sharing, `AiChatStore` for conversation state, `AiTools` for tool-calling surfaces).

Because this is one of the few features that generates network traffic, the data boundary is explicit: it happens **only** when the user invokes it, with no background calls or automatic uploads; the endpoint is user-specified, so with a local model nothing leaves the machine; and what is sent is a summary of facts, not raw data or file contents.

Why keep it at all, in a tool whose central principle is refusing to draw conclusions? Because **interpretation and measurement are separated**. The tool's job is not to conclude at the measurement or reconciliation layers, but after the facts are obtained the user still needs an explanation of them. AI commentary plays the role of a second opinion: its output is explicitly labelled as generated, sits in a different block of the report from the fact list, and is referenced by no rule or verdict. In other words, the feature is designed so that it cannot influence the fact layer — it is a consumer, never a producer. That is the project's rule for generative features: they may exist, but they must sit at the end of the dataflow and their output must be identifiable.

---

# Part V — Engineering

## 22. Quality assurance

This build's test project contains 433 files with 4,568 `[Fact]`/`[Theory]` markers, organised to mirror the code: each service module has a corresponding test file, each decoder has golden-vector tests, each rule has per-scenario behaviour tests. The full list is Appendix F.

**Six complementary kinds of test**, each catching a different defect class.

*Unit tests* verify a module's output for given input — fast, precise, scalable, but limited to the input combinations they exercise. *Golden-vector tests* verify a decoder's understanding of the *specification*: the input is a hand-computed byte sequence and the expectation comes from the document, which lets them catch "implementation and test both misread the spec", something unit tests cannot. *Mutation tests* verify the tests' effectiveness by making small semantic changes to the source and checking that tests fail. *Guard tests* verify invariants rather than features. *Performance-budget tests* verify against time and resource limits. *Runtime reconciliation tests* verify that keys produced at runtime agree with the compile-time catalogue, which requires actually running collection and therefore costs more.

**Guard tests** deserve enumeration, since their targets are the defects that produce no failures: the fact-key catalogue (rescan and compare key by key); runtime key reconciliation (every runtime key is catalogued or matches a family prefix); zombie prefixes (every registered prefix has a producer); exemption reasons (none may be empty); decoder spec references (reflection-enforced); and dead probe wiring — a probe collects and is wired up but nothing consumes it. That last class is invisible to normal testing: it fails no test, produces no wrong output and barely affects performance, so the only mechanical defence is to check the wiring itself.

**Test efficiency matters** because a slow suite will not be run. Tests needing the file system or registry use injectable substitutes — for example, role collection accepts a probe delegate defaulting to the real WMI path, so tests inject a list. This separates "the logic of collecting facts", which is testable, from "accessing the source", which is transport.

**Conventions for new tests**: golden vectors for every decoder; three-state tests for every service (present, unreadable, not applicable) to prevent regime re-introduction of the 2.1 failure mode in individual modules; per-scenario tests for every rule, including the case of one required fact missing, verifying the engine's precondition; and a guard test for every new invariant, since an invariant without a mechanical check is only a comment.

**Limits.** Tests verify agreement between implementation and test, not between implementation and reality. They cannot cover hardware diversity — the project's development and validation are limited to hardware the development machine has, which is the root cause of the limitation in 25.2. They cannot validate methodology: a statistically correct but methodologically biased measurement (say, an uncontrolled condition) passes its tests; methodological correctness depends on domain knowledge. And the mutation target is 82%, not 100%: part of the code's semantics is not fully constrained by tests.

## 23. Performance engineering

**Measurement method.** Cold and warm starts differ substantially (JIT, driver loading, cache filling) and must be recorded separately. Each load method is timed individually, because choosing what to optimise depends on the ranking, which is only visible with per-item timing. And after any performance change, the produced fact-key set must be compared character by character with the previous set — the failure mode being that an optimisation changes collection behaviour so some facts are no longer produced, making performance look better by delivering less. Key-set comparison makes that class of "functionality for speed" trade invisible no longer.

**Completed work.** Recorded in `docs/ROUND-PERF-SIGCACHE-2026-10-12.md` and `docs/ROUND-PERF-DISK-BREAKER-2026-10-13.md`: signature verdict caching (signature verification is repetitive and expensive; the cache's correctness requirement is that the key covers every input affecting the verdict — content, certificate state, time — or stale verdicts result); the storage probe breaker (`StorageProbeBreaker` stops retrying after detecting a failure pattern, so one pathological source no longer drags down the load); and slow-source query guarding (a guard test ensuring an expensive source such as the optional-feature WMI query is queried once per load, guarding against several modules each querying the same source with cost growing linearly in module count and nobody noticing).

**Current load path.** About 5.6 s warm: `LoadPlatformFacts` ≈ 1,700 ms, `LoadSbom` ≈ 974 ms, `LoadAdvancedSupplement` ≈ 700 ms. That ranking drives optimisation priority.

**Parallelisation: measured, then declined.** The load path is I/O-bound and largely independent, making it a natural candidate. A controlled measurement placed 42 `Load*` methods in one process on one machine:

| Executions | Wall time | vs sequential |
|---|---:|---:|
| Sequential (current) | 6,384 ms | — |
| Degree 3 | 2,661 ms | −58% |
| Degree 6 | 2,200 ms | −66% |
| Degree 12 | 2,031 ms | −68% |

All three runs produced character-identical key sets (560 keys, set comparison equal), so parallelisation changed nothing about the output content. **It was nonetheless not adopted**, and the reasons must be recorded in full, because "measured an improvement and declined it" needs better justification than "did it".

*Reason one: a real ordering dependency whose output would become timing-dependent.* As described in 5.2, `LoadServerCompliance()` takes `RoleFacts` as input and the latter is produced by a later method. Sequentially, compliance consistently sees the previous load's values; in parallel it might run before or after the role facts are populated, making `role.compliance.*` timing-dependent and potentially different between runs on the same machine. **Trading unstable output for a 2× load improvement is a bad trade.** Parallelising requires first hoisting role facts ahead of the compliance check, which is the fix for that defect and an independent work item.

*Reason two: parallelising means adding concurrency to hardware probing.* Forty-five load methods running at once means several WMI queries and several driver IOCTLs in flight. WMI's concurrency behaviour, and the concurrency behaviour of the existing hardware accesses that cannot be cancelled once triggered, are not sufficiently characterised. Degree 12 threw no exception and the key set was unchanged, but a single measurement does not rule out races.

*Reason three: coordination cost.* The current 45 methods are a long chain of `try { … } catch { … }`, which keeps the marginal cost of adding a source very low. Converting to a list or parallel orchestration changes that feel — a factor that matters especially while several contributors are adding sources to that same method.

The record includes the measurement, the reasons for declining, and the conditions for revisiting: **once the ordering dependency is fixed**, parallelisation should be re-evaluated, subject to an unchanged key set, stable output across repeated runs, and validated concurrent hardware-probe behaviour.

**Constraints that outrank speed**: never lose facts for speed (key-set comparison); never trade unstable output for speed (23.4 reason one); never reduce honesty for speed (for example, skipping a source on timeout without marking `ReadError`); and caches must have correct keys.

## 24. Privacy and data sovereignty

**No collection, no upload.** No telemetry, usage statistics or crash reporting — `CrashLog` and `diag.log` are written locally and shared only at the user's discretion. Fact-set storage location and retention are user-controlled. Network traffic arises only from user-invoked features: AI commentary and update checks, both with configurable endpoints.

**Sensitive facts.** Asset identity fields (serials, SKU, asset tags) stay in the report, because they are key evidence in a dispute, but the tool draws no inferences from their content. Service lists can reveal software deployment, so PO-005 explicitly does not list service names. Endpoint policy facts (ZR-001, ZR-002) explicitly report status only and do not read identity data. The local API is constrained to anonymous machine identification.

**Data sovereignty.** `docs/DATA-SOVEREIGNTY.md` records the position: the user has full control over where their data lives, in what form and where it goes. Three implementations: local-first (facts and reports default to the local machine), open format (the capsule format is publicly specified in `docs/spec/snapshot.schema.v1.json`), and portability (data can leave the tool and remain interpretable afterwards). The third point matters: if data becomes unreadable once it leaves, the user does not really own it — its use is constrained by the tool. A published format separates ownership of the data from use of the tool.

**Auditability of writes.** All writes pass through `WriteGate` and enter the audit log, which the user can inspect and verify independently of the application (20.4). Data sovereignty extends to changes: not only does the data belong to the user, so must the record of changes to it be visible and verifiable.

## 25. Known limitations

The rule is **record on discovery**; unlisted limitations are not absent, merely undiscovered.

**Known defect.** The load-ordering semantic dependency (5.2): `LoadServerCompliance()` reads the previous load's role facts under sequential loading, producing an internal contradiction within one snapshot. Stable and reproducible rather than random; its interpretive effect is confined to the load in which a role changed. It is also one reason parallelisation was declined (23.4).

**Unverified on real hardware.** AMD-specific facts (SMU, PSP, SEV series) are implemented but unverifiable on the Intel development machine, with the exemption recording that a rule must be added when hardware is available and the exemption removed then. CXL facts likewise, with no CXL hardware. Platform-specific decoding is validated only against the hardware the development machine provides. These limits are registered per item rather than papered over by passing tests.

**Incomplete coverage.** Not all 844 static keys are considered by rules; those neither covered nor exempt form a known number (10.3) and are not cross-checked. The 562 exemptions cover many keys for varying reasons (Appendix D); the threshold is "cannot be crossed" rather than "hard to cross", and maintaining that threshold depends on review rather than mechanical guarantee. The mutation target is 82%, so part of the code's semantics is not fully constrained by tests.

**Intrinsic measurement limits.** Under a virtualisation layer, MSR, PCI configuration space and sensor readings come from the hypervisor and need not correspond to physical hardware; rule `R-CPU-04` warns, but a warning cannot change the nature of the measurement. Temperature and power measurements are time-dependent and their comparability rests on recorded conditions. Benchmark results for units such as NPUs depend heavily on model and precision. And some facts have only one source (14.3, Appendix D's single-source class), so their reliability ceiling is the source's own.

**Environment.** Windows x64 only. Some facts require administrator privilege (`FactCatalog` states this per item); without it their availability is `InsufficientPrivilege` and the tool states what would make them readable. Ring-0 features require a loadable driver, so they are unavailable where drivers cannot load (some managed endpoints, some systems with particular memory-integrity settings). Interface localisation is not complete for some long-form text.

**Deliberate non-goals** (1.4) are design choices rather than limitations, but their user-visible effect is similar: no unsolicited hardware state changes, no unsolicited network traffic, no fault diagnosis, no bypass capabilities, and no claim of completeness on any platform.

## 26. Version history

The full history is in `README.md` with per-round records in `docs/ROUND-*.md`. This section explains the structure rather than repeating the list.

Development proceeds in **rounds**. Each round addresses a related group of capabilities (a subsystem's depth, a source's stratification, a class of reconciliation) and produces a `docs/ROUND-*.md` record containing goals, implemented scope, measurement results and outstanding items. The distinguishing feature is that **investigation itself is documented**: the parallelisation measurement's value lies not only in its conclusion but in its record of what was measured, why it was declined and what would change the answer, so that later contributors need not rediscover a rejected path.

Since v2.70 the key counts in the version history use a **reproducible baseline** — numbers produced by scanning the source rather than counted by hand — for the same reason as 10.1: hand-counted numbers drift from the code, reproducible ones can be re-verified at any time. For v2.84: 844 static keys, 23 dynamic families, 18 rules, 562 exemptions, 416 service modules, 433 test files, 4,568 test markers, 51 documents. The progress marker for this round is item 547 (file-system metadata micro-latency), reflecting a project that tracks capability accumulation by numbered item alongside the two-letter specification IDs (NG-002, ZR-004 and so on).

## 27. Roadmap

**Corrections.** Fix the load-ordering semantic dependency (5.2, 25.1) by hoisting role facts ahead of the compliance check. Re-evaluate parallelisation afterwards (23.4), subject to an unchanged key set, stable output across repeated runs, and validated concurrent hardware probing. Verify AMD and CXL facts on real hardware, then add their rules and remove the exemptions.

**Expansion.** Grow knowledge packs toward more externally maintainable judgement bases, so the tool's judgement can track external knowledge without a new release. Converge the methodology of measurements still evolving (invisible-stall attribution among them), because convergence affects report comparability. Process uncovered keys gradually — rules where crossing is possible, exemption reasons where it is not — with the goal being not a coverage number but **an explicit state for every unchecked key**.

**Not planned.** No REST example set (XT-003, explicitly declined); no background service or scheduled task (AG-005); no default-on HTTP listener shell (17.7); no offensive capabilities (1.4). Recording "will not do" alongside "will do" is as important as the roadmap itself, because it stops future contributors from unknowingly re-introducing a rejected design.

## 28. Licence, contributions and acknowledgements

**Licence.** AGPL-3.0-or-later. The choice relates to the tool's nature: it loads a kernel-mode driver and accesses hardware, so its code needs to be reviewable. AGPL ensures that derivative services remain open, so users — including those using it as a network service — can inspect the code actually executing. Third-party licence information is in `THIRD-PARTY-NOTICES.md`.

**Contributions.** Start with `docs/AGENT_CONTEXT.md` and the contributors' section of `README.md`. Four specific requirements: new facts must update the catalogue (mechanically enforced, 22.4); new facts must have their coverage state resolved — a rule where crossable, an exemption reason otherwise, and empty reasons are not accepted; new decoders must carry specification references (reflection-enforced); and new sources must handle the unreadable case via `Diag.Swallow` with the user-visible effect stated, never defaulting to a filler value.

**Stance.** Contributors should hold to the same principles. The three most important: unreadable must never be disguised as absent; a conflict is more useful than a conclusion, because the former can be re-checked while the latter can only be trusted; and uncertainty must be presented alongside the value.

**Acknowledgements.** The project builds on open-source work including the LibreHardwareMonitor sensing engine and WebView2, and on the hardware-access and printing paths built above them. It also depends heavily on real-hardware testing: many design decisions trace directly to behaviour observed on specific hardware — SMBus concurrency, fixed-value Super I/O sensors, uncancellable device IOCTLs, firmware counter resets. Those observations cannot be derived from specifications; they only come from use.

---

# Part VI — Fact domains

## 29. Processor, chipset and platform identity

This domain spans all three source layers, so cross-layer reconciliation is possible within it — which is why it is among the most valuable in verification.

| Source | Access | Trust | Content |
|---|---|---|---|
| CPUID | User mode | `Measured` | Vendor, family, model, stepping, brand string, feature flags, cache configuration, topology, base frequency (leaf 0x16) |
| MSR | Ring-0 | `Measured` | Per-core microcode revision, multipliers and frequency state, power-management registers, temperature limits |
| SMBIOS Type 4 | Firmware | `Reported` | Processor description, nominal frequency, core and thread counts |
| WMI | OS | `Reported` | Processor name, core and thread counts, cache summary |

Platform identity comes from `CpuIdService`, `CpuTopologyService`, `ChipsetAnalysisService`, `MotherboardAnalysisService` and `PciResourceAuditService`. Topology has three sources — CPUID leaves 0x0B/0x1F, the ACPI PPTT table, and OS APIs — and divergences between them carry diagnostic meaning, since OS topology determination disagrees with hardware in some configurations. Microarchitecture inference (`MicroarchProfile`) supplies expected cache configuration and frequency characteristics as a basis for other checks, with the evidentiary caveat that its reliability depends on the model table's completeness: when a model is absent, dependent checks must report undecidable rather than fail.

**Frequency is three distinct facts.** The *claimed base frequency* (`CpuBrandClaimedMhz`) comes from CPUID leaf 0x16 and is `Measured` yet self-reported. The *silicon-derived base frequency* (`CpuSiliconBaseMhz`) is `Derived` from the maximum non-turbo multiplier and BCLK. The *actual operating frequency* is `Measured` from performance counters or MSR frequency state. Comparing the first two is rule `R-CPU-05`: model relabelling requires editing the brand string and the nominal frequency, while the silicon's multiplier table is not easily modified, so disagreement is strong evidence. Microcode revision is read per core via MSR (`R-CPU-06`).

**Security features and their observation.** Virtualisation extensions, trusted execution technology, memory encryption (TME/SME), trust domain extensions (TDX and the SEV series) and side-channel mitigations. The interpretive point is the separation of *supported* from *enabled*: `*.supported` and `*.enabled` are independent facts, hardware support without firmware enablement is common and important, and conflating them makes an audit report claim a capability the machine does not exercise. Modules: `CpuSecurityService`, `CpuMsrFacts`, `PlatformSecurityMsrFacts`, `TmeFactsService`, `AmdSecurityFactsService`, `CveOfflineFactsService`.

*Limits.* Microarchitecture-table completeness; AMD facts unverified on real hardware; and under a hypervisor, CPUID and MSR readings come from the hypervisor, with the correspondence to physical hardware decided by it.

## 30. Memory subsystem

| Source | Access | Trust | Content |
|---|---|---|---|
| SPD (SMBus) | Driver | `Measured` | Module manufacturer, part number, serial, week, rated timings, capacity and organisation, temperature sensor |
| TSOD (SMBus) | Driver | `Measured` | Module temperature |
| SMBIOS Type 16/17 | Firmware | `Reported` | Array maximum, slot count, declared error-correction type; per-module device description |
| WMI | OS | `Reported` | Total capacity and some configuration |
| Memory controller registers | Ring-0 | `Measured` | Configured frequency and primary timings |

Both a hardware layer (SPD) and a firmware layer (SMBIOS) describe the same things, so the cross-layer conditions for reconciliation are complete.

**SPD decoding** involves version differences: DDR3, DDR4 and DDR5 use different structures and mechanisms (DDR5 adds a different data structure and page mechanism). Decoders must therefore determine the version before applying rules — the failure mode being that decoding with the wrong version's rules produces plausible yet entirely wrong values. The defence is validating structural features first (a fixed byte signature for DDR4, structure markers for DDR5) and reporting undecidable on failure. `SpdConsistencyAuditService` checks whether modules' SPD contents are mutually compatible, diagnosing instability from mixing mutually incompatible timings.

**Interpretation.** Configured versus rated speed is `R-MEM-03`, with causes including BIOS settings, platform limits and mixed modules; the tool states the gap without inferring the cause. ECC is verified through two independent paths — physical device width exceeding data width (hardware, SPD) and the array's declared correction type (firmware, SMBIOS) — giving `R-MEM-05`, the highest-value rule in this domain because material cost differences hinge on it. Array declarations versus installed totals give `R-MEM-04`; module batch and serials give `R-MEM-01` and `R-MEM-02`.

**Latency and bandwidth.** `MemBandwidthService`/`MemBandwidthMath`, `LatencyCurveService`/`CoreLatencyService`, `CacheBenchService`/`SlcCacheBenchService`, `DramTrafficService`/`DramTrafficDecoder`, `LargePageService`/`LargePageDecoder`. Self-validation requirements apply (13.1), and latency curves must record working-set size, since that dependence is the domain's most important characteristic.

**Reliability and attack surface.** `MemoryTestService` (data integrity, a correctness item in the deep-test centre), `MemoryTruthService` (aggregation layer for this domain's identification facts), `MemoryAttackSurfaceService`, `RowhammerProbeService` and `MemoryEncryptionFactsService`.

*Limits.* SMBus is shared, so other monitoring software may access it concurrently; `SmbusBusLock` provides in-process exclusion but cannot stop other processes, and unstable readings are presented as `ReadError`. Some modules' SPD sits in writable EEPROM, so the reliability ceiling is set by physical protection, and a tool cannot tell from the value whether it was modified — an intrinsic single-source limitation. Under virtualisation, physical memory configuration is abstracted and SPD facts are typically unavailable.

## 31. Storage and file systems

Sources: ATA IDENTIFY (model, serial, firmware revision, addressable LBA total, rated rotation rate, ACS version, command set support), ATA SMART (the full attribute set), NVMe identify and health logs (percentage used, data units written, power-on hours, power cycles, unsafe shutdowns, critical warning, media errors), NVMe logs (error log, SMART history, firmware slot information), SMBIOS/WMI enumeration, and file-system APIs.

This is the evidence-richest domain for verification, because the persistent records contain cumulative counters, which are hard to reset without trace.

**Identification.** `DiskInfoService`, `AtaIdentify`, `NvmeHealth`, `EnterpriseStorageFactsService`. Three interpretive points: model and firmware revision come directly from the device, allowing comparison against labelling; capacity exists in two forms — claimed and addressable from LBA total and sector size — whose gap is `R-SSD-05` (HPA/DCO detection, with `ChassisAndHpaFactsService` and `DiskFullFactsService`); and device class is established by comparing the rated rotation rate against whether the SMART attribute set contains mechanical-only attributes (`R-SSD-06`).

**Health.** `StorageSmartService`, `StorageReliabilityFactsService`, `SmartFailingNowFactsService`. The interpretive core is internal consistency of cumulative counters: hours versus bytes written (`R-SSD-01`, detecting resets since controllers need not reset both counters together); percentage used versus bytes written (`R-SSD-02`, the "0% used" claim being the commonest refurbishment claim); unsafe shutdowns versus power cycles (`R-SSD-03`, a logically impossible relationship and therefore more reliable than estimating a normal range); and the critical warning bits (`R-SSD-04`, the device's own statement, uninterpreted). Per-disk counters form the `storage.reliability.` dynamic family at nineteen fields per disk.

**File systems and latency.** `FileSystemVolumeFactsService`, `NtfsVolumeFactsService`, `VolumeService`, `MountedDevicesFactsService`, plus `DiskLatencyStats`, `FsMetadataLatencyService`, `DiskBenchService`. The value here is diagnosing "healthy device, poor experience" — typically SMR drives, whose SMART attributes are entirely normal while sustained write performance collapses, a problem only file-system-layer measurement reveals. `ChassisAndHpaFactsService` also notes that the HPA/DCO capacity gap *is itself* the claimed-versus-addressable reconciliation, already contained, needing no separate rule.

**Forecasting and its boundary.** `BaselineForecastFactsService` and `BaselineLearningService` extend observed trends; their output is extrapolation of what has been observed, not failure prediction. Drive failure prediction is only partly reliable in both research and practice, so the position is to supply re-checkable values and trends rather than a "this drive will fail within N days" conclusion.

*Limits.* Some SMART attributes have vendor-defined semantics, so raw numbers and values are shown for cross-reference. Controllers may maintain counters on particular events (resetting some counters on power cycles), so absolute values need not reflect the device's full history — which is precisely why rules judge by ratios and logical relations rather than absolute values. Above Storage Spaces or virtual disks, device-layer facts have no one-to-one relationship with logical volumes, so capacity and health must be read at both physical and logical levels.

## 32. Display and graphics

Sources: PCI configuration space (device identity, BAR resources, link width and speed, capability registers, ReBAR state), EDID (monitor model, serial, supported resolutions and refresh rates, colour characteristics, HDR capability), NVML/NVAPI (temperature, power, clocks, memory usage, error counters — `Reported`), WMI enumeration, and performance counters/ETW.

Because the vendor library's data is `Reported`, reconciliation in this domain uses PCI configuration space (`Measured`) as the baseline, with vendor data serving presentation and performance analysis rather than identification verdicts.

`DisplayAdapterFactsService`, `GpuAnalysisService` and `GpuDeepFactsService` cover adapters, with membership enumerated by WMI (dynamic family `display.adapter.`). Link verification matters most here, since graphics cards are the commonest victims of down-negotiation: `PcieLinkService`/`PcieLinkDecoder`, `PcieAnalysisService`, `PcieVerifyFacts`, `PciCapabilityFactsService`, with `R-LNK-01` taking the worst link in the machine — causes being slot wiring limits, power-management settings, signal integrity and riser quality. ReBAR (`ResizableBarService`/`ResizableBarDecoder`) is decided by three conditions — hardware support, firmware enablement, driver support — so the tool reports each condition's state rather than a single enabled/disabled.

Monitors are handled by `EdidService`, `MonitorFactsService`, `MonitorConnectionService` and `DisplayLinkService`: identity from EDID, connection interface (`VideoOutputTechnology`), supported modes, and colour/HDR capability (`HdrCapabilityService`). Two practical points: EDID content may be incomplete or untrustworthy, since adapters and switches sometimes supply synthetic EDID; and in multi-monitor configurations the facts are per monitor, hence two dynamic families (`monitor.` and `mon.`). The connection interface's exemption notes a single source.

Graphics pipeline measurement: `FrameTimeService` (per-frame times, output as distributions rather than averages, because stutter is a tail phenomenon), `GpuRenderTestService` (synthetic load), `GpuStressService` (stability), and `GpuTdrFactsService` (timeout-detection-and-recovery events, valuable because the OS event log is not the vendor library's self-report). `CudaService` and `GpuCodecService` cover compute and codec capability, reporting `NotSupported` when the vendor library or version is absent rather than inferring an alternative.

*Limits.* Vendor library fields vary with version; synthetic EDID; virtual display adapters in virtual machines; and multi-GPU configurations needing per-card versus machine-wide distinction.

## 33. Networking

Sources: PCI configuration space (controller identity, link width and speed), NDIS/WMI (interface configuration, advanced properties, offload capabilities, counters), OS APIs (link state, speed, connection profiles, DNS, routing), performance counters, and **active measurement** by the tool itself.

Active measurement distinguishes this domain: some facts cannot be obtained passively, so traffic must be generated, and therefore strict data-boundary constraints apply (33.4).

`NetAdapterService`/`NetAdapterDecoder`, `NicLinkFactsService`, `NicHealthFactsService` and `NetOffloadFactsService` cover interfaces, link state, negotiated speed, health counters and offload capabilities. This domain's exemption is worth quoting: a NIC "shortfall" is already placed side by side — PCIe and line sides — in the interpretation layer, and the two are *supposed* to differ (a PCIe 3.0 x1 link exceeds 1 GbE bandwidth), so the difference is not a conflict. Binding them into a rule would create systematic false conflicts.

`NetworkService`, `DnsService` and `NetworkSystemInventoryFactsService` cover IP configuration, DNS, routing and connection profiles; `ListenPortFactsService` inventories listening ports as the `net.listen.` family. The interpretive point is distinguishing *listening* from *reachable*: a port in a listening state does not mean its service responds correctly, nor that the service is the expected one. The tool states the former and does not infer the latter.

**Active measurement and the data boundary.** `NetworkSpeedService`, `LoopbackSignalService`, `Bufferbloat` and jitter (TU-008). Targets must be user-specified on the same opt-in allowlist as `ms.ping`; with no target, **no traffic and no zero**. Bufferbloat measurement works by measuring latency change under load, so it must generate load, and the network impact must be disclosed before execution. Wireless facts (`WifiSignalService`, `BluetoothBatteryService`) are highly variable, so their history matters more than a single reading.

*Limits.* Active measurement results depend on every hop in the path, so they cannot be attributed to one local component; advanced properties and offload capabilities are driver declarations; and virtual network interfaces do not reflect physical hardware.

## 34. Audio and multimedia

Facts come mainly from OS audio APIs (MMDevice enumeration, IAudioClient configuration) and related hardware controllers, mostly `Reported` because the audio path passes through several layers of abstraction. `AudioEndpointFactsService`, `AudioLatencyService`, `AudioSpectrumService` and `MultimediaFactsService` cover endpoints, latency, spectrum measurement and subsystem aggregation; endpoints form two dynamic families (`audio.endpoint.`, `audio.latency.`).

The interpretive point is that **the relationship between buffer size and latency is arithmetic, not reconciliation** — the recorded exemption: the endpoint buffer is an IAudioClient configuration result, a single source, and its relation to sample rate is arithmetic (milliseconds = frames ÷ sample rate). That distinction separates two kinds of relationship: arithmetic ones, where one value determines another and reliability rests entirely on the input, and reconciliation ones, where value comes from the independence of multiple sources. Audio latency is used alongside `DpcLatencyService` to diagnose audio glitching, since the cause may be an undersized buffer, an overlong driver DPC, or a delay from power-management frequency transitions — distinguishable by different measurements.

`MediaProtectionFactsService` covers content protection state, with the same supported-versus-enabled distinction as 29. `CameraFactsService` covers camera enumeration; its exemption notes a single source with no second path — a structural property of multimedia device enumeration, not a tool defect, meaning the reliability ceiling is set by the OS enumeration mechanism.

*Limits.* Layer accumulation in the audio path; the time-sensitivity of device enumeration; and virtual or redirected audio devices in remote sessions and virtual machines, whose latency characteristics differ from physical hardware.

## 35. Power, thermal and fan control

Sources: MSR (power state, frequency state, temperature limits), RAPL energy counters, Super I/O (fan speeds, temperatures, voltages), the battery API (`Reported`: design capacity, full-charge capacity, charge state, cycle count), PMBus on the PSU, UPS devices, and power-policy APIs.

This domain has a **power-conservation crossing condition**: input power (PMBus or RAPL) should satisfy a conservation relation with the sum of component power. That cross-check is planned but not implemented.

`PowerPolicyService`/`PowerPolicyDecoder` decode power plans (processor performance state limits, core parking, sleep and hibernate settings). The interpretive point: power policy is a *configured* value, not a measured one, so a configured performance floor may be unreachable because of thermals or power limits, and a configured ceiling may be ignored by the platform — the difference between configured and measured is what carries information. `PpttFactsService` handles the ACPI PPTT table (cache and power-domain hierarchy per level), more valuable on hybrid processors where performance and efficiency cores differ in power-domain layout. `CppcCapabilityFactsService` covers collaborative processor performance control, where enablement requires firmware, OS and processor support together, and results from CPPC and traditional ACPI P-state control are not directly comparable.

Sensors and fans: `SensorService` with `SensorService.Fans`, `FanControlRow` and `FanCurveService`; `CoreTempMapService` for per-core distribution; `ThermalStickyService` for stuck readings; `SensorSanity` for credibility. `SuperIoProbeService` and `SuperIoHwmFactsService` handle probing and hardware-monitor decoding, registered as the `sio.0x` and `sio.hwm.0x` families because the ports actually found (0x2E/0x4E or others) are platform facts, not compile-time constants. Fan-curve application is a write operation, so it passes the write gate and needs consent, with the affected fans and temperature ranges stated.

**Power sources.** RAPL energy counters are cumulative and overflow, so power is computed from differences over an interval with overflow and counter-reset handling. PMBus is unique in being a *system-external* measurement: component energy comes from internal counters, whereas the PSU measures the machine's actual input, and the two make the conservation cross-check possible. That check is unbuilt, and the note in the coverage report is explicit — "psu.: PMBus mains vs RAPL vs NVML can be crossed (the blueprint's power-conservation reconciliation)" — showing that *crossable but not yet crossed* is distinguished from *not crossable and exempted*.

Battery facts come from the Windows battery API, a single source, with `R-BAT-01` doing the wear judgement. UPS facts confirm the presence of protection rather than assessing capacity.

*Limits.* Sensor credibility rests on heuristic rules; temperature and power are time-dependent; Super I/O models and register layouts are platform-dependent, with probe failures reported as `NotSupported`; and the power-conservation check is unimplemented.

## 36. Peripherals and buses

Facts here are mostly `Measured`, since PCI/PCIe configuration space and USB descriptors come directly from devices.

| Sub-domain | Modules | Content |
|---|---|---|
| PCI enumeration | `Bus0InventoryService`, `PciResourceAuditService` | Devices on the bus, BAR resources and conflicts |
| PCI capability | `PciCapabilityFactsService`, `BusCapabilityFactsService` | Supported capabilities and their configuration |
| PCIe link | `PcieLinkService`, `PcieLinkDecoder`, `PcieAnalysisService` | Per-device link width and speed versus capability |
| PCIe errors | `EcamAerService` | Advanced Error Reporting accessibility and state |
| USB topology | `UsbTopologyService`, `UsbLinkService`, `UsbLinkDecoder` | Controller/hub tree, per-port negotiated rates |
| Thunderbolt/USB4 | `TbUsb4Service` | Negotiated link and security level |
| Input devices | `MousePollRateFactsService` | Mouse polling rate |
| Other | `CameraFactsService`, `ServerRoleFilePrintFactsService` | Cameras, printing devices |

PCI devices are enumerated as the `pci.dev.` and `pci.res.` families, since membership comes from bus enumeration. `PciResourceAuditService` audits resource assignment — BAR addresses, sizes and overlaps — with **resource conflicts** the core diagnostic, stated in terms of which devices and which address ranges so the user can act on BIOS settings. `BusDeepDiveFactsService` and `BusCapabilityFactsService` cover capability structures.

USB topology (a single source per its exemption) and per-port link negotiation: the interpretive point is comparing negotiated rate against device capability, an attachment to a slower port yielding the slower rate — a common case with chassis front-panel cabling. `TbUsb4Service` covers Thunderbolt/USB4 including its security level, which determines which devices may attach and is therefore relevant to chapter 14. `EcamAerService`'s point is that AER availability depends on the platform exposing ECAM access and enabling AER, so "cannot read AER state" and "no AER errors" must be distinguished.

*Limits.* Enumeration is time-sensitive under hot-plug; bridged or switched links require per-segment distinction; negotiated USB rates depend on cable, port and device together, so a shortfall does not implicate one cause; and enumeration-type facts are often single-source.

## 37. Operating system, software and updates

This layer is `Reported` and the most readily rewritten (7.3).

| Sub-domain | Modules | Content |
|---|---|---|
| Services and startup | `ServiceInventoryService`, `ServiceBootFactsService`, `StartupService`, `ScheduledTaskStartup` | Service enumeration and configuration, startup items |
| Drivers | `DriverAnalysisService`, `DriverAuditService`, `DriverAuditDecoder`, `DriverInspectionFactsService` | Enumeration, signature state, static inspection |
| Device install history | `SetupApiTimelineService` | Timeline parsed from device installation records |
| Events and reliability | `EventLogSummaryService`, `EventsService`, `ReliabilityHistoryService`, `ErrorAggregationService` | Event summaries and aggregation, reliability history |
| Crash analysis | `BsodService`, `WheaErrorService`, `WheaTimelineStore`, `MinidumpFactsService` | Dump parsing, hardware error events, error timeline |
| Updates | `WindowsUpdateHistoryService`, `UpdateCheckService`, `UpdateRetryFactsService` | Update history and state |
| Package management | `WingetService` | Package manager state |
| Software inventory | `SbomService`, `AssetFactsService` | Software bill of materials, asset facts |
| Licensing | `LicenseService`, `LicenseDecoder` | Licence state |

Driver observation has three levels — enumeration, signature state and static per-file inspection (the `drvinsp.` family) — with the value being identification of suspicious drivers (unsigned, anomalously signed, or in unexpected locations), which differs from `ByovdCompareService`'s blocklist comparison.

`SetupApiTimelineService` parses device installation records taking only language-neutral fields (markers, device instance ID vocabulary, timestamp formats, pinned by tests), so localised labels do not break parsing. Its value is timeline reconstruction: an anomalous device install or driver change leaves a timestamp comparable with other timelines.

Event handling is summarised rather than dumped, since a full event log dump is useless to a user — with the exemption noting that the summary's source *is* the event log, so self-reconciliation is meaningless. The same principle runs through the exemption list: comparing a summary with its own source is not reconciliation, because they are not independent sources. Crash and hardware-error handling (`BsodService`, `WheaErrorService`, `WheaTimelineStore`, `MinidumpFactsService`) is valuable chiefly through **timeline correlation** of WHEA events, crash events and device install history — locating when a problem began, which a static fact list cannot provide.

`SbomService` produces a software bill of materials, with source traceability required (registry, package manager or file system). `AssetFactsService` provides asset facts whose identity fields are exempt. `WindowsUpdateHistoryService` and `UpdateCheckService` provide update history and state, whose value is confirming **build and patch level**, since a machine's security posture relates directly to patch level, which update history establishes rather than version number implying.

`SelfIntegrityFactsService` and `SelfTelemetry` provide the tool's own integrity facts — process signature state, loaded modules — another instance of the stance that the tool's own trust conditions are themselves facts.

*Limits.* OS-layer rewritability; event logs can be cleared with or without audit traces, so "no errors" is not "no errors ever happened"; install-record parsing depends on the file format; and inventory completeness depends on source completeness, since software not registered in the registry will not appear.

## 38. Security policy and endpoints

Facts here sit at the endpoint and policy level, sourced mostly from the registry and management registrations. `LocalSecurityAuditService` and `AuditPolicyService` cover local security and audit policy, whose interpretive point is that **a policy existing does not mean events are recorded** — policy must be read alongside the audit subsystem's actual operating state, a recurring pattern in this domain where configuration and behaviour are two different things. The exemption notes audit policy facts come from LSA and the event log, a single source.

`SecurityAuditFactsService`, `SecurityAuditDeepFactsService` and `SecurityAuditSupplementFactsService` form a three-layer audit, layered by acquisition cost and depth. `EndpointPolicyFactsService` and `DeviceComplianceFactsService` cover endpoint policy conformance, with the compliance-baseline constraints of 14.5 (unavailable data must not masquerade as a pass; the snapshot date must be printed; item text is not listed). `ZeroTrustFactsService` and `TrustOpsFactsService` provide registration state (MDM, Entra join, Passkey support) with the shared constraint of reporting status without reading identity data.

Data protection: `VolumeEncryptionFactsService` and `BitLockerRecoveryFactsService` (whose practical point is that *having* recovery information and having *usable* recovery information are different things — whether the key is escrowed to a directory service, printed, or only on the machine determines recoverability after hardware failure, and the tool states escrow state without stating the key itself), and `MediaProtectionFactsService`.

Execution traces: `ShimcacheFactsService`, `PrefetchFactsService` and `RecycleBinFactsService` reconstruct what the machine has run, with the interpretive point of **trace lifetime and overwriting** — capacity is limited, older entries are overwritten, so a trace list reflects recent activity rather than full history. The tool states content and its time range and does not infer that absence means it never happened. Certificate store enumeration is a single source, valuable for judging trust configuration state and, with the audit log, forming the evidence of "this machine's trust state".

*Limits.* Single sources throughout; high rewritability; incomplete traces; and the separation of policy from behaviour.

## 39. Diagnostics, boot and environment

| Sub-domain | Modules | Content |
|---|---|---|
| Boot | `BootTimingFactsService`, `BootBreakdownService`, `BootBreakdownDecoder`, `UefiBootFactsService` | Boot time decomposition, boot options, boot stages |
| Sleep and power transitions | `SleepDiagnosticsService` | Sleep and wake diagnostics |
| Freezes and stalls | `FreezeDiagnosisFactsService`, `InvisibleStallService`, `InvisibleStallDecoder` | Freeze diagnosis, invisible-stall measurement |
| Latency and real-time | `DpcLatencyService`, `TimerFoundationService` | DPC latency, timer foundation |
| Time | `TimeSyncFactsService`, `CoreTimeService`, `CoreTimeDecoder` | Time source and sync state, core timer decode |
| Environment | `EnvCheckService`, `BackendEnvironmentService`, `EnvironmentDiagnosticsFactsService` | Execution-environment checks |
| Platform depth | `PlatformDeepDiveFactsService`, `PlatformExpansionFactsService`, `TopologyDeepenFactsService` | Deeper platform facts |
| Self-diagnosis | `SelfIntegrityFactsService`, `Diag`, `CrashLog` | Tool integrity and diagnostic records |

**Boot time decomposition** is the most practically valuable feature here: firmware phase timings from the ACPI FPDT table's decoded timestamps, plus OS-phase timings from boot records and service start times. The value is localisation — firmware settings and device initialisation are handled quite differently from driver and service startup — and `BootBreakdownDecoder` is a pure function guarded by golden vectors and mutation testing.

**Invisible stalls** measure scheduling delay: the difference between when a thread should have run and when it did. The distribution reflects the system's real-time quality without depending on assumptions about the stall's cause — necessary because conventional metrics look normal during such stalls. Freeze diagnosis is more severe, resting on correlating event logs, hardware error events and crash dumps.

**Time and timers.** Time-sync state (source, synchronisation, offset) matters because report timestamps, event-log correlation and measurement intervals all depend on system time. Timer foundation facts (high-precision event timer availability and resolution) relate to condition recording, since timer resolution sets the lower bound of timing precision. Core time facts are exempt as single-source measurements.

**Environment and platform depth.** Environment facts (OS build, privilege level, required component presence, driver load state) exist to *explain the absence of other facts*: when a fact's availability is `NotSupported` or `InsufficientPrivilege`, environment facts supply the full context.

**Self-diagnosis.** `SelfIntegrityFactsService`, `Diag` and `CrashLog` make the tool's own trustworthiness inspectable: a user can look up which features were degraded rather than merely seeing fields without values.

*Limits.* These facts are cross-cutting, so attribution points toward a subsystem without confirming it; timing measurements are sensitive to load and system state; and platform-dependent sources (FPDT, WHEA) may be unavailable, in which case the corresponding decomposition and timeline cannot be built.

## 40. Conclusion

The document's central claims can be reduced to a mutually supporting set. **The value of a fact lies in its verifiability**: a conclusion that cannot be re-checked is worth little more than no conclusion, which is why the output form is the combination of source, value, trust level and availability rather than a single number or grade. **Unreadable and absent are different conclusions**, the core of every principle and violated by filling in zeros, typical values or cached values, or by reading a failed scan as an empty set — all of which are defects in this project, not trade-offs. **A conflict is more useful than a conclusion**, because the former can be re-checked. **Uncertainty must accompany the value**, since a number without uncertainty is unusable professionally. And **quality requirements must be mechanised**: the key catalogue, coverage reporting, specification references, guard tests and mutation testing all convert requirements that once depended on human attention into checks that fail automatically.

The costs are stated just as plainly: performance (the load path's ~5.6 s partly reflects these requirements, and must be monitored); feature velocity (the whitelist driver, reasoned exemptions and specification annotations all raise the bar for additions); incomplete coverage (deliberately accepted, compensated by making the uncovered number visible); platform breadth (AMD and CXL implemented but unverified, registered rather than claimed); and deferred parallelisation (measured at a 66% improvement and declined, because it would make output timing-dependent — an unstable output is not worth twice the speed).

The appendices are generated from the version-controlled sources, so the figures quoted here — 844 static keys, 23 dynamic families, 18 rules, 562 exemptions, 416 service modules, 433 test files, 4,568 test markers, 51 documents — can be regenerated and diffed at any time. The methodology has its own documents: `docs/MEASUREMENT-METHODOLOGY.md` and `docs/spec/METHODOLOGY.md`, with data sovereignty in `docs/DATA-SOVEREIGNTY.md`, the query language in `docs/spec/query-language.v1.md`, the snapshot format in `docs/spec/snapshot.schema.v1.json` and known limitations in `docs/spec/LIMITATIONS.md`.

The final recommendation to users is the same as the first sentence of this whitepaper. The tool's output is **evidence, not judgement**. Use it by obtaining checkable facts, attending to each fact's source and availability, following up conflicts yourself, and combining the facts with what you know about the situation when a conclusion is needed. The tool lays out what the machine says and what the machine actually reveals; the judgement remains the user's.

---

# Part VII — Engineering practice

## 41. Test strategy

The six test kinds of chapter 22 serve different defect classes: unit tests find logic errors; golden vectors find decoding errors, because their expectations come from the specification rather than the implementation; mutation tests find ineffective tests; guard tests find broken invariants; performance-budget tests find regression; and runtime reconciliation tests find integration mismatches.

Scale relationships matter: 4,568 markers across 433 files (about 10.5 per file) reflects both liberal parameterisation with `[Theory]` and multi-faceted testing of each module (normal, boundary and failure paths as separate methods). Test execution speed is a design constraint, since a slow suite is not run: injectable probes replace file system and registry access, hardware-dependent tests are excluded from unattended runs, and pure decoders are isolated so they can be tested without loading the application.

New-test conventions: golden vectors for every decoder; three-state tests for every service; per-scenario tests for every rule, including a missing required fact to verify the engine's precondition; and a guard test for every new invariant. The limits are as stated in 22: agreement between implementation and test is not agreement with reality; hardware diversity is not covered; methodology cannot be tested; and the mutation target is 82%.

## 42. Decoders and specification references

Decoding is the one pipeline stage that can be tested entirely without an operating system or hardware, so it receives the project's heaviest quality investment. Its isolation into `XinSpect.Decoders` has three motives: mutation testing is feasible only if decoders can be exercised without loading the application; golden-vector tests need no environment; and the boundary stays clean — decoders must not perform I/O, and I/O modules must not decode.

Five rules derive from experience. **Validate structure before applying rules** — the general form being that a decoder's first step is deciding whether this is something it can decode at all. **Missing fields must have somewhere to record absence.** **Carry specification references**, enforced by reflection. **Do no cross-field semantic inference**: a decoder converts bits to fields per the specification, while format-level inference (where a field's presence changes how another is read) is permitted because it is part of the specification. **Do not discard unexplained bits**, since they may be meaningful — a new field, a vendor extension, or evidence of modification.

The main decoders and their bases are tabulated in the Chinese edition (42.4), covering JEDEC SPD for DDR3/4/5, NVMe base specification logs, ATA/ATAPI IDENTIFY, SMBIOS structures, ACPI tables (FPDT, PPTT, HEST, BERT, SLIT, CEDT, MCFG, HPET and others), PCI Express link and resizable-BAR capability structures, USB port state, ACPI FPDT and OS boot records, platform timer specifications, power management models, and vendor-published ME region descriptions — alongside several project-defined measurement methods (invisible stall, ceiling, machine age), whose `SpecRef` points at the project's own methodology. The distinction matters because **what can be re-checked differs**: an external specification validates the former, methodology and self-validation the latter.

Decoder tests take three forms: single-field golden vectors; full-structure golden vectors from specification examples or captured hardware reviewed by hand; and boundary/abnormal input tests (empty, truncated, mismatched structure, extreme values) verifying structure validation and missing-field marking.

## 43. Failure-mode taxonomy

Failures are classified by blast radius and recoverability.

**Level 1, single reading.** Causes: absent on this platform (`NotSupported`), insufficient privilege, read failure (`ReadError`), meaningless here (`NotApplicable`). The reading carries the corresponding availability plus a consequence statement; other facts are unaffected; the exception is recorded with `Diag.Swallow`. The distinction among the four states is the point: collapsing them into one "read failed" leaves the user unable to decide the next step.

**Level 2, single source.** A whole group of related facts fails — an anomalous WMI provider, an interrupted driver channel, a device removed mid-load. The group is presented as read-failed and recorded; the load continues. The difference from level 1 is *scope communication*: the report should make the group failure noticeable (for instance, "optional-feature enumeration failed; installed roles report installation state only") rather than leaving users to discover empty fields one at a time.

**Level 3, persistent source failure.** Failure repeated over time, the worst case being *blocking* — an operation that neither returns nor fails. Circuit breakers stop retrying within the run and mark the source disabled. Their necessity comes from blocking's nature: an operation that does not return raises no exception, so levels 1 and 2 cannot cover it. A known risk: some hardware accesses, once triggered, cannot be cancelled from user mode, so a breaker stops *subsequent attempts* — the first block still happens.

**Level 4, program defect.** Unhandled exceptions go to `CrashLog`; logic errors are found by tests and reconciliation. The separation of `CrashLog` from `Diag` lets "tool defect" and "environment limitation" be distinguished when a user reports a problem.

**Level 5, silent failure.** The most dangerous class: nothing fails, but the result is wrong. No exception, no diagnostic entry, no performance impact, and it looks entirely normal on screen. Its concrete instance in this project is the release-build source scan reporting 0 keys, which coverage reporting read as full coverage (10.1) — every step succeeded while the result's meaning was inverted. Silent failures cannot be found by runtime mechanisms; only by structural ones: compile-time fixing of properties that should be fixed, guard tests over invariants rather than features, coverage reporting that makes unchecked items a visible number, key-set comparison after performance changes, and dead-probe-wiring checks.

**Applying the taxonomy.** For a new source, ask: what failures are possible; can it block (timeouts, breaker); what does the user see on failure; could its existence assumption fail in the release environment; and does anything check its result against an invariant or another source?

## 44. Localisation and language modes

The interface offers Traditional Chinese, Simplified Chinese and English, implemented by runtime table substitution rather than three interfaces: translatable text is defined by carrier keys and resolved by the language service. `EnglishStrings` holds English strings, `ZhTerms` holds Chinese terminology. The motive is avoiding hard-coded literals in screen definitions — a principle that was violated once and left consequences (below).

**Custom-drawn controls** are the specific trap: their text is custom dependency properties, not part of the visual tree, so a language switch does not update them because the usual traversal does not reach them. They were converted individually (part of the v2.27 round). The lesson generalises: localisation completeness depends not on the translation table's completeness but on whether *every* text source is unified. Any text bypassing the table is a hole.

**Composite strings** (status bar, clock) are translated segment by segment, because segment order varies between languages; this also protects dynamic content from being frozen during translation.

**Two concrete problems.** First, bound text was once treated as inline text and written into the file, so content stopped following its data source (the clock and live readings stopped updating). The general form: batch text substitution cannot distinguish display literals from data-driven content, though they look identical in the file. Second, mode contamination: generating English strings from Simplified Chinese output produced mixed-language results; the fix is reverse-mapping strings to the source language before storing them. The general form: generating one mode by converting another accumulates errors; all modes should derive from one base.

**This document's editions take a different route**: Traditional is the authored text, Simplified is a character-level OpenCC conversion (guaranteeing paragraph-for-paragraph correspondence), and English is separately authored. One side effect worth noting: source comments mix the two Chinese scripts, so the build converts the assembled document to Traditional in one pass, at the character level only, leaving identifiers, code and numbers untouched.

Three principles follow: text must have one source; data-driven content must be separated from text; and all language modes should derive from one base.

## 45. Build, release and version management

Four projects: `XinSpect` (the WPF application, producing the single portable executable), `XinSpect.Decoders` (pure decoders, the mutation-testing target), `XsRegProbe` (the whitelist read-only kernel driver) and the test project. The split follows 42.2: what can run without an OS is isolated, what touches hardware directly is isolated, the rest lives in the application. The project file declares `net10.0-windows` and the version (2.84, file version 2.84.0.0) and is the single source of the version number.

Two release-relevant technical points. **Assets handling**: the release directory holds only `Assets/` and the executable, which is the direct cause of the 10.1 defect — any design relying on reading source or project files at runtime fails in release, and it fails *silently*, reporting an empty set. **Driver accompaniment**: Ring-0 features depend on the driver loading, so they are unavailable where it cannot (25.5); the tool reports availability rather than simulating an alternative.

**Pre-release checks** divide into three groups. Machine-verifiable: all tests pass (including guard and performance-budget tests), the catalogue matches the source, dynamic-family prefixes match their producers, exemptions all carry reasons, decoders all carry specification references, and the coverage report can be produced. Human-confirmed: new facts have their coverage state resolved, new sources have their failure modes handled, new writes go through the gate with risk stated, new network traffic is user-initiated, and document figures match the generator output. Post-release only: the single executable in a clean environment, driver loading across Windows versions, and report/export readability outside the development environment. The existence of that third group is the source of the 25.2 limitations.

**Per-round records** (`docs/ROUND-*.md`) capture goals, scope, measurements and outstanding items, whose value is recording *rejected paths* — the parallelisation measurement being the prime example — plus register entries for decisions not to do something (XT-003), so that "decided against" is a recorded decision rather than an omission.

**Reproducibility as a release requirement** operates at two levels: numbers (version-history statistics produced by scanning) and documents (this whitepaper's appendices produced by the generator). The value is that drift becomes detectable by re-running rather than by a reader noticing.

## 46. Risk assessment

**Misleading risk** — output misread, leading to wrong action — is the most important class, because the consequence lands outside the tool. Its sources are not wrong values but misunderstood semantics: reading "unreadable" as "absent"; reading "conflict" as "counterfeit" (a conflict only means two sources disagree, whose causes include differing source reliability, differing vintage — firmware descriptions lagging a component swap — and genuine modification); and reading "uncovered" as "checked". Handling: three-state honesty, conflict evidence lists, and coverage reporting. The limit: understanding still rests on the user's judgement, which is one reason the project refuses a composite score — a score stops people thinking.

**Privilege risk.** Handling: read-only first, the whitelist driver, and the write gate. The limit: whitelisting cannot prevent a listed register from being read incorrectly, and read-only cannot prevent side effects from a read itself.

**Write risk.** Handling: explicit risk labelling (scope and consequence), per-instance consent, audit records. The limit: label completeness depends on the implementer's understanding; for platform-specific operations lacking complete documentation it may be incomplete.

**Data-exposure risk.** Handling: no collection or upload; minimal network traffic; constrained handling of sensitive fields (no service names, no identity data); data sovereignty. The limit: the tool cannot control how a user handles a report.

**Performance and availability risk.** Handling: load-time budgets guarded by tests; deep-test items user-initiated; timeouts and breakers. The limit: stress and memory tests deliberately impose load, and their risk (exposing pre-existing hardware instability) cannot be removed, only managed through explicit invocation and disclosure.

**Evidence-credibility risk.** Handling: a hash-chained log verifiable independently; source and measurement time on every fact; time-sync facts as corroboration. The limit: a report's credibility ceiling is set by its environment — incorrect system time or altered trust settings reduce its evidentiary value.

The common structure across all six: **these mechanisms do not guarantee correct conclusions; they guarantee that the tool does not conceal its own state.** A tool that does the latter leaves its user able to judge the former.

## 47. Machine-verification workflow

Constraints: no disassembly, limited time, uncontrolled environment.

**Step 1 — confirm the conditions for obtaining evidence.** Check privilege (without administrator and a loaded driver, the most valuable evidence — microcode revisions, PCI configuration space, SPD over SMBus — is unavailable, and the availability field will say so) and check for a virtualisation layer: if `R-CPU-04` reports one, hardware-layer readings in the same report may come from the hypervisor, which changes what verification means, since a machine running in a VM has no tradeable physical hardware.

**Step 2 — obtain identity and configuration facts**: processor, board, memory, storage list, display, OS build and patch level. This produces the *baseline statement* — what the machine claims to be, against which everything else is compared. The output should be exported in full (capsule or `--json evidence`).

**Step 3 — identification reconciliation**, which needs no load and is therefore cheap: `R-MEM-01/02/04/05`, `R-SSD-05/06`, `R-CPU-02/03/05/06`, `R-LNK-01`. Read the conflict list, remembering that a conflict is not counterfeit.

**Step 4 — cumulative-counter checks**: `R-SSD-01/02/03/04` and `R-BAT-01`. These carry high information because they concern cumulative records that are hard to falsify; `R-SSD-03`'s logical-impossibility verdict is especially reliable, resting on no estimate of a normal range. Absolute counter values need caution (31.6), so ratios and logical relations carry more weight.

**Step 5 — deeper correctness verification** within the time budget: memory testing, disk surface scanning, fake-capacity testing, storage performance sampling. These are expensive, hence last; "pass" means no problem was found within the tested scope.

**Step 6 — firmware-layer evidence**, most valuably flash analysis (hashing, comparison, FIT parsing). A hash's value lies in comparison — against known-good content, or across time on the same machine — so a one-off hash alone is of limited use.

**Step 7 — preserve and export** the capsule or JSON, the report and the audit log, so that a dispute weeks later has a re-checkable basis.

*Limits.* The order cannot be reversed once the machine leaves; if time runs short, prioritise steps 3 and 4 rather than spreading effort evenly. The seller's machine may be in any state, so some evidence may be unavailable and will be marked as such. And the transaction decision rests on factors the tool cannot observe: price, warranty, condition.

## 48. Audit workflow

Auditing differs from verification in three ways: the subject is a known device (usually already under management, with a record it should match); the focus is the baseline and change (rather than identity and health); and the time horizon is long, since evidence may be cited months or years later.

**Step 1 — establish a baseline** while the device is in a known-good state: the full fact set, the measurement time, and the tool version — the last because different versions collect different fact sets (26.3), so differences between a baseline from an old version and a later snapshot include changes in coverage rather than only changes in the device.

**Step 2 — set up compliance benchmarks**, in two forms: device compliance baselines (ZR-004, a user-supplied checklist compared item by item, where unavailable data must not masquerade as a pass) and custom checklists (NG-002, verified line by line with line numbers, no silent partial success, unrecognised keys are not errors, verification only without execution). Both hinge on clearly distinguishing pass, fail and undecidable — the commonest audit-tool defect being to count undecidable as pass, producing a report that looks clean.

**Step 3 — periodic sampling**, as consistently as possible (same privilege, same tool version, similar system state) so cross-period comparison is valid.

**Step 4 — change detection** via the hardware change timeline (HM-007). Changes fall into three types needing different handling: expected (matching change-management records, where the value is confirming record accuracy), unexpected (needing investigation), and **source change rather than fact change** — where a value changed because a driver update moved the same item to a different source. Distinguishing the third requires the source field; without it, the third is misclassified as the second.

**Step 5 — verifiable export**: report signing (RS-010) and external audit-log verification (`--verify-audit`), both designed so that verification does not depend on the object being verified.

**Step 6 — produce a citable report**: source and trust level on every fact; verdict and evidence on every reconciliation result; coverage state on uncovered facts; undecidable items listed explicitly; and the snapshot date and tool version present. Citing a report should include the conditions under which it was produced (46.7).

*Limits.* A baseline's value depends on the state it was taken in; compliance comparison's value depends on the checklist's quality, which the tool does not assess; change detection covers only the facts the snapshot covers; and auditing cannot reconstruct the state of unsampled periods.

## 49. Reading a report

A complete report has four parts, in the order of derivation from raw data to verdict: the **fact list** (grouped by category, each with name, value, unit, source, trust, availability and time); the **reconciliation results** (rule id, partition, title, verdict, explanation, evidence); the **coverage statement** (how many keys are covered, exempt and uncovered); and the **context** (tool version, measurement time, execution environment — privilege level and whether a hypervisor was detected — and methodology notes). Missing any part reduces the report's evidentiary value.

**Reading a fact** means four steps: availability first (if not `Present`, the field has no value, and the reason dictates the action — `NotSupported` needs none, `InsufficientPrivilege` means re-run elevated, `ReadError` means check driver state and the diagnostic log, `NotApplicable` needs none); then trust level, which sets how much extra verification the value needs; then source, which is the basis for re-checking; then value and time, the latter essential for cross-snapshot comparison.

**Reading a verdict.** *Match* means no inconsistency was found **for that rule's facet**, not that the machine passed. *Conflict* should first be examined through its evidence list; its three possible causes are differing source reliability (trust `Reported` less than `Measured`), differing vintage, and genuine difference. *Undecidable* means required facts were missing — and **undecidable is not a pass**, the single most important point in report reading, since counting it as a pass systematically overstates fleet health.

**Reading coverage.** Covered facts are considered by at least one rule (which does not mean a match was returned); exempt facts are deliberately not reconciled, with reasons in the exemption list — a reader caring about a specific fact should check whether exemption was a sound design choice or an omission; and **uncovered facts are not cross-checked at all**, so their credibility rests only on their source. A reader seeing an uncovered fact used as the basis for a judgement should recognise that basis as uncrossed.

**Reading context.** Tool version determines which facts were collected; measurement time determines currency; execution environment determines which facts were available and the nature of hardware-layer readings; methodology notes govern the interpretation of measurements.

**Hypervisor impact.** With a virtualisation layer detected, hardware-layer facts come from it, so `R-CPU-04` marks a methodological warning, performance measurements reflect the combination of hypervisor and host, and identity facts reflect the hypervisor's configuration.

**Common misreadings**, each matched to a mechanism: treating an empty value as zero (2.1); treating a match as an overall pass (49); treating undecidable as a pass (49); treating uncovered as checked (49); treating a conflict as counterfeit (46); ignoring measurement conditions (13.2); and ignoring version and environment (49).

## 50. Troubleshooting and common misdiagnoses

Troubleshoot outside-in: environment conditions first (privilege, virtualisation, endpoint protection blocking driver load), then tool state (diagnostic records, `CrashLog`, driver load state), and hardware and platform last — because the first two are far more likely than the third.

Common symptoms and causes: many fields unreadable with `InsufficientPrivilege` (not run elevated, affecting Ring-0 facts); all facts from one WMI source missing (provider anomaly, failing as a group); unusually long load times (a blocking source — check the diagnostic log and breaker state); a sensor reading static for a long time (unconnected or uninitialised, so possibly not a real temperature); zero or blank memory slot count (the Type 16 structure absent or unreadable — **never** "no memory slots"); blank SMBIOS asset fields (the field is absent, which the `asset.field.` family records so absence and non-reading are distinguishable); a PCIe link narrower than the card's capability (slot wiring, power management, signal integrity, risers); a drive reporting 0% used on a used machine (reset lifetime counter, which `R-SSD-02` cross-checks against bytes written); TPM state unreadable (disabled in firmware, disabled by the OS, or insufficient privilege — "unreadable" and "disabled" are different findings); an event log showing no errors (logs can be cleared or overwritten, so absence is not innocence); and the local API unreachable (the listener shell deliberately does not start — use the CLI's `--json evidence`).

**Reading the diagnostic log**: each line has four fields — time, location label, exception type and message, and the effect on the user. Read the effect first (it states the scope of the degradation), then the location (the failing source), then the message (the technical cause). Unhandled exceptions go to `CrashLog`; both files sit in the same folder and should be attached together to a bug report.

**Reporting a problem** should include: both diagnostic records; tool and OS versions; execution environment (privilege, virtualisation); the specific symptom — which field, its availability value, and the expected behaviour; and the capsule or export if the symptom is observable in the fact set. Because the tool distinguishes four unreadable states, "a field has no value" is not a complete description: the availability value is the key information.

## 51. Contributor implementation guide

**Adding a fact**: choose the key name (dotted hierarchy, existing prefix if possible); decide the trust level by how many hands the value passed through; handle all four unreadable cases, never filling with zero or a typical value; add the key to `FactKeyCatalog` (omitting it fails a test, deliberately); resolve its coverage state — used by a rule, or exempt with a reason whose threshold is "no second source exists"; and add at least three-state tests.

**Adding a source**: determine its layer (which sets trust and reconciliation value); determine whether Ring-0 is needed (higher cost, since it touches the driver and whitelist); handle its failure modes against the five questions of 43.7; add timeouts and a breaker where blocking is possible, conservatively if the hardware is unavailable for testing; choose the load timing, considering ordering semantics and the load budget, and preferring the deep-test centre or evidence lab over the startup path for expensive sources; and document it in the corresponding domain chapter.

**Adding a rule** carries the highest risk, since a badly designed rule emits false signals indefinitely. Confirm the **independence of inputs** — if both facts come from one source, or one is derived from the other, the verdict has no reconciliation meaning. Confirm the proposition is **decidable from the inputs** — "unsafe shutdowns exceed power cycles" is logically impossible, whereas "writes are high" needs an estimate of a normal range and is far less reliable. Handle missing values not at all, because the engine does; declare all required facts; choose the scope, per-disk for storage; write per-scenario tests including one missing required fact; and check the coverage impact, since a new rule turns its inputs from uncovered or exempt into covered, and an existing exemption may need removal to avoid a contradictory state. Above all, **do not pad**: if the proposition is strained, the inputs are not independent, or the verdict is unreliable, do not add it — the coverage rate is not the goal.

**Adding a decoder**: obtain the specification basis and carry the `SpecRef` (pointing at the project's methodology when no external specification applies); implement structure validation first; mark missing fields; avoid semantic inference; preserve unexplained content; add golden vectors; and ensure mutability — that is, verify it lives in `XinSpect.Decoders`, refactoring to pure functions if it depends on the OS.

**Pre-submission checklist**: all tests pass; catalogue and source agree; new facts have a resolved coverage state; new sources have their failure modes handled; new writes pass the gate with stated risk; new network traffic is user-initiated; new decoders carry specification references; documentation (including the corresponding chapter of this whitepaper) is updated; and, for performance work, the key set is unchanged. The first five are machine-guaranteed or machine-verifiable; the last four need human confirmation — and the fewer human-confirmed items there are, the better, because human confirmation decays with time and staffing.

## 52. Worked example: reading one report

The values below are illustrative, presented in this project's actual output shape, chosen to exercise several interpretation cases: mixed modules, a refurbished SSD, a down-negotiated PCIe link and one item lost to insufficient privilege.

**Context.** Tool version 2.84; measured 2026-10-14 22:41:07; running elevated, so Ring-0 facts should be available; no virtualisation layer detected (`R-CPU-04` match), so hardware-layer readings are direct measurements and 49's hypervisor adjustment does not apply — had it been detected, the whole report's hardware layer would need reinterpretation, with a large loss of diagnostic value.

**An unreadable item.** Per-core microcode revision, availability `InsufficientPrivilege`, reason "requires administrator privilege and the read-only driver", source "MSR (Ring-0)". The cause is privilege or driver, not absence of hardware; the correct response is to check driver load state; it must not be read as "this machine has no microcode information".

**A memory fact.** Module manufacturers, value "Vendor A | Vendor A | Vendor B | Vendor B", `Measured`, SMBus SPD, present. Two of four modules come from a different vendor; the value is a statement whose meaning the rule decides.

**A derived fact.** Silicon-derived base frequency 3,600 MHz, `Derived`, from maximum non-turbo multiplier × BCLK, present. Being derived, its derivation must be stated, and comparing it with CPUID's claimed value is `R-CPU-05`.

**Result one.** `R-MEM-01` conflict: manufacturers and part numbers disagree, so these are not from one batch, with both lists as evidence. This confirms a mixed batch, whose causes include memory expansion, replacing a failed module, or assembly. **It does not indicate counterfeiting**; its meaning is contextual — if the seller claims factory configuration, the conflict contradicts that claim, and if the seller disclosed an upgrade, it merely confirms the statement.

**Result two.** `R-SSD-02` conflict on disk 3: 0% used against 96,400 GiB written. This is strong evidence of a reset counter, since a device with 96,400 GiB written cannot still be at 100% health; causes include controller firmware resetting the counter and maintenance behaviour by the tool. Both are stateable facts, and the user should cross-check power-on hours and power cycles via `R-SSD-01` and `R-SSD-03`.

**Result three.** `R-LNK-01` conflict: current x4 / 8.0 GT/s against maximum x16 / 16.0 GT/s. Both width and speed are reduced; causes include physical slot wiring, power management and riser quality; the tool states the gap, and attributing the cause needs board specifications and physical configuration.

**Result four.** `R-MEM-05` undecidable, missing fact "memory array declared error-correction type", condition "requires SMBIOS Type 16, which this system does not provide". Undecidable is not a pass: ECC presence was not confirmed, and confirming it needs another route such as the module datasheet or system documentation.

**Coverage.** 844 keys; 562 exemptions; a known uncovered count. Readers should note which facts were not cross-checked, and adjust the credibility of any judgement resting on them.

**Conclusions.** First, a conflict list is worth more than any single value: the three conflicts here are stateable, re-checkable and directly relevant to the transaction. Second, undecidable items must be listed explicitly, or the report signals the wrong thing. Third, interpretation needs context the seller and the deal provide, not the tool. Fourth, source and trust level must be inspected: the silicon frequency is `Derived`, while all hardware-layer facts are `Measured`, whose reliability in turn depends on the measurement environment — credible here, because no hypervisor was detected.

## 53. Measurement methodology

The project distinguishes **reading** (of existing state, not altering the observed object) from **measuring** (running a defined load under defined conditions, whose result depends on those conditions and must therefore be recorded and self-validated). The practical consequence is that a reading's error is "read wrong" or "could not read", whereas a measurement's error additionally includes method bias — harder to detect, because instead of anomalous values it produces stable, wrong ones.

**Reading reliability** rests on three factors: source layer (hardware-highest to OS-lowest); source singularity (a single source sets its own reliability ceiling, since cross-validation is impossible); and read success, the distinction between failure and zero — basic, yet the most commonly violated in practice.

**Four requirements for measurement.** Self-validation (a mathematical checksum, a known-size workload, or write-readback — necessary because an invalid measurement reports stable, plausible, wrong numbers); recorded conditions; presented uncertainty (an error margin or sample count, with differences inside it stated as not statistically significant); and distinguishing unmeasured from zero.

**Five failure modes.** Cache effects (mitigated by known-load self-verification and recording the working set); counter resets (mitigated by per-round write-readback and overflow handling); condition drift (mitigated by condition recording and stability checks); insufficient samples (mitigated by adequate sample counts and distributional output); observation-scope mismatch (measuring a proxy that does not cover the phenomenon — the invisible stall being the exemplar, mitigated by measuring the user-perceivable quantity directly); and **methodological bias**, which neither self-validation nor statistics can detect and only domain review can, since **tests cannot validate methodology**.

**Presentation** requires value alongside conditions, intervals or distributions rather than point estimates where variance is material, and visible self-validation state — an unvalidated measurement should not be presented as a number, so that an invalid measurement cannot be mistaken for a valid one.

*Limits.* Results depend on hardware, driver, OS and background environment; each method has a domain of applicability beyond which it misleads; and correct methodology ultimately depends on domain knowledge, whose only mitigation is stating the method and its assumptions.

## 54. Fact-key naming and evolution conventions

Keys are part of the public interface: capsule contents, query-language comparisons, local API responses and report organisation all rest on them. Renaming invalidates existing snapshots and queries; changing meaning corrupts historical comparison; and unbounded proliferation dilutes coverage reporting, since the denominator grows.

Keys take the form `<domain>.<sub-domain or object>.<attribute>`, with 107 domain prefixes (Appendix A). Prefix count should stay restrained, since adding one means adding a domain, which costs more than adding a sub-domain. Three key forms exist, distinguished by **what determines membership**: *fixed keys*, fixed at compile time in the catalogue; *dynamic families*, membership determined by the machine (each declaring why and by what); and *fixed keys within dynamic families* — summary keys such as `monitor.summary`, `sa.lsp.count` and `role.surface`, because a summary is itself a fixed question ("how many monitors") and should therefore be a fixed key rather than a family member.

**Evolution conventions**: published keys are never renamed (express new meaning as a new key); meaning changes require a new key, so that a changed value in historical comparison can be attributed to a changed fact rather than a changed source; key removal requires a catalogue update or a test fails; dynamic-family prefixes must have producers, or the zombie-prefix test fails; and family prefixes end with a dot unless the final segment is a port prefix such as `sio.0x`, an exception that exists to accommodate "the port is a platform fact".

**Key-space integrity** is guaranteed by two checks — runtime reconciliation and the zombie-prefix check — making the key space a closed, verifiable set. That closure has two uses: the coverage denominator is correct, and the report's field set is predictable. Because some keys are referenced by several modules (reader and presenter, for instance), a meaning change requires reviewing all references; centralising keys in the catalogue is what makes consistency mechanically checkable. The conventions cost effort — catalogue updates, exemption reasons, family declarations, no renaming — and buy correct coverage reporting, valid historical comparison, predictable field sets and a closed key space.

## 55. Decision record

The reasons behind the design are scattered throughout this document; this chapter collects them, each with decision, reason, cost and **removal condition** — the last being the most important, since it states when the decision should be revisited.

*Data model.* **D-01** All facts in one type — consumer consistency (3.1); cost: unused fields; revisit if a fact class cannot be expressed (extend the model rather than adding a type). **D-02** Availability separate from value — unreadable must differ from zero (2.1); no removal condition. **D-03** Four trust levels — statements about inference-chain length; cost: manual classification, fuzzy boundaries. **D-04** `numericValue` separate from `value` — comparison needs numbers, presentation needs formatting; cost: keeping them in sync.

*Key space.* **D-05** Catalogue fixed at compile time — the release scan failure read as full coverage (10.1); cost: manual catalogue updates; revisit if the release form changes to include sources. **D-06** Dynamic families declare reasons — non-enumerable membership must not become a blanket exemption; cost: writing and reviewing reasons. **D-07** No renames, new keys for changed meaning — valid historical comparison. **D-08** Summary questions use fixed keys.

*Reconciliation.* **D-09** Three verdicts, no score — scores stop thinking (1.1, 8.1). **D-10** Missing values handled by the engine, not in rules — global consistency. **D-11** Conflicts must carry evidence — verdicts must be re-checkable. **D-12** Storage rules per disk — storage health is per device. **D-13** No coverage target — forcing full coverage manufactures padding rules. **D-14** Exemptions need reasons, threshold "cannot be crossed" — distinguishing "cannot" from "tedious".

*Acquisition.* **D-15** Whitelist read-only driver — enumerable, reviewable access; cost: expansion effort. **D-16** Read-first, writes via one gate — separating the consequences of read and write defects. **D-17** Timeouts and breakers on risky sources — uncancellable accesses can block; revisit if the underlying access becomes cancellable. **D-18** Two diagnostic records — separating tool defects from environment limits. **D-19** `Diag` never throws — the diagnostic mechanism must not become a failure source.

*Output.* **D-20** Four outputs, one model — consistency by architecture. **D-21** HTTP listener shell off by default — a listener adds local attack surface; revisit if a secure local authentication mechanism is provided. **D-22** Query syntax errors return errors, not empty results — disguising an error as emptiness is an honesty violation. **D-23** Independently verifiable audit log — verification must not depend on the verified.

*Feature trade-offs.* **D-24** No REST example set (XT-003) — a usable example ships code targeting an uncommitted endpoint; revisit if the endpoint specification becomes a commitment. **D-25** No background service, no schedule (AG-005) — "could be done" and "is being done" differ; revisit: none. **D-26** Network traffic only on user request — the data boundary. **D-27** AI commentary at the end of the dataflow — generative features must not affect the fact layer.

*Performance.* **D-28** Load path not parallelised — a real ordering dependency would make output timing-dependent (23.4); cost: forgoing a ~66% improvement; **revisit after fixing the `LoadServerCompliance` dependency (5.2)**. **D-29** Key-set comparison after performance changes — no trading facts for speed. **D-30** Performance budget guarded by tests — regressions found by tests rather than review.

Reviewing all thirty, the common structure is that **most decisions cost implementation effort or convenience and buy verifiability or output stability**. A few have no removal condition, meaning they are the tool's basic stance rather than technical choices (D-02, D-09, D-13); changing those would change the tool's nature, so they should not be changed by technical assessment. The rest have removal conditions that are typically changes in environment or implementation — D-05's release form, D-28's ordering dependency — and those should be actively revisited when the conditions change, rather than left in place until a problem appears.

## 56. Open questions

Some items are neither defects (the current implementation is not wrong) nor to-dos (there is no defined fix) but design questions that need further thought before they can be decided. Listing them makes them discussable rather than silently settled during implementation.

**What should the coverage goal be?** The project sets no target (2.5, D-13), which solves manufactured rules but leaves the question of how uncovered facts should be treated. Three stances: accept them (their credibility rests on their source); process them gradually; or mark them in the report so readers know at the point of reading that the fact was never cross-checked. The third has the lowest cost and directly improves interpretive accuracy, at the price of a more complex presentation. Undecided.

**How should the unmeasurable be measured?** Some items are technically difficult or impossible — cross-node latency on single-node machines, CXL behaviour without CXL hardware, AMD facts without AMD hardware — and are marked not-applicable or undecidable. But that marking can mask a real distinction: some items *should* be measurable on a given platform and only lack an implementation path. Separating "this machine lacks the capability" from "this tool does not yet support it" needs a finer state than the current model has.

**Cross-machine comparison.** Historical comparison of one machine exists (19.3). Cross-machine comparison must handle hardware configuration, driver, OS and environmental differences; its value is high (comparing same-model machines in asset management) but its validity is hard to guarantee, so the current stance is to supply facts and conditions and let the user handle the comparison logic.

**Diversifying rule sources.** Rules currently come from the project's own design. An alternative is user-configurable rules in knowledge packs (for example, "this organisation's machines should not contain certain hardware combinations"). The problem is that configurable rules introduce new sources of false signals, since organisation-written rules may not match the facts' semantics, so the direction requires rule-validation mechanisms first — trial runs and post-hoc review of output. Note that partial capability already exists: device compliance baselines (ZR-004) and custom checklists (NG-002) both accept user-supplied lists, but both are *comparison lists* rather than *rule engines* — the former checks whether facts match a list, the latter needs decision logic.

**Standardising measurement.** The project's methods are internal choices. Cross-tool comparability would require standardised loads, conditions and metrics — difficult, since standardisation needs industry consensus. A middle path is what the project already does: state the method and its parameters explicitly, and make the parameters exportable so a third party can reproduce the conditions.

**Attributing causes.** The project supplies facts and verdicts but not diagnoses (1.4), which leaves a gap: after a conflict list, users still judge severity themselves. A possible improvement is a *structured statement of causes* rather than a conclusion — "possible causes include A, B and C, of which A can be distinguished by fact X" — still re-checkable and therefore consistent with the project's stance, but requiring a cause list per rule, at considerable cost and with no guarantee of completeness.

**Output stability after parallelisation.** If the ordering dependency is fixed, the next question is how parallel execution can guarantee stable output: even with every ordering dependency removed, parallel execution may change the order in which facts are produced, and some facts' values depend on others (summary facts, for instance). Parallelisation would therefore need a mechanism declaring fact dependencies so the engine can validate that parallel execution is safe. The cost of that mechanism may exceed the benefit of parallelisation — another reason it was deferred.

The common property of all seven: **they are concrete instances of tension between the project's core principles and practical needs** — honesty against readability (1, 3); refusing to conclude against being useful (2, 4); re-checkability against user burden (5, 6); output stability against performance (7). The existing stance is to prefer the core principle and mitigate the practical loss by making state visible. That consistency has kept the project's direction stable across many rounds of expansion; its cost is that some practical needs go unmet.

## 57. Terminology concordance

The project mixes Chinese and English terminology, so the Chinese editions carry a concordance (chapter 57 there) for cross-language reading. The convention is that technical terms use the specification's or industry's English (MSR, SPD, SMBIOS), while the project's own concepts are named in Chinese with English glosses. The most important project-specific terms in English are: **fact**, **fact key**, **trust level** (`Measured`/`Reported`/`Derived`/`Unknown`), **availability** (`Present`/`NotSupported`/`InsufficientPrivilege`/`ReadError`/`NotApplicable`), **three-state honesty**, **reconciliation**, **verdict** (match/conflict/undecidable), **per-disk scope**, **explicit exemption**, **coverage report**, **dynamic key family**, **zombie prefix**, **guard test**, **dead probe wiring**, **snapshot**, **time capsule**, **hardware change timeline**, **decision registry**, **write gate**, **graceful degradation**, **invisible stall**, **self-validating measurement**, **reproducible baseline** and **canonical**.

Two cautions: the Chinese names are authoritative for the project's own terms, and the English glosses here are explanations rather than official translations — in technical communication, use the English specification terms directly to avoid ambiguity. Where Chinese has no established equivalent (the trust level `Measured`, for instance), the English original with a Chinese explanation is used; such mixing is normal in technical fields rather than an oversight.

## 58. Document map and further reading

The project's documentation falls into four groups. The **entry documents** are `README.md` (Traditional Chinese), `README.zh-CN.md` and `README.en.md`, covering features, installation and version history. **This whitepaper** covers intent, data model, decision semantics, engineering constraints and the full fact-domain account. The **specifications and methodology documents** are `docs/spec/query-language.v1.md`, `docs/spec/snapshot.schema.v1.json`, `docs/spec/METHODOLOGY.md`, `docs/spec/LIMITATIONS.md`, `docs/spec/CERTIFICATION-PLAN.md`, `docs/spec/corpus-upload.v1.md`, `docs/MEASUREMENT-METHODOLOGY.md`, `docs/DATA-SOVEREIGNTY.md`, `docs/DEPLOYMENT.md`, `docs/EC-RISK-ASSESSMENT.md` and `docs/AGENT_CONTEXT.md`. The **per-round records** are the `docs/ROUND-*.md` files, whose value is recording rejected paths (45.5).

Finding what you need: verifying a used machine — chapter 47, then chapter 49 to interpret the result, then chapter 9 for a specific rule's proposition; establishing an audit baseline — chapter 48; understanding why a field has no value — 2.1 and chapter 50; checking whether a fact is cross-checked — Appendix A for its producer and Appendix D for an exemption entry, and if neither applies it is uncovered; understanding where a dynamic family's membership comes from — Appendix B; extending the tool — chapter 51 and chapter 55, the latter to avoid re-introducing rejected designs; citing output as evidence — 20.4, 48.6 and 46.7; judging measurement reliability — chapter 53 and chapter 13; understanding the tool's stance and non-goals — 1.4 and 2.1–2.8.

Appendix purposes: **A** 844 static keys and their producing modules (find which module produces a key); **B** 23 dynamic families and what determines their members; **C** the 18 rules (inputs, partitions, scope); **D** 562 exemptions and reasons (why a class of facts is not reconciled); **E** 416 service modules (size and facts owned); **F** 433 test files and their marker counts; **G** 51 documents with titles and sizes; **H** the glossary.

The appendices are produced by `work/wp_gen.py` from the version-controlled sources and assembled into the three editions by `work/wp_build.py` (40.3), so drift is detectable by re-running rather than by a reader noticing. Documentation maintenance follows three rules derived from the principles above: numbers must be reproducible; non-goals must be recorded, distinguished from outstanding work; and rejected paths must be recorded with their measurement, reasons and removal conditions, so later contributors need not rediscover them.

---

# Appendices

Everything in these appendices is produced by scanning this build's sources with `work/wp_gen.py`:

```
python work/wp_gen.py
```

The generator emits fragments into `work/wp/`, which `work/wp_build.py` (Chinese editions) and `work/wp_build_en.py` (this edition) assemble. The appendices therefore cannot drift from the code: if keys, rules, exemptions or modules change, re-running the generator produces the corresponding appendices and the difference can be compared line by line.

| Appendix | Content | Source |
|---|---|---|
| A | Full static fact-key catalogue with producing modules | `Services/FactKeyCatalog.cs`, `Services/*.cs` |
| B | Dynamic key families and what determines their members | `Services/FactKeyDynamicCatalog.cs` |
| C | Reconciliation rule index | `Services/VerifyRules.cs` |
| D | Explicit exemption list and reasons | `Services/FactCoverageReport.cs` |
| E | Service module index | `Services/*.cs` |
| F | Test file index | `Tests/*.cs` |
| G | Document index | `docs/**/*.md` |
| H | Glossary | Written by hand |

**Note on Appendices B and D in this edition.** The rationale text for dynamic-family declarations and exemptions is authored in the source tree in Chinese, and is reproduced verbatim in the Chinese editions. Appendix B below is a full English translation of all 23 family declarations. Appendix D summarises the exemption categories with counts and translated representative entries; the complete per-entry rationale text for all 562 exemptions is in the Chinese editions (`WHITEPAPER.md`, `WHITEPAPER.zh-CN.md`).

In Appendices A and E, "producing modules" means modules whose source contains the corresponding fact-key string literal. A key may be referenced by several modules (a reader and a presenter, for example), and all referencing modules are listed; a module's "keys owned" is the sum of the keys it references, so those totals exceed the number of keys.

---

### Appendix A — notes

The catalogue holds **844** static fact keys, plus the dynamic families of Appendix B. The prefix table gives, for each of the 107 prefixes, its key count and the modules that produce most of its keys. The per-prefix tables then list every key with the modules that reference it.

---

### Appendix C — notes

There are **18** reconciliation rules. A verdict is one of three — match, conflict, undecidable; no score and no authenticity claim. The scope column records whether a rule runs once per machine or once per disk.

---

### Appendix E — notes, Appendix F — notes and Appendix G — notes

Appendix E indexes the **416** modules under `Services/`, with each module's primary type, line count and the number of fact keys it owns. Appendix F indexes the **433** test files and their `[Fact]`/`[Theory]` marker counts. Appendix G indexes the **51** documents under `docs/` with their titles and byte sizes.

---

# Appendix A — Full fact-key catalogue

The catalogue holds **844** static fact keys, plus **23** dynamic key families (Appendix B).

| Prefix | Keys | Primary producing modules |
|---|---:|---|
| `ac.` | 29 | `FactKeyCatalog.cs`、`AcpiTableService.cs` |
| `acpi.` | 4 | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ag.` | 7 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `amd.` | 8 | `AmdSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `asset.` | 4 | `AssetFactsService.cs`、`FactKeyCatalog.cs` |
| `au.` | 3 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `audio.` | 2 | `FactKeyCatalog.cs`、`AudioEndpointFactsService.cs` |
| `audit.` | 3 | `FactKeyCatalog.cs`、`SecurityAuditFactsService.cs` |
| `backend.` | 3 | `BackendEnvironmentService.cs`、`FactKeyCatalog.cs` |
| `bl.` | 11 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `boot.` | 3 | `FactKeyCatalog.cs`、`BootTimingFactsService.cs` |
| `byovd.` | 2 | `ByovdCompareService.cs`、`FactKeyCatalog.cs` |
| `cam.` | 1 | `CameraFactsService.cs`、`FactKeyCatalog.cs` |
| `cap.` | 9 | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `cert.` | 1 | `FactKeyCatalog.cs`、`SecurityAuditFactsService.cs` |
| `chassis.` | 1 | `ChassisAndHpaFactsService.cs`、`FactKeyCatalog.cs` |
| `chipset.` | 3 | `ChipsetSecurityService.cs`、`FactKeyCatalog.cs` |
| `cm.` | 4 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `cmos.` | 3 | `CmosService.cs`、`FactKeyCatalog.cs` |
| `cpu.` | 15 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `cxl.` | 1 | `CxlFactsService.cs`、`FactKeyCatalog.cs` |
| `dbg.` | 3 | `DebugConfigService.cs`、`FactKeyCatalog.cs` |
| `defender.` | 1 | `FactKeyCatalog.cs`、`SecurityAuditFactsService.cs` |
| `display.` | 2 | `DisplayAdapterFactsService.cs`、`FactKeyCatalog.cs` |
| `dk.` | 7 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dl.` | 15 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `dm.` | 8 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `drvinsp.` | 2 | `DriverInspectionFactsService.cs`、`FactKeyCatalog.cs` |
| `ds.` | 18 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `dv.` | 9 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.` | 24 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `el.` | 8 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ent.` | 4 | `EnterpriseStorageFactsService.cs`、`FactKeyCatalog.cs` |
| `esp.` | 3 | `EspScanService.cs`、`FactKeyCatalog.cs` |
| `etl.` | 2 | `EtlReadbackService.cs`、`FactKeyCatalog.cs` |
| `ev.` | 19 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `evt.` | 1 | `EventLogSummaryService.cs`、`FactKeyCatalog.cs` |
| `fs.` | 23 | `FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `fw.` | 30 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `gd.` | 7 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `gp.` | 4 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `gpu.` | 11 | `FactKeyCatalog.cs`、`GpuTdrFactsService.cs` |
| `gx.` | 12 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `hist.` | 9 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `hm.` | 10 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `hpa.` | 1 | `ChassisAndHpaFactsService.cs`、`FactKeyCatalog.cs` |
| `ie.` | 4 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `in.` | 11 | `FactKeyCatalog.cs`、`SelfIntegrityFactsService.cs` |
| `knowledge.` | 2 | `FactKeyCatalog.cs`、`KnowledgePackService.cs` |
| `mchbar.` | 2 | `FactKeyCatalog.cs`、`FactRelationEngine.cs` |
| `mem.` | 6 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `mon.` | 2 | `FactKeyCatalog.cs`、`MonitorConnectionService.cs` |
| `monitor.` | 1 | `EvidenceLabService.cs`、`FactKeyCatalog.cs` |
| `ms.` | 23 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `msr.` | 1 | `CpuFirmwareFactsService.cs`、`FactKeyCatalog.cs` |
| `mu.` | 12 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `net.` | 4 | `FactKeyCatalog.cs`、`ListenPortFactsService.cs` |
| `ng.` | 10 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `nic.` | 4 | `FactKeyCatalog.cs`、`NicHealthFactsService.cs` |
| `numa.` | 3 | `FactKeyCatalog.cs`、`NumaTopologyService.cs` |
| `nw.` | 22 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `of.` | 7 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `oob.` | 1 | `FactKeyCatalog.cs`、`OobFactsService.cs` |
| `pci.` | 4 | `FactKeyCatalog.cs`、`Bus0InventoryService.cs` |
| `pcieaer.` | 2 | `EcamAerService.cs`、`FactKeyCatalog.cs` |
| `pl.` | 10 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `platform.` | 6 | `FactKeyCatalog.cs`、`FactRelationEngine.cs` |
| `pmu.` | 7 | `FactKeyCatalog.cs`、`PmuCapabilityFactsService.cs` |
| `po.` | 15 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `psu.` | 4 | `FactKeyCatalog.cs`、`PsuPmbusFactsService.cs` |
| `pv.` | 5 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `pw.` | 9 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `qs.` | 9 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `raid.` | 1 | `FactKeyCatalog.cs`、`OobFactsService.cs` |
| `rc.` | 18 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `reg.` | 1 | `CpuFirmwareFactsService.cs`、`FactKeyCatalog.cs` |
| `role.` | 2 | `EvidenceLabService.cs`、`FactKeyCatalog.cs` |
| `rs.` | 11 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `sa.` | 45 | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sb.` | 22 | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sc.` | 10 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `se.` | 15 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `setuptl.` | 2 | `FactKeyCatalog.cs`、`SetupApiTimelineService.cs` |
| `sg.` | 14 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `smart.` | 1 | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs` |
| `smbus.` | 1 | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `spi.` | 9 | `FactKeyCatalog.cs`、`FactRelationEngine.cs` |
| `st.` | 20 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `storage.` | 2 | `FactKeyCatalog.cs`、`StorageReliabilityFactsService.cs` |
| `sv.` | 14 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `time.` | 3 | `FactKeyCatalog.cs`、`TimeSyncFactsService.cs` |
| `tm.` | 5 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tpm.` | 3 | `FactKeyCatalog.cs`、`TpmFactsService.cs` |
| `tu.` | 10 | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs` |
| `uefi.` | 10 | `FactKeyCatalog.cs`、`UefiBootFactsService.cs` |
| `uf.` | 11 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |
| `ufv.` | 1 | `FactKeyCatalog.cs`、`UefiFvFactsService.cs` |
| `ups.` | 3 | `FactKeyCatalog.cs`、`UpsFactsService.cs` |
| `usb.` | 3 | `FactKeyCatalog.cs`、`UsbTopologyService.cs` |
| `usbstor.` | 1 | `FactKeyCatalog.cs`、`SecurityAuditFactsService.cs` |
| `uv.` | 11 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ux.` | 12 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `virt.` | 10 | `FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |
| `vr.` | 8 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ws.` | 13 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `xt.` | 7 | `FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `zr.` | 9 | `FactKeyCatalog.cs`、`FactCoverageReport.cs` |

### `ac` — 29 鍵

| Fact key | Producing modules |
|---|---|
| `ac.aml.stat` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |
| `ac.aml.str.count` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.aml.str.sample` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.bgrt` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.bgrt.deep` | `AcpiDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ac.cpc.summary` | `CppcCapabilityFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ac.dbg2` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.dbg2.deep` | `AcpiDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ac.dbg2.devices` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.dsm.summary` | `AcpiDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ac.hpet.full.base` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.hpet.full.id` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.hpet.full.min_tick` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.hpet.full.sequence` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.mcfg.full.count` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.mcfg.full.list` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.mpam` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.nfit.devices` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.nfit.spa.count` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.nfit.summary` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.pptt.count` | `FactKeyCatalog.cs`、`PpttFactsService.cs` |
| `ac.pptt.summary` | `FactKeyCatalog.cs`、`PpttFactsService.cs` |
| `ac.pwres.summary` | `AcpiDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ac.spcr` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.spcr.deep` | `AcpiDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ac.ssdt.third.count` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.ssdt.third.list` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `ac.tables.deep` | `AcpiDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ac.tables.summary` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |

### `acpi` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `acpi.bert.record` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `acpi.bert.region_length` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `acpi.hest.sources` | `AcpiTableService.cs`、`FactKeyCatalog.cs` |
| `acpi.tables` | `AcpiTableService.cs`、`ClosingFactsService.cs`、`FactKeyCatalog.cs` |

### `ag` — 7 鍵

| Fact key | Producing modules |
|---|---|
| `ag.compose` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ag.exitcode` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ag.headless` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ag.remote` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ag.rotate` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ag.service` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServiceModeFactsService.cs` |
| `ag.stdin` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `amd` — 8 鍵

| Fact key | Producing modules |
|---|---|
| `amd.mem_enc.enabled` | `AmdSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `amd.psp.present` | `AmdSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `amd.sev.supported` | `AmdSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `amd.sev_es.enabled` | `AmdSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `amd.sev_es.supported` | `AmdSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `amd.sev_snp.enabled` | `AmdSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `amd.sev_snp.supported` | `AmdSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `amd.sme.supported` | `AmdSecurityFactsService.cs`、`FactKeyCatalog.cs` |

### `asset` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `asset.chassis` | `AssetFactsService.cs`、`CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `asset.identify` | `AssetFactsService.cs`、`EvidenceLabService.cs`、`FactKeyCatalog.cs` |
| `asset.identify.evidence` | `AssetFactsService.cs`、`EvidenceLabService.cs`、`FactKeyCatalog.cs` |
| `asset.uuid` | `AssetFactsService.cs`、`FactKeyCatalog.cs` |

### `au` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `au.apo.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |
| `au.drift` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `au.privacy.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |

### `audio` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `audio.endpoints` | `AudioEndpointFactsService.cs`、`FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `audio.latency` | `AudioLatencyFactsService.cs`、`FactKeyCatalog.cs` |

### `audit` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `audit.logclear.count` | `FactKeyCatalog.cs`、`SecurityAuditFactsService.cs` |
| `audit.logclear.last` | `FactKeyCatalog.cs`、`SecurityAuditFactsService.cs` |
| `audit.mode` | `AuditPolicyService.cs`、`FactKeyCatalog.cs` |

### `backend` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `backend.environment_decision` | `BackendEnvironmentService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |
| `backend.mmio` | `BackendEnvironmentService.cs`、`CapabilityMatrixService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |
| `backend.msr` | `BackendEnvironmentService.cs`、`CapabilityMatrixService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |

### `bl` — 11 鍵

| Fact key | Producing modules |
|---|---|
| `bl.baseline` | `BaselineLearningService.cs`、`FactKeyCatalog.cs` |
| `bl.boot.summary` | `BaselineForecastFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `bl.ci.summary` | `BaselineForecastFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `bl.corr.summary` | `DevDiagAndCorrelationFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `bl.events` | `BaselineLearningService.cs`、`FactKeyCatalog.cs` |
| `bl.life` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `bl.pattern` | `DevDiagAndCorrelationFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `bl.quality` | `BaselineForecastFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `bl.score` | `BaselineLearningService.cs`、`FactKeyCatalog.cs` |
| `bl.season.summary` | `BaselineForecastFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `bl.temp.forecast` | `BaselineForecastFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `boot` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `boot.duration_ms` | `BootTimingFactsService.cs`、`FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `boot.last_time` | `BootTimingFactsService.cs`、`FactKeyCatalog.cs` |
| `boot.post_code` | `FactKeyCatalog.cs`、`IoPortFactsService.cs` |

### `byovd` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `byovd.hits` | `ByovdCompareService.cs`、`FactKeyCatalog.cs` |
| `byovd.rules` | `ByovdCompareService.cs`、`FactKeyCatalog.cs` |

### `cam` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `cam.count` | `CameraFactsService.cs`、`FactKeyCatalog.cs`、`KeyRangeGuardService.cs` |

### `cap` — 9 鍵

| Fact key | Producing modules |
|---|---|
| `cap.iop` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `cap.mmio` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `cap.msr` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `cap.pci` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `cap.pmu` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `cap.smbus` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `cap.tpm` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `cap.uefi.variables` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |
| `cap.wmi` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs` |

### `cert` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `cert.foreign_roots` | `FactKeyCatalog.cs`、`SecurityAuditFactsService.cs` |

### `chassis` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `chassis.security_status` | `ChassisAndHpaFactsService.cs`、`FactKeyCatalog.cs` |

### `chipset` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `chipset.bios_cntl` | `ChipsetSecurityService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |
| `chipset.me_hfs` | `ChipsetSecurityService.cs`、`FactKeyCatalog.cs` |
| `chipset.smramc` | `ChipsetSecurityService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |

### `cm` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `cm.affinity.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |
| `cm.cache.share.summary` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `cm.numa.proc.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |
| `cm.whea.timeline` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs` |

### `cmos` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `cmos.checksum` | `CmosService.cs`、`FactKeyCatalog.cs` |
| `cmos.rtc_time` | `CapabilityMatrixService.cs`、`CmosService.cs`、`FactKeyCatalog.cs` |
| `cmos.rtc_valid` | `CmosService.cs`、`FactKeyCatalog.cs` |

### `cpu` — 15 鍵

| Fact key | Producing modules |
|---|---|
| `cpu.die.count` | `FactKeyCatalog.cs`、`TopologyDeepenFactsService.cs` |
| `cpu.eff.curve` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |
| `cpu.generation` | `CpuFirmwareFactsService.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `cpu.pkg_c2_us` | `CoverageService.cs`、`FactKeyCatalog.cs`、`MemoryEncryptionFactsService.cs` |
| `cpu.pkg_c3_us` | `FactKeyCatalog.cs`、`MemoryEncryptionFactsService.cs` |
| `cpu.pkg_c6_us` | `FactKeyCatalog.cs`、`MemoryEncryptionFactsService.cs` |
| `cpu.pkg_c7_us` | `FactKeyCatalog.cs`、`MemoryEncryptionFactsService.cs` |
| `cpu.pl.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |
| `cpu.smi.cstate.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |
| `cpu.stepping.known` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |
| `cpu.thermal.events` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs` |
| `cpu.tjmax` | `CpuFirmwareFactsService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs`、`KeyRangeGuardService.cs` |
| `cpu.topology.smt` | `FactKeyCatalog.cs`、`TopologyDeepenFactsService.cs` |
| `cpu.turbo.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |
| `cpu.volt.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |

### `cxl` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `cxl.cfmws.count` | `CxlFactsService.cs`、`FactKeyCatalog.cs` |

### `dbg` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `dbg.kernel` | `DebugConfigService.cs`、`FactKeyCatalog.cs` |
| `dbg.start_options` | `DebugConfigService.cs`、`FactKeyCatalog.cs` |
| `dbg.testsigning` | `DebugConfigService.cs`、`FactKeyCatalog.cs` |

### `defender` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `defender.exclusions.count` | `FactKeyCatalog.cs`、`SecurityAuditFactsService.cs` |

### `display` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `display.hw.gpu` | `DisplayAdapterFactsService.cs`、`FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `display.summary` | `DisplayAdapterFactsService.cs`、`FactKeyCatalog.cs` |

### `dk` — 7 鍵

| Fact key | Producing modules |
|---|---|
| `dk.alias.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `dk.anon.stability` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dk.candidate` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GovernanceKnowledgeFactsService.cs` |
| `dk.docmap` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GovernanceKnowledgeFactsService.cs` |
| `dk.retention` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GovernanceKnowledgeFactsService.cs` |
| `dk.roundtrip` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dk.snapshot.age` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |

### `dl` — 15 鍵

| Fact key | Producing modules |
|---|---|
| `dl.cmdline.count` | `FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |
| `dl.cmdline.summary` | `FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |
| `dl.dll.summary` | `DevDiagAndCorrelationFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dl.dump` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`TrustOpsFactsService.cs` |
| `dl.etw.filter` | `EtwFilterViewService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dl.etw.summary` | `EtwFilterViewService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dl.gc.mode` | `EnvironmentRuntimeFactsService.cs`、`FactKeyCatalog.cs` |
| `dl.gc.summary` | `EnvironmentRuntimeFactsService.cs`、`FactKeyCatalog.cs` |
| `dl.leak.summary` | `DevDiagAndCorrelationFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dl.modconflict.count` | `ClosingFactsService.cs`、`FactKeyCatalog.cs` |
| `dl.modconflict.summary` | `ClosingFactsService.cs`、`FactKeyCatalog.cs` |
| `dl.stack` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |
| `dl.sympath` | `EnvironmentRuntimeFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dl.threadpool.count` | `EnvironmentRuntimeFactsService.cs`、`FactKeyCatalog.cs` |
| `dl.threadpool.summary` | `EnvironmentRuntimeFactsService.cs`、`FactKeyCatalog.cs` |

### `dm` — 8 鍵

| Fact key | Producing modules |
|---|---|
| `dm.clean` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceDashboardFactsService.cs` |
| `dm.conflict` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceDashboardFactsService.cs` |
| `dm.dash` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceDashboardFactsService.cs` |
| `dm.hidden.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `dm.maint.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GovernanceExtraFactsService.cs` |
| `dm.sense` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceDashboardFactsService.cs` |
| `dm.taskres.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `dm.timeline.summary` | `FactKeyCatalog.cs`、`MaintenanceDashboardFactsService.cs` |

### `drvinsp` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `drvinsp.drivers.count` | `DriverInspectionFactsService.cs`、`FactKeyCatalog.cs`、`KeyRangeGuardService.cs` |
| `drvinsp.truncated` | `DriverInspectionFactsService.cs`、`FactKeyCatalog.cs` |

### `ds` — 18 鍵

| Fact key | Producing modules |
|---|---|
| `ds.bl.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`VolumeEncryptionFactsService.cs` |
| `ds.cert.auto` | `DataSecurityFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ds.cert.count` | `FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |
| `ds.cert.summary` | `FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |
| `ds.cert.update` | `DataSecurityDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ds.dashboard` | `DataSecurityDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ds.devpol.deep` | `DataSecurityDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ds.devpol.summary` | `DataSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `ds.dha` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GovernanceExtraFactsService.cs` |
| `ds.drm` | `DataSecurityFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ds.efs.count` | `DataSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `ds.hello.count` | `DataSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `ds.sac` | `DataSecurityFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ds.sac.deep` | `DataSecurityDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ds.selfsigned.count` | `DataSecurityFactsService.cs`、`FactKeyCatalog.cs` |
| `ds.selfsigned.list` | `DataSecurityDeepFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ds.summary` | `DataSecurityFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ds.tls.summary` | `DataSecurityFactsService.cs`、`FactKeyCatalog.cs` |

### `dv` — 9 鍵

| Fact key | Producing modules |
|---|---|
| `dv.container.summary` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dv.env.path` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dv.nested` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dv.runtime.summary` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dv.sdk.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |
| `dv.toolchain.summary` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `dv.vm.count` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`HyperVInventoryFactsService.cs` |
| `dv.vm.inventory` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`HyperVInventoryFactsService.cs` |
| `dv.wsl.summary` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `eg` — 24 鍵

| Fact key | Producing modules |
|---|---|
| `eg.arm64` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.ci` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.corpus` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.coverage` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.docsingle` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.docslinks` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.fuzz` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.hygiene` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.keyapi` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.keydocs` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.knowledge` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.mixarch` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.notimpl` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.p95` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.pagenav` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.perfregress` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.prtemplate` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.realcorpus` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.security` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.selftel` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.semver` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.snapshot.matrix` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.untested` | `EngineeringGovernanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `eg.winbranch` | `EngineeringGovernance2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `el` — 8 鍵

| Fact key | Producing modules |
|---|---|
| `el.advisory.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `el.bios` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `el.browser.versions` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleAndReportsFactsService.cs` |
| `el.dash` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `el.driver.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `el.office.version` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleAndReportsFactsService.cs` |
| `el.os` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `el.pack.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |

### `ent` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `ent.fc` | `EnterpriseStorageFactsService.cs`、`FactKeyCatalog.cs` |
| `ent.iscsi` | `EnterpriseStorageFactsService.cs`、`FactKeyCatalog.cs` |
| `ent.mpio` | `EnterpriseStorageFactsService.cs`、`FactKeyCatalog.cs` |
| `ent.nvmeof` | `EnterpriseStorageFactsService.cs`、`FactKeyCatalog.cs` |

### `esp` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `esp.dbx` | `EspScanService.cs`、`FactKeyCatalog.cs` |
| `esp.files` | `EspScanService.cs`、`FactKeyCatalog.cs` |
| `esp.partitions.count` | `EspScanService.cs`、`FactKeyCatalog.cs` |

### `etl` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `etl.readback` | `EtlReadbackService.cs`、`FactKeyCatalog.cs` |
| `etl.traces.count` | `EtlReadbackService.cs`、`FactKeyCatalog.cs` |

### `ev` — 19 鍵

| Fact key | Producing modules |
|---|---|
| `ev.anon` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |
| `ev.audit.timeline` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.csv` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.dash` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.diff` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.fleet` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.i18n` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.import` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.migrate` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.namedquery` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.pack` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.pdf` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.query.agg` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.rulever` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.schedule` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.verify` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.watch` | `EvidenceExportFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.watermark` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ev.xinsig` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `evt` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `evt.system.7d` | `EventLogSummaryService.cs`、`FactKeyCatalog.cs` |

### `fs` — 23 鍵

| Fact key | Producing modules |
|---|---|
| `fs.bin.count` | `FactKeyCatalog.cs`、`RecycleBinFactsService.cs` |
| `fs.bin.roots` | `FactKeyCatalog.cs`、`RecycleBinFactsService.cs` |
| `fs.bin.size_mb` | `FactKeyCatalog.cs`、`RecycleBinFactsService.cs` |
| `fs.bits.count` | `FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `fs.bits.summary` | `FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `fs.insthist.count` | `FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `fs.insthist.summary` | `FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `fs.mft.stat` | `FactKeyCatalog.cs`、`NtfsVolumeFactsService.cs` |
| `fs.mount.count` | `FactKeyCatalog.cs`、`MountedDevicesFactsService.cs` |
| `fs.mount.letters` | `FactKeyCatalog.cs`、`MountedDevicesFactsService.cs` |
| `fs.mount.volumes` | `FactKeyCatalog.cs`、`MountedDevicesFactsService.cs` |
| `fs.pf.count` | `FactKeyCatalog.cs`、`PrefetchFactsService.cs` |
| `fs.pf.formats` | `FactKeyCatalog.cs`、`PrefetchFactsService.cs` |
| `fs.pf.latest` | `FactKeyCatalog.cs`、`PrefetchFactsService.cs` |
| `fs.pf.sample` | `FactKeyCatalog.cs`、`PrefetchFactsService.cs` |
| `fs.privacy.boundary` | `EnvironmentRuntimeFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `fs.shim.count` | `FactKeyCatalog.cs`、`ShimcacheFactsService.cs` |
| `fs.shim.latest` | `FactKeyCatalog.cs`、`ShimcacheFactsService.cs` |
| `fs.shim.sample` | `FactKeyCatalog.cs`、`ShimcacheFactsService.cs` |
| `fs.taskhist.count` | `FactKeyCatalog.cs`、`TaskHistoryFactsService.cs` |
| `fs.taskhist.last` | `FactKeyCatalog.cs`、`TaskHistoryFactsService.cs` |
| `fs.taskhist.summary` | `FactKeyCatalog.cs`、`TaskHistoryFactsService.cs` |
| `fs.usn.state` | `FactKeyCatalog.cs`、`NtfsVolumeFactsService.cs` |

### `fw` — 30 鍵

| Fact key | Producing modules |
|---|---|
| `fw.acpi.missing` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.bcd.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |
| `fw.bg.acm` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.bg.profile` | `FactKeyCatalog.cs`、`SpiFitFactsService.cs` |
| `fw.bitlocker.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |
| `fw.bootchain.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |
| `fw.capsule.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |
| `fw.dbx.generation` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |
| `fw.dbx.sbat` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.elam.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |
| `fw.fit.count` | `FactKeyCatalog.cs`、`SpiFitFactsService.cs` |
| `fw.fit.summary` | `FactKeyCatalog.cs`、`SpiFitFactsService.cs` |
| `fw.fpdt` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.fpdt.post` | `AcpiFpdtFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `fw.fv` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.gpt.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |
| `fw.me` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.me.manifest` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.psp` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.psp.dir` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.rt` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.smm.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |
| `fw.spiperm.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |
| `fw.tcgl.phase` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.ucode.cve` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.ucode.winner` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.var.attr` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.varstore` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.wdac.layer` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareDeepFactsService.cs` |
| `fw.wdac.source` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FirmwareAuditFactsService.cs` |

### `gd` — 7 鍵

| Fact key | Producing modules |
|---|---|
| `gd.dx.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `gd.hdr.summary` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `gd.plug.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `gd.shcache.summary` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `gd.tablet.count` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `gd.tdr.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `gd.vrr.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |

### `gp` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `gp.dvr.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs` |
| `gp.hags` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs` |
| `gp.pollrate` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MousePollRateFactsService.cs` |
| `gp.registrypol` | `AuditPolicyService.cs`、`FactKeyCatalog.cs` |

### `gpu` — 11 鍵

| Fact key | Producing modules |
|---|---|
| `gpu.lz.driver_version` | `FactKeyCatalog.cs` |
| `gpu.lz.memory` | `FactKeyCatalog.cs` |
| `gpu.lz.name` | `FactKeyCatalog.cs` |
| `gpu.radeon.fan` | `FactKeyCatalog.cs` |
| `gpu.radeon.name` | `FactKeyCatalog.cs` |
| `gpu.radeon.power` | `FactKeyCatalog.cs` |
| `gpu.radeon.temp` | `FactKeyCatalog.cs` |
| `gpu.retired_pages` | `FactKeyCatalog.cs`、`GpuTdrFactsService.cs`、`KeyRangeGuardService.cs` |
| `gpu.tdr.delay` | `FactKeyCatalog.cs`、`GpuTdrFactsService.cs` |
| `gpu.tdr.dpc` | `FactKeyCatalog.cs`、`GpuTdrFactsService.cs` |
| `gpu.tdr.level` | `FactKeyCatalog.cs`、`GpuTdrFactsService.cs` |

### `gx` — 12 鍵

| Fact key | Producing modules |
|---|---|
| `gx.amd` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.cap.rt` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.depth` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.drvchain` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.intel` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.multi` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.nv.ecc` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.nv.fan` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.nv.pl` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.nv.state` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.vbios` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `gx.vdisp` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |

### `hist` — 9 鍵

| Fact key | Producing modules |
|---|---|
| `hist.gpudriver` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |
| `hist.memtrain.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |
| `hist.mon.summary` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `hist.nicerr.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |
| `hist.pktloss` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `hist.rules.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |
| `hist.uncore.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs` |
| `hist.usb.speed` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `hist.vram` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GpuDeepFactsService.cs` |

### `hm` — 10 鍵

| Fact key | Producing modules |
|---|---|
| `hm.conflict.summary` | `EndpointPolicyFactsService.cs`、`FactKeyCatalog.cs` |
| `hm.drivhist.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |
| `hm.hwdiff` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`TrustOpsFactsService.cs` |
| `hm.prob.delta` | `FactKeyCatalog.cs`、`GovernanceExtraFactsService.cs` |
| `hm.prob.summary` | `FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `hm.sensdrift` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |
| `hm.tree` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `hm.unknown.count` | `FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `hm.unknown.list` | `FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `hm.wake.summary` | `EnvironmentRuntimeFactsService.cs`、`FactKeyCatalog.cs` |

### `hpa` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `hpa.dco` | `ChassisAndHpaFactsService.cs`、`FactKeyCatalog.cs` |

### `ie` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `ie.completion` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `ie.deeplink` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `ie.grafana` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `ie.ps.template` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |

### `in` — 11 鍵

| Fact key | Producing modules |
|---|---|
| `in.cfg.integrity` | `FactKeyCatalog.cs`、`SelfIntegrityFactsService.cs` |
| `in.cfg.repair` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `in.debug` | `FactKeyCatalog.cs`、`SelfIntegrityFactsService.cs` |
| `in.deps` | `FactKeyCatalog.cs`、`SelfIntegrityFactsService.cs` |
| `in.deps.integrity` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `in.elevation.summary` | `EnvironmentRuntimeFactsService.cs`、`FactKeyCatalog.cs` |
| `in.env` | `ClosingFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `in.privs` | `FactKeyCatalog.cs`、`SelfIntegrityFactsService.cs` |
| `in.report` | `FactKeyCatalog.cs`、`SelfIntegrityFactsService.cs` |
| `in.self` | `FactKeyCatalog.cs`、`SelfIntegrityFactsService.cs` |
| `in.update.sig` | `EnvironmentRuntimeFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `knowledge` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `knowledge.pack.status` | `FactKeyCatalog.cs`、`KnowledgePackService.cs` |
| `knowledge.pack.tables` | `FactKeyCatalog.cs`、`KnowledgePackService.cs` |

### `mchbar` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `mchbar.base` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`MchbarService.cs` |
| `mchbar.registers` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`MchbarService.cs` |

### `mem` — 6 鍵

| Fact key | Producing modules |
|---|---|
| `mem.encryption.sgx` | `FactKeyCatalog.cs`、`MemoryEncryptionFactsService.cs` |
| `mem.encryption.tme` | `FactKeyCatalog.cs`、`MemoryEncryptionFactsService.cs` |
| `mem.mixmatch.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |
| `mem.mrc.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |
| `mem.ras.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |
| `mem.rowhammer` | `FactKeyCatalog.cs`、`MemoryAttackSurfaceService.cs` |

### `mon` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `mon.count` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`MonitorConnectionService.cs` |
| `mon.dp` | `FactKeyCatalog.cs`、`MonitorConnectionService.cs` |

### `monitor` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `monitor.summary` | `EvidenceLabService.cs`、`FactKeyCatalog.cs`、`MonitorFactsService.cs` |

### `ms` — 23 鍵

| Fact key | Producing modules |
|---|---|
| `ms.bench.chain` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |
| `ms.benchdrift` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ms.boot.all` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ms.boot.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `ms.corelat` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |
| `ms.coresym.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |
| `ms.ctxsw` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |
| `ms.dpc` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |
| `ms.fancurve` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |
| `ms.fanzones` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ms.fslat.detail` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FsMetadataLatencyService.cs` |
| `ms.fslat.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FsMetadataLatencyService.cs` |
| `ms.gpu.fp16` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ms.irqaff` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |
| `ms.lo.throughput` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |
| `ms.memlat.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |
| `ms.ping` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ms.psu.eff` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |
| `ms.sleep` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |
| `ms.tau` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |
| `ms.tlb.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformReconciliationFactsService.cs` |
| `ms.usbps` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ms.xnuma` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MeasurementMethodologyFactsService.cs` |

### `msr` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `msr.0x8b` | `CpuFirmwareFactsService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |

### `mu` — 12 鍵

| Fact key | Producing modules |
|---|---|
| `mu.cam.count` | `EndpointPolicyFactsService.cs`、`FactKeyCatalog.cs` |
| `mu.codec.count` | `FactKeyCatalog.cs`、`MultimediaFactsService.cs` |
| `mu.codec.summary` | `FactKeyCatalog.cs`、`MultimediaFactsService.cs` |
| `mu.drm.components` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MediaProtectionFactsService.cs` |
| `mu.drm.summary` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `mu.hdr` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |
| `mu.hwdec.summary` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `mu.mf.summary` | `FactKeyCatalog.cs`、`MultimediaFactsService.cs` |
| `mu.mic.count` | `FactKeyCatalog.cs`、`MultimediaFactsService.cs` |
| `mu.mic.summary` | `FactKeyCatalog.cs`、`MultimediaFactsService.cs` |
| `mu.spatial.formats` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MediaProtectionFactsService.cs` |
| `mu.spatial.summary` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `net` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `net.listen.count` | `FactKeyCatalog.cs`、`ListenPortFactsService.cs` |
| `net.listen.entries` | `FactKeyCatalog.cs`、`ListenPortFactsService.cs` |
| `net.listen.unverified` | `FactKeyCatalog.cs`、`ListenPortFactsService.cs` |
| `net.offload` | `FactKeyCatalog.cs`、`NetOffloadFactsService.cs`、`RecoveryPowerAndInteropFactsService.cs` |

### `ng` — 10 鍵

| Fact key | Producing modules |
|---|---|
| `ng.checklist` | `ChecklistFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ng.coverage.covered` | `CoverageFactsService.cs`、`FactKeyCatalog.cs` |
| `ng.coverage.exempt` | `CoverageFactsService.cs`、`FactKeyCatalog.cs` |
| `ng.coverage.headline` | `CoverageFactsService.cs`、`FactKeyCatalog.cs` |
| `ng.coverage.keys` | `CoverageFactsService.cs`、`FactKeyCatalog.cs` |
| `ng.coverage.uncovered` | `CoverageFactsService.cs`、`FactKeyCatalog.cs` |
| `ng.history` | `EnvironmentRuntimeFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ng.progress` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `ng.report` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `ng.tree` | `ClosingFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `nic` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `nic.count` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`NicHealthFactsService.cs` |
| `nic.dirty_count` | `FactKeyCatalog.cs`、`NicHealthFactsService.cs` |
| `nic.gap` | `FactKeyCatalog.cs`、`NicLinkFactsService.cs` |
| `nic.mac.0` | `FactKeyCatalog.cs`、`NicHealthFactsService.cs` |

### `numa` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `numa.slit.nodes` | `FactKeyCatalog.cs`、`TopologyDeepenFactsService.cs` |
| `numa.topology` | `FactKeyCatalog.cs`、`NumaTopologyService.cs`、`PlatformReconciliationFactsService.cs` |
| `numa.topology.xnode` | `FactKeyCatalog.cs`、`NumaTopologyService.cs` |

### `nw` — 22 鍵

| Fact key | Producing modules |
|---|---|
| `nw.bt.fw` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `nw.bt.pairing` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |
| `nw.cert.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `nw.dns.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `nw.edid.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |
| `nw.eee.caps` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |
| `nw.fw.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `nw.hidbat.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |
| `nw.icc.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |
| `nw.legacy.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs` |
| `nw.offload2.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `nw.print.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |
| `nw.printer.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `nw.route.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |
| `nw.sensor.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |
| `nw.smb.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `nw.usb.desc.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |
| `nw.usb.port.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |
| `nw.vnic.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs` |
| `nw.wifi.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `nw.wol.packets` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |
| `nw.wol.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |

### `of` — 7 鍵

| Fact key | Producing modules |
|---|---|
| `of.bcd` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OfflineAnalysisFactsService.cs` |
| `of.diff` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OfflineAnalysisFactsService.cs` |
| `of.dump` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OfflineAnalysisFactsService.cs` |
| `of.evtx` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OfflineAnalysisFactsService.cs` |
| `of.hive` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OfflineAnalysisFactsService.cs` |
| `of.raw` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OfflineAnalysisFactsService.cs` |
| `of.writeblock` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OfflineAnalysisFactsService.cs` |

### `oob` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `oob.ipmi` | `FactKeyCatalog.cs`、`OobFactsService.cs` |

### `pci` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `pci.bus0.inventory` | `Bus0InventoryService.cs`、`FactKeyCatalog.cs` |
| `pci.dev.00.0` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |
| `pci.dev.1f.5` | `Bus0InventoryService.cs`、`FactKeyCatalog.cs` |
| `pci.res.1f.5` | `FactKeyCatalog.cs`、`FactRelationEngine.cs` |

### `pcieaer` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `pcieaer.ecam` | `EcamAerService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |
| `pcieaer.scan` | `EcamAerService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |

### `pl` — 10 鍵

| Fact key | Producing modules |
|---|---|
| `pl.cfgmigrate` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleSecurityFactsService.cs` |
| `pl.forward` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`QueryGovernanceFactsService.cs` |
| `pl.i18ninstaller` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleSecurityFactsService.cs` |
| `pl.package` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ProductAndPlatformFactsService.cs` |
| `pl.pagebudget` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ProductAndPlatformFactsService.cs` |
| `pl.portable` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`GovernanceExtraFactsService.cs` |
| `pl.releasenote` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleAndReportsFactsService.cs` |
| `pl.shotbaseline` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ProductAndPlatformFactsService.cs` |
| `pl.uiquality` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ProductAndPlatformFactsService.cs` |
| `pl.upgrade` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleSecurityFactsService.cs` |

### `platform` — 6 鍵

| Fact key | Producing modules |
|---|---|
| `platform.debug_interface` | `FactKeyCatalog.cs`、`PlatformSecurityMsrFacts.cs` |
| `platform.feature_control` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`PlatformSecurityMsrFacts.cs` |
| `platform.hvci` | `BackendEnvironmentService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |
| `platform.pawnio` | `BackendEnvironmentService.cs`、`FactKeyCatalog.cs` |
| `platform.secure_boot` | `BackendEnvironmentService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `platform.testsigning` | `BackendEnvironmentService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |

### `pmu` — 7 鍵

| Fact key | Producing modules |
|---|---|
| `pmu.fixed.0` | `FactKeyCatalog.cs`、`PmuCapabilityFactsService.cs` |
| `pmu.fixed_counters` | `FactKeyCatalog.cs`、`PmuCapabilityFactsService.cs` |
| `pmu.general_counters` | `FactKeyCatalog.cs`、`PmuCapabilityFactsService.cs` |
| `pmu.uncore.perf_status` | `FactKeyCatalog.cs`、`UncorePmuService.cs` |
| `pmu.uncore.platform` | `FactKeyCatalog.cs`、`UncorePmuService.cs` |
| `pmu.uncore.ratio_limit` | `FactKeyCatalog.cs`、`UncorePmuService.cs` |
| `pmu.version` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs`、`PmuCapabilityFactsService.cs` |

### `po` — 15 鍵

| Fact key | Producing modules |
|---|---|
| `po.compression` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.freqvar` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.gdi` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.handles` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.pgflt` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.pool` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.proctree` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ProductAndPlatformFactsService.cs` |
| `po.rboost` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.svcboot.summary` | `FactKeyCatalog.cs`、`ServiceBootFactsService.cs` |
| `po.svcboot.trigger` | `FactKeyCatalog.cs`、`ServiceBootFactsService.cs` |
| `po.svccrash` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.syscall.lat` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ProductAndPlatformFactsService.cs` |
| `po.timerres` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.trans` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PerformanceObservationFactsService.cs` |
| `po.ws` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ProductAndPlatformFactsService.cs` |

### `psu` — 4 鍵

| Fact key | Producing modules |
|---|---|
| `psu.pmbus.pout` | `FactKeyCatalog.cs`、`PsuPmbusFactsService.cs` |
| `psu.pmbus.temp1` | `FactKeyCatalog.cs`、`PsuPmbusFactsService.cs` |
| `psu.pmbus.vin` | `FactKeyCatalog.cs`、`PsuPmbusFactsService.cs` |
| `psu.pmbus.vout` | `FactKeyCatalog.cs`、`PsuPmbusFactsService.cs` |

### `pv` — 5 鍵

| Fact key | Producing modules |
|---|---|
| `pv.adid.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |
| `pv.ddv` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs` |
| `pv.speech.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |
| `pv.telemetry` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs` |
| `pv.uwp.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |

### `pw` — 9 鍵

| Fact key | Producing modules |
|---|---|
| `pw.battery.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |
| `pw.ms.compat` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `pw.plan.drift` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `pw.shutdown.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `pw.sleep.cap` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `pw.sleepstudy.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `pw.usbsel` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `pw.wake.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `pw.waketask.summary` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `qs` — 9 鍵

| Fact key | Producing modules |
|---|---|
| `qs.cache` | `ExportAndCacheFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `qs.grammar` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`QueryGovernanceFactsService.cs` |
| `qs.help` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`QueryInsightFactsService.cs` |
| `qs.history` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`QueryGovernanceFactsService.cs` |
| `qs.index` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`QueryInsightFactsService.cs` |
| `qs.keydiff` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`QueryInsightFactsService.cs` |
| `qs.range` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs` |
| `qs.trust` | `ClosingFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `qs.unit` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`QueryInsightFactsService.cs` |

### `raid` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `raid.oob` | `FactKeyCatalog.cs`、`OobFactsService.cs` |

### `rc` — 18 鍵

| Fact key | Producing modules |
|---|---|
| `rc.acl.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `rc.backup.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |
| `rc.dump.count` | `FactKeyCatalog.cs`、`MinidumpFactsService.cs` |
| `rc.dump.latest` | `FactKeyCatalog.cs`、`MinidumpFactsService.cs` |
| `rc.dump.sample` | `FactKeyCatalog.cs`、`MinidumpFactsService.cs` |
| `rc.exhaust.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |
| `rc.fastboot.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `rc.hang.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |
| `rc.hiber.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs` |
| `rc.io.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |
| `rc.link.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |
| `rc.lkr.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |
| `rc.msi.orphan.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `rc.pending` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |
| `rc.rp.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |
| `rc.vss.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryDisplayFactsService.cs` |
| `rc.wer.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |
| `rc.winsxs.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`RecoveryInventoryFactsService.cs` |

### `reg` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `reg.microcode` | `CpuFirmwareFactsService.cs`、`FactKeyCatalog.cs`、`FactRelationEngine.cs` |

### `role` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `role.surface` | `EvidenceLabService.cs`、`FactKeyCatalog.cs`、`RoleSurfaceFactsService.cs` |
| `role.surface.evidence` | `EvidenceLabService.cs`、`FactKeyCatalog.cs`、`RoleSurfaceFactsService.cs` |

### `rs` — 11 鍵

| Fact key | Producing modules |
|---|---|
| `rs.diff.json` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleAndReportsFactsService.cs` |
| `rs.fleet` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleAndReportsFactsService.cs` |
| `rs.helpfmt` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |
| `rs.jsonl` | `ExportAndCacheFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `rs.manual` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |
| `rs.print` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |
| `rs.rdiff` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleAndReportsFactsService.cs` |
| `rs.sbom` | `FactKeyCatalog.cs`、`SbomService.cs` |
| `rs.sig` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`TrustOpsFactsService.cs` |
| `rs.svg` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |
| `rs.xlsx` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |

### `sa` — 45 鍵

| Fact key | Producing modules |
|---|---|
| `sa.a11y.displayswitch.exe` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.a11y.magnify.exe` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.a11y.narrator.exe` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.a11y.osk.exe` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.a11y.read` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.a11y.sethc.exe` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.a11y.utilman.exe` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.acct.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |
| `sa.ads.deep` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditDeepFactsService.cs` |
| `sa.ads.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |
| `sa.appinit.load` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.appinit.read` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.appinit.value` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.assoc.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |
| `sa.autologon` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |
| `sa.bl.recovery` | `BitLockerRecoveryFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `sa.comhij.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |
| `sa.exposure.admin_shares` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.exposure.rdp` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.exposure.read` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.exposure.remote_registry` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.exposure.winrm` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.ifeo.scan` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.logon.deep` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditDeepFactsService.cs` |
| `sa.logon.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |
| `sa.lsp.count` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.lsp.read` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.path.deep` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditDeepFactsService.cs` |
| `sa.path.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |
| `sa.proxy.read` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.proxy.winhttp.server` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.proxy.wininet.enable` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.proxy.wininet.override` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.proxy.wininet.server` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.sddl.deep` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditDeepFactsService.cs` |
| `sa.sddl.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |
| `sa.svcbin.deep` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditDeepFactsService.cs` |
| `sa.svcbin.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |
| `sa.uninst.deep` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditDeepFactsService.cs` |
| `sa.uninst.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |
| `sa.winlogon.notify.count` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.winlogon.read` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.winlogon.shell` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.winlogon.userinit` | `FactKeyCatalog.cs`、`LocalSecurityAuditService.cs` |
| `sa.wmisub.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`SecurityAuditSupplementFactsService.cs` |

### `sb` — 22 鍵

| Fact key | Producing modules |
|---|---|
| `sb.dim.count` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.dim.serial.count` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.dim.summary` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.m2slot.count` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.m2slot.summary` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.mctl` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.oem.count` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.oem.list` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.pci.acs.summary` | `FactKeyCatalog.cs`、`PciCapabilityFactsService.cs` |
| `sb.pci.cap.count` | `FactKeyCatalog.cs`、`PciCapabilityFactsService.cs` |
| `sb.pci.cap.summary` | `FactKeyCatalog.cs`、`PciCapabilityFactsService.cs` |
| `sb.pci.dpc.summary` | `FactKeyCatalog.cs`、`PciCapabilityFactsService.cs` |
| `sb.pci.l1sub.summary` | `FactKeyCatalog.cs`、`PciCapabilityFactsService.cs` |
| `sb.pci.rev.known` | `FactKeyCatalog.cs`、`PciCapabilityFactsService.cs` |
| `sb.pci.vc.summary` | `FactKeyCatalog.cs`、`PciCapabilityFactsService.cs` |
| `sb.pci.vf.summary` | `FactKeyCatalog.cs`、`PciCapabilityFactsService.cs` |
| `sb.pcieaer.full.summary` | `FactKeyCatalog.cs`、`PciCapabilityFactsService.cs` |
| `sb.psu` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.slot.count` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.slot.summary` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.type03` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |
| `sb.type03.count` | `FactKeyCatalog.cs`、`SmbiosDeepFactsService.cs` |

### `sc` — 10 鍵

| Fact key | Producing modules |
|---|---|
| `sc.fanbearing` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `sc.io.summary` | `AdvancedSupplementFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `sc.netdrop.summary` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `sc.pcurve.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ScenarioThirdWaveFactsService.cs` |
| `sc.slow.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ScenarioThirdWaveFactsService.cs` |
| `sc.swap` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `sc.tbaseline` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `sc.uptime.summary` | `EnvironmentDiagnosticsFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `sc.verdict.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ScenarioThirdWaveFactsService.cs` |
| `sc.verifycheck.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ScenarioThirdWaveFactsService.cs` |

### `se` — 15 鍵

| Fact key | Producing modules |
|---|---|
| `se.audit2.summary` | `FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |
| `se.baseline` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `se.credroam` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleSecurityFactsService.cs` |
| `se.cve` | `CveOfflineFactsService.cs`、`FactKeyCatalog.cs` |
| `se.espbaseline` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleSecurityFactsService.cs` |
| `se.grade` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs`、`QueryInsightFactsService.cs` |
| `se.kerb.summary` | `EndpointPolicyFactsService.cs`、`FactKeyCatalog.cs` |
| `se.ntlm` | `EndpointPolicyFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `se.pipe.count` | `FactKeyCatalog.cs`、`TrustOpsFactsService.cs` |
| `se.pipe.summary` | `FactKeyCatalog.cs`、`TrustOpsFactsService.cs` |
| `se.rdp.cert` | `EndpointPolicyFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `se.share` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `se.surface` | `DeepBenchMethodology2FactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `se.winre.summary` | `EndpointPolicyFactsService.cs`、`FactKeyCatalog.cs` |
| `se.wpbt` | `ClosingFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `setuptl` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `setuptl.recent` | `FactKeyCatalog.cs`、`SetupApiTimelineService.cs` |
| `setuptl.sections.count` | `FactKeyCatalog.cs`、`SetupApiTimelineService.cs` |

### `sg` — 14 鍵

| Fact key | Producing modules |
|---|---|
| `sg.audio` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ScenarioSecondWaveFactsService.cs` |
| `sg.boot.summary` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `sg.bt` | `FactKeyCatalog.cs`、`ScenarioGuidanceFactsService.cs` |
| `sg.diskfull` | `DiskFullFactsService.cs`、`FactKeyCatalog.cs` |
| `sg.display` | `FactKeyCatalog.cs`、`ScenarioGuidanceFactsService.cs` |
| `sg.freeze` | `FactKeyCatalog.cs`、`FreezeDiagnosisFactsService.cs` |
| `sg.hot` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |
| `sg.input` | `FactKeyCatalog.cs`、`ScenarioGuidanceFactsService.cs` |
| `sg.mem.summary` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `sg.nicslow` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |
| `sg.print` | `FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs`、`ScenarioGuidanceFactsService.cs` |
| `sg.updcap` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformExpansionFactsService.cs` |
| `sg.usbslow` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ScenarioSecondWaveFactsService.cs` |
| `sg.wifi` | `FactKeyCatalog.cs`、`ScenarioGuidanceFactsService.cs` |

### `smart` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `smart.failing_now.count` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`SmartFailingNowFactsService.cs` |

### `smbus` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `smbus.tsod` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs`、`TsodSurveyor.cs` |

### `spi` — 9 鍵

| Fact key | Producing modules |
|---|---|
| `spi.bios_compare` | `FactKeyCatalog.cs`、`SpiFlashCompareService.cs` |
| `spi.bios_hash` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`SpiFlashHashService.cs` |
| `spi.entropy_map` | `FactKeyCatalog.cs`、`SpiEntropyService.cs` |
| `spi.flash_map` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`SpiFlashHashService.cs` |
| `spi.frap` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`SpiFlashService.cs` |
| `spi.hsfsts` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`SpiFlashService.cs` |
| `spi.prr` | `FactKeyCatalog.cs`、`SpiFlashService.cs` |
| `spi.regions` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`SpiFlashService.cs` |
| `spi.write_surface` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`SpiFlashService.cs` |

### `st` — 20 鍵

| Fact key | Producing modules |
|---|---|
| `st.align.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs` |
| `st.bridge.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageDeepFactsService.cs` |
| `st.ctrl.tree` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageDeepFactsService.cs` |
| `st.dwpd` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs`、`StorageDeepFactsService.cs` |
| `st.fs.refs` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FileSystemVolumeFactsService.cs` |
| `st.fs.volumes` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FileSystemVolumeFactsService.cs` |
| `st.fs.vss` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FileSystemVolumeFactsService.cs` |
| `st.fw.known` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs`、`StorageDeepFactsService.cs` |
| `st.iscsi.paths` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`IscsiSessionFactsService.cs` |
| `st.iscsi.sessions` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`IscsiSessionFactsService.cs` |
| `st.legacy.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs` |
| `st.ntfs.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs` |
| `st.nvme.err` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `st.nvme.extra` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `st.powerloss` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `st.pstrans.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |
| `st.sanitize.cap` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `st.ss.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs`、`StorageDeepFactsService.cs` |
| `st.wear.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`StorageAndScenarioReconciliationFactsService.cs` |
| `st.xcheck.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs`、`StorageDeepFactsService.cs` |

### `storage` — 2 鍵

| Fact key | Producing modules |
|---|---|
| `storage.reliability.available` | `FactKeyCatalog.cs`、`StorageReliabilityFactsService.cs` |
| `storage.reliability.count` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`RecoveryPowerAndInteropFactsService.cs`、`StorageReliabilityFactsService.cs` |

### `sv` — 14 鍵

| Fact key | Producing modules |
|---|---|
| `sv.adds` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleConditionalFactsService.cs` |
| `sv.cluster.state` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleConditionalFactsService.cs` |
| `sv.core` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerModeFactsService.cs` |
| `sv.dhcp` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleConditionalFactsService.cs` |
| `sv.dns.zones` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleConditionalFactsService.cs` |
| `sv.file.shares` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleFilePrintFactsService.cs` |
| `sv.hyperv.switches` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleConditionalFactsService.cs` |
| `sv.iis.apppools` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleConditionalFactsService.cs` |
| `sv.licensing` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleDeepFactsService.cs` |
| `sv.print.queues` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleFilePrintFactsService.cs` |
| `sv.rds` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleConditionalFactsService.cs` |
| `sv.roles.services` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleDeepFactsService.cs` |
| `sv.shares` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerRoleDeepFactsService.cs` |
| `sv.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ServerComplianceFactsService.cs` |

### `time` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `time.drift.ppm` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`TimeSyncFactsService.cs` |
| `time.hpet` | `FactKeyCatalog.cs`、`TimeSyncFactsService.cs` |
| `time.pm_timer` | `FactKeyCatalog.cs`、`TimeSyncFactsService.cs` |

### `tm` — 5 鍵

| Fact key | Producing modules |
|---|---|
| `tm.ntp.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |
| `tm.qpc.cost` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs` |
| `tm.rtc.drift` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |
| `tm.source` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |
| `tm.tzdb` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs` |

### `tpm` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `tpm.present` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs`、`RecoveryPowerAndInteropFactsService.cs`、`TpmFactsService.cs` |
| `tpm.replay.summary` | `FactKeyCatalog.cs`、`TpmReplayFactsService.cs` |
| `tpm.tcg_log` | `FactKeyCatalog.cs`、`TpmFactsService.cs` |

### `tu` — 10 鍵

| Fact key | Producing modules |
|---|---|
| `tu.battery` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tu.jitter` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tu.matrix` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tu.memhold` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tu.nightly` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tu.pfreq` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tu.rawpack` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tu.thermresp` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tu.ups` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `tu.vdroop` | `DeepBenchSecondWaveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `uefi` — 10 鍵

| Fact key | Producing modules |
|---|---|
| `uefi.audit_mode` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`UefiBootFactsService.cs` |
| `uefi.boot_entries.count` | `FactKeyCatalog.cs`、`UefiSignatureFactsService.cs` |
| `uefi.boot_order_count` | `FactKeyCatalog.cs`、`UefiBootFactsService.cs` |
| `uefi.db` | `CapabilityMatrixService.cs`、`FactKeyCatalog.cs`、`UefiSignatureFactsService.cs` |
| `uefi.dbx` | `FactKeyCatalog.cs`、`UefiSignatureFactsService.cs` |
| `uefi.deployed_mode` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`UefiBootFactsService.cs` |
| `uefi.kek` | `FactKeyCatalog.cs`、`UefiSignatureFactsService.cs` |
| `uefi.pk` | `FactKeyCatalog.cs`、`UefiSignatureFactsService.cs` |
| `uefi.secure_boot` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`UefiBootFactsService.cs` |
| `uefi.setup_mode` | `FactKeyCatalog.cs`、`FactRelationEngine.cs`、`UefiBootFactsService.cs` |

### `uf` — 11 鍵

| Fact key | Producing modules |
|---|---|
| `uf.channel` | `EnvironmentRuntimeFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uf.dash` | `EnvironmentRuntimeFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uf.driver.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `uf.duration.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `uf.ready` | `EndpointPolicyFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uf.retry.count` | `FactKeyCatalog.cs`、`UpdateRetryFactsService.cs` |
| `uf.retry.summary` | `FactKeyCatalog.cs`、`UpdateRetryFactsService.cs` |
| `uf.servicing` | `EnvironmentRuntimeFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `uf.size.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`LifecycleMaintenanceFactsService.cs` |
| `uf.source` | `EnvironmentRuntimeFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs`、`MaintenanceFactsService.cs` |
| `uf.timeline` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`QueryGovernanceFactsService.cs` |

### `ufv` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `ufv.bios.count` | `FactKeyCatalog.cs`、`UefiFvFactsService.cs` |

### `ups` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `ups.battery.percent` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`UpsFactsService.cs` |
| `ups.runtime_minutes` | `FactKeyCatalog.cs`、`UpsFactsService.cs` |
| `ups.status` | `FactKeyCatalog.cs`、`UpsFactsService.cs` |

### `usb` — 3 鍵

| Fact key | Producing modules |
|---|---|
| `usb.busiest_controller` | `FactKeyCatalog.cs`、`UsbTopologyService.cs` |
| `usb.controllers` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`UsbTopologyService.cs` |
| `usb.devices` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`UsbTopologyService.cs` |

### `usbstor` — 1 鍵

| Fact key | Producing modules |
|---|---|
| `usbstor.count` | `FactKeyCatalog.cs`、`SecurityAuditFactsService.cs` |

### `uv` — 11 鍵

| Fact key | Producing modules |
|---|---|
| `uv.cdat` | `BusCapabilityFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.cxl.summary` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.ext` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.hub` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.tb.route` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.tb.sec` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.usb.desc` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.usb.err` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.usb.pwr` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.usb.trim` | `BusCapabilityFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `uv.usb4` | `BusDeepDiveFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |

### `ux` — 12 鍵

| Fact key | Producing modules |
|---|---|
| `ux.age` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ux.copy` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |
| `ux.crashreport` | `ExportUxFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `ux.dpi` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |
| `ux.eco` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |
| `ux.errors` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |
| `ux.help.search` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |
| `ux.keybrowser` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |
| `ux.palette` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |
| `ux.provenance` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |
| `ux.theme` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |
| `ux.uia` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`UserExperience2FactsService.cs` |

### `virt` — 10 鍵

| Fact key | Producing modules |
|---|---|
| `virt.hns` | `FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |
| `virt.hypervisor` | `FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |
| `virt.judge` | `EvidenceLabService.cs`、`FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |
| `virt.msr` | `EvidenceLabService.cs`、`FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |
| `virt.platform` | `FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |
| `virt.vbs` | `FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |
| `virt.vendor` | `FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |
| `virt.vmcount` | `FactKeyCatalog.cs`、`KeyRangeGuardService.cs`、`VirtualizationFactsService.cs` |
| `virt.vmms` | `FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |
| `virt.vswitch` | `FactKeyCatalog.cs`、`VirtualizationFactsService.cs` |

### `vr` — 8 鍵

| Fact key | Producing modules |
|---|---|
| `vr.gantt` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`VisualizationFactsService.cs` |
| `vr.heatmap2d` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`VisualizationFactsService.cs` |
| `vr.multiaxis` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`VisualizationFactsService.cs` |
| `vr.radar` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`VisualizationFactsService.cs` |
| `vr.sankey` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`VisualizationFactsService.cs` |
| `vr.theme` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`VisualizationFactsService.cs` |
| `vr.tree` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`VisualizationFactsService.cs` |
| `vr.zoom` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`VisualizationFactsService.cs` |

### `ws` — 13 鍵

| Fact key | Producing modules |
|---|---|
| `ws.appx.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |
| `ws.autostart.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |
| `ws.bootdrv.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |
| `ws.channel.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSystemInventoryFactsService.cs` |
| `ws.driver.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformGovernanceFactsService.cs` |
| `ws.dumpcfg.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs` |
| `ws.kb.known` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`PlatformDeepDiveFactsService.cs` |
| `ws.lsa.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |
| `ws.minifilter.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InventoryGovernanceFactsService.cs` |
| `ws.power.events` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`NetworkSecurityInventoryFactsService.cs` |
| `ws.svc.graph` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`OperationsInventoryFactsService.cs` |
| `ws.task.summary` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |
| `ws.virt.contra` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`FinalInventoryFactsService.cs` |

### `xt` — 7 鍵

| Fact key | Producing modules |
|---|---|
| `xt.csharp` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |
| `xt.py` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |
| `xt.registry` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |
| `xt.rest` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |
| `xt.snapshot.fs` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |
| `xt.sysmon.map` | `AutomationAndGuidanceFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `xt.trigger` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`InterchangeAndReportFactsService.cs` |

### `zr` — 9 鍵

| Fact key | Producing modules |
|---|---|
| `zr.aadj` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |
| `zr.cba` | `EndpointPolicyFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `zr.compliance.baseline` | `DeviceComplianceFactsService.cs`、`FactKeyCatalog.cs` |
| `zr.compliance.table` | `DeviceComplianceFactsService.cs`、`FactKeyCatalog.cs` |
| `zr.dynlock` | `EndpointPolicyFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `zr.mdm` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |
| `zr.passkey` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`TrustOpsFactsService.cs` |
| `zr.report` | `EndpointPolicyFactsService.cs`、`FactCoverageReport.cs`、`FactKeyCatalog.cs` |
| `zr.wipe` | `FactCoverageReport.cs`、`FactKeyCatalog.cs`、`ZeroTrustFactsService.cs` |

# Appendix B — Dynamic key families

Twenty-three key families cannot be enumerated at compile time. Each declares why its membership is a runtime property and what determines it. This is a declaration, not a blanket exemption: a registered prefix with no producer in the source is judged a zombie prefix by `FactKeyCatalogTests`.

Twenty-three key families cannot be enumerated at compile time. Each declares why its membership is a runtime property and what determines it. This is a declaration, not a blanket exemption: a registered prefix with no producer in the source is judged a zombie prefix by `FactKeyCatalogTests`.

| # | Prefix | What determines the members |
|---:|---|---|
| 1 | `pci.dev.` | One entry per device present on bus 0 (members are device numbers, dev.fn). Which devices exist is a machine fact that cannot be enumerated at compile time. |
| 2 | `pci.res.` | One entry per device with expansion resources (BARs). As above: membership comes from bus enumeration. |
| 3 | `sio.0x` | Super I/O probe results named by the indicator port actually found (0x2E, 0x4E, possibly others). The port number is a platform fact, not a compile-time constant. |
| 4 | `sio.hwm.0x` | Super I/O hardware-monitor readings (fan, temperature, voltage, input voltage) grouped by the port detected. As with `sio.0x`, the port determines the grouping. |
| 5 | `pmu.fixed.` | One entry per fixed-function counter (`pmu.fixed.0…`), whose count is reported by CPUID leaf 0x0A — architectures with two, three and four all exist, so the members are not hard-coded. |
| 6 | `reconcile.` | One conclusion key per reconciliation rule (the key is determined by the rule id). A conclusion is not a fact being reconciled, so it is deliberately excluded from the coverage denominator — but it is a genuine runtime output and is registered here truthfully. |
| 7 | `audio.latency.` | One entry per audio endpoint (endpoint names come from MMDevice enumeration). Which audio devices a machine has is a property of the user's environment. |
| 8 | `audio.endpoint.` | One entry per audio endpoint (MMDevice enumeration index). The endpoint count is determined by the machine's audio devices and cannot be counted at compile time. |
| 9 | `drvinsp.` | One entry per driver file inspected (index plus field name, e.g. `0.summary`). How many suspicious drivers turn up is a scan result, not a compile-time constant. |
| 10 | `storage.reliability.` | One entry per disk per field (disk ordinal × 19 fields). How many disks exist and which fields the provider does not supply are facts about the machine and its drive firmware. |
| 11 | `asset.field.` | One entry per SMBIOS asset field (field names come from the table definitions: system manufacturer, board serial, and so on). The field set is a property of the specification rather than a constant, and absent fields also need somewhere to record their absence. |
| 12 | `sa.ifeo.` | One entry per image with an Image File Execution Options entry (image names come from registry enumeration and are arbitrary strings). Which images have a debugger attached is a machine fact; only images that *have* something attached produce keys. |
| 13 | `sa.lsp.` | One entry per Winsock layered service provider (entry number). Which providers are installed is a machine installation fact; `sa.lsp.count` is a fixed catalogue key. |
| 14 | `display.adapter.` | One entry per display adapter (names come from WMI). How many adapters there are and what they are called are machine facts. |
| 15 | `mon.` | One entry per monitor connection (index). How many screens are attached is decided by the environment. |
| 16 | `monitor.` | One entry per physical monitor (instance names `DesktopMonitorN…`). Names and counts come from WMI enumeration; `monitor.summary` is a fixed catalogue key. |
| 17 | `net.listen.` | One entry per listening port (protocol plus port number). What this machine is waiting for is machine state, and the port set comes from the connection table, so it cannot be listed at compile time. |
| 18 | `numa.node.` | One entry per NUMA node (node number). The node count is reported by `GetNumaHighestNodeNumber`; platforms from single-node up to eight nodes exist. |
| 19 | `acpi.table.` | One entry per ACPI table (signature plus ordinal within the same signature). Which tables this machine's firmware provides is a machine fact, and signatures and counts cannot be enumerated at compile time; the `acpi.tables` summary key remains in the static catalogue. |
| 20 | `in.deps.` | One entry per loaded module (module file name). Which DLLs are loaded is a fact about this process at this moment, and names are arbitrary; only modules judged *worth looking at* (unsigned, unverifiable, or a sideloading candidate) produce keys, and the `in.deps` summary key remains in the static catalogue. |
| 21 | `dl.dll.` | Per-process module detail (process names come from enumeration and are arbitrary strings). How many processes exist and what they are called are facts about the machine at that moment; keys are produced only with explicit consent (L2), and the `dl.dll.summary` summary key remains in the static catalogue. |
| 22 | `role.installed.` | One entry per Windows server role or feature (role name). Which are installed is an installation fact about this machine, and a Core installation can carry dozens of roles; names contain capitals and hyphens and therefore do not satisfy the catalogue's key-format rule, so they must travel as a family. |
| 23 | `tpm.replay.pcr.` | One entry per PCR in the TPM boot-chain replay (PCR index), computed for SHA-256 only on this machine. Which PCRs should be replayed and which are implemented on the machine are decided by the TCG log and the TPM itself; the `tpm.replay.summary` summary key remains in the static catalogue. |

---

# Appendix C — Reconciliation rule index

**18** reconciliation rules. A verdict is one of three — match, conflict or undecidable — with no score and no authenticity claim.

| Rule | Partition | Title | Required facts | Scope |
|---|---|---|---|---|
| `R-MEM-01` | 記憶體 | 各條記憶體模組並非同批 | `DimmCount`、`DimmManufacturers`、`DimmPartNumbers` | machine |
| `R-MEM-02` | 記憶體 | 記憶體模組序號異常 | `DimmCount`、`DimmSerials` | machine |
| `R-MEM-03` | 記憶體 | 記憶體未跑在標稱速度 | `DimmSpeedMts`、`DimmConfiguredMts` | machine |
| `R-MEM-04` | 記憶體 | 記憶體陣列宣稱與實際安裝對不上 | `DimmSizeTotalMiB`、`ArrayMaxCapacityMiB`、`ArraySlotCount`、`DimmCount` | per-disk |
| `R-MEM-05` | 記憶體 | 宣稱 ECC 與模組實際位元寬度對不上 | `MemEccType`、`MemEccBitsPresent` | per-disk |
| `R-SSD-01` | 儲存裝置 | 通電小時與累計寫入量對不上 | `NvmeDataUnitsWritten`、`NvmePowerOnHours` | per-disk |
| `R-SSD-02` | 儲存裝置 | 已用壽命與累計寫入量對不上 | `NvmePercentageUsed`、`NvmeDataUnitsWritten` | per-disk |
| `R-SSD-03` | 儲存裝置 | 不安全關機次數多於通電次數 | `NvmeUnsafeShutdowns`、`NvmePowerCycles` | per-disk |
| `R-SSD-04` | 儲存裝置 | NVMe 回報關鍵警告 | `NvmeCriticalWarning` | per-disk |
| `R-SSD-05` | 儲存裝置 | 宣稱容量與可定址容量對不上 | `DiskClaimedCapacityGB`、`AtaTotalLba` | per-disk |
| `R-SSD-06` | 儲存裝置 | 宣稱轉速與屬性集矛盾 | `AtaRotationRate`、`SmartSpinUpPresent` | per-disk |
| `R-BAT-01` | 電池 | 電池滿充容量明顯低於設計容量 | `BatteryDesignCapacityMWh`、`BatteryFullCapacityMWh` | machine |
| `R-CPU-06` | 處理器 | 各核心微碼版本不一致 | `CpuMicrocodePerCore` | machine |
| `R-CPU-02` | 處理器 | 品牌字串帶工程樣品標記 | `CpuBrandString` | machine |
| `R-CPU-03` | 處理器 | 快取層級異常 | `CpuL3Bytes`、`CpuL2TotalBytes`、`CpuPhysicalCores`、`CpuIsHybrid` | machine |
| `R-CPU-04` | 處理器 | 偵測到虛擬層，讀值可信度下降 | `HypervisorPresent` | machine |
| `R-CPU-05` | 處理器 | 矽晶基礎頻率與宣稱值對不上 | `CpuSiliconBaseMhz`、`CpuBrandClaimedMhz` | machine |
| `R-LNK-01` | PCIe 鏈路 | PCIe 鏈路寬度低於裝置能力 | `PcieCurWidth`、`PcieMaxWidth`、`PcieCurSpeed`、`PcieMaxSpeed` | machine |

# Appendix D — Explicit exemptions (summary)

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

# Appendix E — Service module index

**416** modules under `Services/`. "Keys owned" is the number of fact keys the file is responsible for producing in `FactKeyCatalog`.

| Module | Type | Lines | Keys owned |
|---|---|---:|---:|
| `FactKeyCatalog.cs` | FactKeyCatalog | 882 | 844 |
| `FactCoverageReport.cs` | FactCoverageReport | 701 | 496 |
| `FactRelationEngine.cs` | FactRelation | 526 | 28 |
| `LocalSecurityAuditService.cs` | LocalSecurityAuditService | 360 | 27 |
| `AcpiTableService.cs` | AcpiService | 258 | 23 |
| `RecoveryPowerAndInteropFactsService.cs` | RecoveryPowerAndInteropFactsService | 350 | 19 |
| `CapabilityMatrixService.cs` | CapabilityMatrixService | 66 | 18 |
| `KeyRangeGuardService.cs` | RangeVerdict | 111 | 17 |
| `FirmwareDeepFactsService.cs` | FirmwareDeepFactsService | 390 | 16 |
| `EngineeringGovernance2FactsService.cs` | EngineeringGovernance2FactsService | 363 | 14 |
| `EnvironmentRuntimeFactsService.cs` | EnvironmentRuntimeFactsService | 393 | 14 |
| `GpuDeepFactsService.cs` | GpuDeepFactsService | 425 | 14 |
| `MaintenanceFactsService.cs` | MaintenanceFactsService | 476 | 13 |
| `SmbiosDeepFactsService.cs` | SmbiosDeepFactsService | 210 | 13 |
| `DeepBenchSecondWaveFactsService.cs` | DeepBenchSecondWaveFactsService | 386 | 11 |
| `InterchangeAndReportFactsService.cs` | InterchangeAndReportFactsService | 541 | 11 |
| `LifecycleMaintenanceFactsService.cs` | LifecycleMaintenanceFactsService | 272 | 11 |
| `NetworkSystemInventoryFactsService.cs` | NetworkSystemInventoryFactsService | 477 | 11 |
| `PlatformReconciliationFactsService.cs` | PlatformReconciliationFactsService | 191 | 11 |
| `AdvancedSupplementFactsService.cs` | AdvancedSupplementFactsService | 627 | 10 |
| `AutomationAndGuidanceFactsService.cs` | AutomationAndGuidanceFactsService | 314 | 10 |
| `BusDeepDiveFactsService.cs` | BusDeepDiveFactsService | 347 | 10 |
| `DeepBenchMethodology2FactsService.cs` | DeepBenchMethodology2FactsService | 323 | 10 |
| `EndpointPolicyFactsService.cs` | EndpointPolicyFactsService | 338 | 10 |
| `EngineeringGovernanceFactsService.cs` | EngineeringGovernanceFactsService | 312 | 10 |
| `EnvironmentDiagnosticsFactsService.cs` | EnvironmentDiagnosticsFactsService | 339 | 10 |
| `EvidenceExportFactsService.cs` | EvidenceExportFactsService | 263 | 10 |
| `ExportUxFactsService.cs` | ExportUxFactsService | 260 | 10 |
| `FinalInventoryFactsService.cs` | FinalInventoryFactsService | 209 | 10 |
| `FirmwareAuditFactsService.cs` | FirmwareAuditFactsService | 232 | 10 |
| `InventoryGovernanceFactsService.cs` | InventoryGovernanceFactsService | 298 | 10 |
| `MeasurementMethodologyFactsService.cs` | MeasurementMethodologyFactsService | 286 | 10 |
| `NetworkSecurityInventoryFactsService.cs` | NetworkSecurityInventoryFactsService | 251 | 10 |
| `OperationsInventoryFactsService.cs` | OperationsInventoryFactsService | 489 | 10 |
| `PerformanceObservationFactsService.cs` | PerformanceObservationFactsService | 432 | 10 |
| `PlatformDeepDiveFactsService.cs` | PlatformDeepDiveFactsService | 256 | 10 |
| `PlatformExpansionFactsService.cs` | PlatformExpansionFactsService | 309 | 10 |
| `PlatformGovernanceFactsService.cs` | PlatformGovernanceFactsService | 256 | 10 |
| `RecoveryDisplayFactsService.cs` | RecoveryDisplayFactsService | 254 | 10 |
| `RecoveryInventoryFactsService.cs` | RecoveryInventoryFactsService | 296 | 10 |
| `SecurityAuditSupplementFactsService.cs` | SecurityAuditSupplementFactsService | 315 | 10 |
| `StorageAndScenarioReconciliationFactsService.cs` | StorageAndScenarioReconciliationFactsService | 406 | 10 |
| `UserExperience2FactsService.cs` | UserExperience2FactsService | 250 | 10 |
| `VirtualizationFactsService.cs` | VirtualizationFactsService | 320 | 10 |
| `ZeroTrustFactsService.cs` | ZeroTrustFactsService | 412 | 10 |
| `DataSecurityFactsService.cs` | DataSecurityFactsService | 325 | 9 |
| `PciCapabilityFactsService.cs` | PciCapabilityFactsService | 168 | 9 |
| `AmdSecurityFactsService.cs` | AmdSecurityFactsService | 164 | 8 |
| `VisualizationFactsService.cs` | VisualizationFactsService | 297 | 8 |
| `BackendEnvironmentService.cs` | BackendEnvironmentInputs | 208 | 7 |
| `ClosingFactsService.cs` | ClosingFactsService | 201 | 7 |
| `EvidenceLabService.cs` | EvidenceLabService | 1965 | 7 |
| `OfflineAnalysisFactsService.cs` | OfflineAnalysisFactsService | 252 | 7 |
| `ProductAndPlatformFactsService.cs` | ProductAndPlatformFactsService | 211 | 7 |
| `ServerRoleConditionalFactsService.cs` | ServerRoleConditionalFactsService | 507 | 7 |
| `AcpiDeepFactsService.cs` | AcpiDeepFactsService | 248 | 6 |
| `LifecycleAndReportsFactsService.cs` | LifecycleAndReportsFactsService | 217 | 6 |
| `MemoryEncryptionFactsService.cs` | MemoryEncryptionFactsService | 109 | 6 |
| `SecurityAuditDeepFactsService.cs` | SecurityAuditDeepFactsService | 298 | 6 |
| `SelfIntegrityFactsService.cs` | TokenPrivilegeSnapshot | 539 | 6 |
| `StorageDeepFactsService.cs` | StorageDeepFactsService | 269 | 6 |
| `TrustOpsFactsService.cs` | TrustOpsFactsService | 223 | 6 |
| `BaselineForecastFactsService.cs` | BaselineForecastFactsService | 230 | 5 |
| `CoverageFactsService.cs` | CoverageFactsService | 44 | 5 |
| `DataSecurityDeepFactsService.cs` | DataSecurityDeepFactsService | 219 | 5 |
| `LifecycleSecurityFactsService.cs` | LifecycleSecurityFactsService | 178 | 5 |
| `MaintenanceDashboardFactsService.cs` | MaintenanceDashboardFactsService | 207 | 5 |
| `MultimediaFactsService.cs` | MultimediaFactsService | 195 | 5 |
| `QueryInsightFactsService.cs` | QueryInsightFactsService | 264 | 5 |
| `ScenarioGuidanceFactsService.cs` | ScenarioGuidanceFactsService | 135 | 5 |
| `SecurityAuditFactsService.cs` | SecurityAuditFactsService | 177 | 5 |
| `SpiFlashService.cs` | SpiControllerAccess | 212 | 5 |
| `UefiBootFactsService.cs` | UefiBootFactsService | 151 | 5 |
| `UefiSignatureFactsService.cs` | UefiSignatureFactsService | 135 | 5 |
| `AssetFactsService.cs` | AssetFactsService | 139 | 4 |
| `CpuFirmwareFactsService.cs` | CpuFirmwareFactsService | 139 | 4 |
| `DevDiagAndCorrelationFactsService.cs` | DevDiagAndCorrelationFactsService | 221 | 4 |
| `EnterpriseStorageFactsService.cs` | EnterpriseStorageFactsService | 114 | 4 |
| `GovernanceExtraFactsService.cs` | GovernanceExtraFactsService | 164 | 4 |
| `GpuTdrFactsService.cs` | GpuTdrFactsService | 101 | 4 |
| `PmuCapabilityFactsService.cs` | PmuCapabilityFactsService | 72 | 4 |
| `PrefetchFactsService.cs` | PrefetchListing | 154 | 4 |
| `PsuPmbusFactsService.cs` | PsuPmbusReading | 168 | 4 |
| `QueryGovernanceFactsService.cs` | QueryGovernanceFactsService | 84 | 4 |
| `ScenarioThirdWaveFactsService.cs` | ScenarioThirdWaveFactsService | 217 | 4 |
| `BaselineLearningService.cs` | AnnotatedEvent | 274 | 3 |
| `ChipsetSecurityService.cs` | WinRing0PciConfigReader | 101 | 3 |
| `CmosService.cs` | CmosService | 98 | 3 |
| `DebugConfigService.cs` | DebugConfigService | 62 | 3 |
| `EspScanService.cs` | EspFileInfo | 206 | 3 |
| `FileSystemVolumeFactsService.cs` | FileSystemVolumeFactsService | 240 | 3 |
| `GovernanceKnowledgeFactsService.cs` | GovernanceKnowledgeFactsService | 137 | 3 |
| `ListenPortFactsService.cs` | ListenPortRow | 136 | 3 |
| `MinidumpFactsService.cs` | DumpListing | 137 | 3 |
| `MountedDevicesFactsService.cs` | MountedDevicesFactsService | 132 | 3 |
| `NicHealthFactsService.cs` | NicStatsEntry | 113 | 3 |
| `RecycleBinFactsService.cs` | RecycleBinFactsService | 101 | 3 |
| `ServerRoleDeepFactsService.cs` | ServerRoleDeepFactsService | 260 | 3 |
| `ShimcacheFactsService.cs` | ShimcacheFactsService | 118 | 3 |
| `SpiFitFactsService.cs` | SpiFitFactsService | 97 | 3 |
| `TaskHistoryFactsService.cs` | TaskHistoryFactsService | 102 | 3 |
| `TimeSyncFactsService.cs` | TimeSyncFactsService | 92 | 3 |
| `TopologyDeepenFactsService.cs` | TopologyDeepenFactsService | 88 | 3 |
| `UncorePmuService.cs` | UncoreSupport | 276 | 3 |
| `UpsFactsService.cs` | UpsFactsService | 89 | 3 |
| `UsbTopologyService.cs` | UsbEdge | 86 | 3 |
| `AuditPolicyService.cs` | AuditPolicyService | 142 | 2 |
| `BootTimingFactsService.cs` | BootTimingFactsService | 80 | 2 |
| `Bus0InventoryService.cs` | Bus0InventoryService | 100 | 2 |
| `BusCapabilityFactsService.cs` | BusCapabilityFactsService | 198 | 2 |
| `ByovdCompareService.cs` | ByovdCompareService | 110 | 2 |
| `ChassisAndHpaFactsService.cs` | ChassisFactsService | 187 | 2 |
| `DeviceComplianceFactsService.cs` | DeviceComplianceFactsService | 174 | 2 |
| `DisplayAdapterFactsService.cs` | DisplayAdapterFactsService | 102 | 2 |
| `DriverInspectionFactsService.cs` | DriverInspectionRow | 189 | 2 |
| `EcamAerService.cs` | EcamAerService | 134 | 2 |
| `EtlReadbackService.cs` | EtlFileSummary | 135 | 2 |
| `EtwFilterViewService.cs` | EtwFilterResult | 214 | 2 |
| `ExportAndCacheFactsService.cs` | JsonlExportService | 116 | 2 |
| `FsMetadataLatencyService.cs` | FsLatencySample | 133 | 2 |
| `HyperVInventoryFactsService.cs` | HyperVInventoryFactsService | 147 | 2 |
| `IscsiSessionFactsService.cs` | IscsiSessionFactsService | 182 | 2 |
| `KnowledgePackService.cs` | KnowledgeTable | 162 | 2 |
| `MchbarService.cs` | MchbarService | 98 | 2 |
| `MediaProtectionFactsService.cs` | MediaProtectionFactsService | 203 | 2 |
| `MonitorConnectionService.cs` | MonitorConnectionService | 69 | 2 |
| `NtfsVolumeFactsService.cs` | VolumeProbeResult | 179 | 2 |
| `NumaTopologyService.cs` | NumaTopologyService | 106 | 2 |
| `OobFactsService.cs` | OobFactsService | 95 | 2 |
| `PlatformSecurityMsrFacts.cs` | WinRing0KernelMsrReader | 96 | 2 |
| `PpttFactsService.cs` | PpttFactsService | 59 | 2 |
| `RoleSurfaceFactsService.cs` | RoleSurfaceFactsService | 152 | 2 |
| `ScenarioSecondWaveFactsService.cs` | ScenarioSecondWaveFactsService | 221 | 2 |
| `ServerRoleFilePrintFactsService.cs` | ServerRoleFilePrintFactsService | 322 | 2 |
| `ServiceBootFactsService.cs` | ServiceBootFactsService | 160 | 2 |
| `SetupApiTimelineService.cs` | SetupApiTimelineService | 99 | 2 |
| `SpiFlashHashService.cs` | SpiFlashHashService | 138 | 2 |
| `StorageReliabilityFactsService.cs` | StorageReliabilityRaw | 245 | 2 |
| `TpmFactsService.cs` | TpmFactsService | 158 | 2 |
| `UpdateRetryFactsService.cs` | UpdateRetryFactsService | 149 | 2 |
| `AcpiFpdtFactsService.cs` | FpdtPhase | 174 | 1 |
| `AudioEndpointFactsService.cs` | AudioEndpointFactsService | 143 | 1 |
| `AudioLatencyFactsService.cs` | AudioLatencyFactsService | 199 | 1 |
| `BitLockerRecoveryFactsService.cs` | BitLockerRecoveryFactsService | 265 | 1 |
| `CameraFactsService.cs` | PnpCameraEntry | 56 | 1 |
| `ChecklistFactsService.cs` | ChecklistFactsService | 261 | 1 |
| `CoverageService.cs` | CoverageService | 210 | 1 |
| `CppcCapabilityFactsService.cs` | CppcCapabilityFactsService | 166 | 1 |
| `CveOfflineFactsService.cs` | OfflineAdvisory | 167 | 1 |
| `CxlFactsService.cs` | CxlFactsService | 57 | 1 |
| `DiskFullFactsService.cs` | DiskUsageEntry | 214 | 1 |
| `EventLogSummaryService.cs` | EvtEntry | 88 | 1 |
| `FreezeDiagnosisFactsService.cs` | FreezeDiagnosisFactsService | 66 | 1 |
| `IoPortFactsService.cs` | IoPortFactsService | 36 | 1 |
| `MemoryAttackSurfaceService.cs` | MemoryAttackSurfaceService | 20 | 1 |
| `MonitorFactsService.cs` | MonitorFactsService | 180 | 1 |
| `MousePollRateFactsService.cs` | MousePollRateFactsService | 149 | 1 |
| `NetOffloadFactsService.cs` | WmiRow | 115 | 1 |
| `NicLinkFactsService.cs` | NicLinkFactsService | 196 | 1 |
| `SbomService.cs` | SbomComponent | 154 | 1 |
| `ServerComplianceFactsService.cs` | ServerComplianceFactsService | 77 | 1 |
| `ServerModeFactsService.cs` | ServerModeFactsService | 70 | 1 |
| `ServiceModeFactsService.cs` | ServiceModeFactsService | 132 | 1 |
| `SmartFailingNowFactsService.cs` | SmartFailingNowFactsService | 99 | 1 |
| `SpiEntropyService.cs` | RegionEntropy | 193 | 1 |
| `SpiFlashCompareService.cs` | SpiFlashCompareService | 77 | 1 |
| `TpmReplayFactsService.cs` | TpmReplayFactsService | 108 | 1 |
| `TsodSurveyor.cs` | TsodSurveyor | 79 | 1 |
| `UefiFvFactsService.cs` | UefiFvRow | 158 | 1 |
| `VolumeEncryptionFactsService.cs` | VolumeEncryptionFactsService | 131 | 1 |
| `AccentInk.cs` | AccentInk | 93 | 0 |
| `AiChatStore.cs` | AiChatStore | 77 | 0 |
| `AiService.cs` | AiProvider | 982 | 0 |
| `AiTools.cs` | AiTool | 146 | 0 |
| `AlertService.cs` | AlertEvent | 108 | 0 |
| `AmdSmuService.cs` | AmdSmuTelemetry | 178 | 0 |
| `AssetChangeDetector.cs` | AssetEventKind | 67 | 0 |
| `AtaIdentify.cs` | AtaIdentifyInfo | 93 | 0 |
| `AtomicWrite.cs` | — | 21 | 0 |
| `AudioSpectrumService.cs` | AudioSpectrumService | 227 | 0 |
| `AuditLogService.cs` | AuditLogService | 111 | 0 |
| `BatteryService.cs` | BatteryInfo | 102 | 0 |
| `BenchErrorMargin.cs` | BenchErrorMargin | 141 | 0 |
| `BenchRecord.cs` | BenchConditions | 276 | 0 |
| `BenchService.cs` | BenchService | 279 | 0 |
| `BiosMeDecoder.cs` | BiosMeDecoder | 128 | 0 |
| `BiosMeService.cs` | BiosMeService | 412 | 0 |
| `BluetoothBatteryService.cs` | BluetoothDeviceRow | 76 | 0 |
| `BootBreakdownDecoder.cs` | BootCulprit | 127 | 0 |
| `BootBreakdownService.cs` | BootBreakdownService | 193 | 0 |
| `BootRepairService.cs` | BootRepairService | 141 | 0 |
| `BottleneckAnalyzer.cs` | BottleneckKind | 580 | 0 |
| `BsodService.cs` | BsodRow | 100 | 0 |
| `Bufferbloat.cs` | BufferbloatResult | 26 | 0 |
| `BugCheck.cs` | — | 39 | 0 |
| `CacheBenchService.cs` | CacheLatencyRow | 214 | 0 |
| `CeilingDecoder.cs` | CeilingRow | 543 | 0 |
| `CeilingService.cs` | CeilingLoad | 1176 | 0 |
| `CeremonyService.cs` | CeremonyService | 123 | 0 |
| `ChessBenchService.cs` | BoardEngineKind | 401 | 0 |
| `ChessEngine.cs` | — | 338 | 0 |
| `ChipsetAnalysisService.cs` | ChipsetAnalysisService | 450 | 0 |
| `CleanupService.cs` | CleanCategory | 175 | 0 |
| `CliService.cs` | CliService | 497 | 0 |
| `ComputeChartService.cs` | ComputeMetric | 336 | 0 |
| `ContextMenuService.cs` | ContextMenuEntry | 222 | 0 |
| `CoreLatencyService.cs` | CoreLatencyService | 237 | 0 |
| `CoreTempMapService.cs` | CoreTempEntry | 157 | 0 |
| `CoreTimeDecoder.cs` | CoreTimeRow | 179 | 0 |
| `CoreTimeService.cs` | CoreTimeService | 178 | 0 |
| `CorpusUploadService.cs` | CorpusContributionV1 | 67 | 0 |
| `CpuAffinity.cs` | CpuAffinity | 259 | 0 |
| `CpuCoreUsageService.cs` | CoreLoadCell | 110 | 0 |
| `CpuIdService.cs` | CpuIdCacheRow | 655 | 0 |
| `CpuIdVerifyFacts.cs` | CpuIdVerifyFacts | 113 | 0 |
| `CpuMsrFacts.cs` | CpuMsrFacts | 39 | 0 |
| `CpuPinoutService.cs` | CpuPinoutService | 316 | 0 |
| `CpuSecurityService.cs` | SecurityBitRow | 144 | 0 |
| `CpuTopologyService.cs` | CpuTopologyService | 163 | 0 |
| `CpuzReportService.cs` | CpuzReportService | 427 | 0 |
| `CrashLog.cs` | CrashLog | 99 | 0 |
| `CudaService.cs` | CudaService | 35 | 0 |
| `DeepAccessService.cs` | DriverServiceState | 486 | 0 |
| `DeviceDiagnosticService.cs` | DeviceDiagnosticService | 430 | 0 |
| `Diag.cs` | DiagEntry | 125 | 0 |
| `DimmLayout.cs` | DimmSlotView | 367 | 0 |
| `DimmReferenceService.cs` | DimmReferenceService | 196 | 0 |
| `DiskBenchService.cs` | DiskBenchService | 273 | 0 |
| `DiskInfoService.cs` | DiskKind | 217 | 0 |
| `DiskIo.cs` | DiskIo | 102 | 0 |
| `DiskLatencyStats.cs` | DiskLatencyStats | 126 | 0 |
| `DiskScanService.cs` | ScanFile | 148 | 0 |
| `DiskSurfaceScanService.cs` | SurfaceBlock | 130 | 0 |
| `DisplayLinkDecoder.cs` | DisplayColorEncoding | 134 | 0 |
| `DisplayLinkService.cs` | DisplayLinkRow | 286 | 0 |
| `DnsService.cs` | NetAdapter | 228 | 0 |
| `DpcLatencyService.cs` | DpcRow | 373 | 0 |
| `DramTrafficDecoder.cs` | CounterValidation | 86 | 0 |
| `DramTrafficService.cs` | DramTrafficService | 195 | 0 |
| `DriverAnalysisService.cs` | DriverAnalysisService | 268 | 0 |
| `DriverAuditDecoder.cs` | DriverRow | 153 | 0 |
| `DriverAuditService.cs` | DriverAuditService | 187 | 0 |
| `DriverMmioReader.cs` | RawDeviceChannel | 189 | 0 |
| `DriverMsrReader.cs` | DriverMsrReader | 170 | 0 |
| `DriverReadyService.cs` | DriverReadyService | 156 | 0 |
| `EdidService.cs` | MonitorGamutInfo | 263 | 0 |
| `EnglishStrings.cs` | EnglishStrings | 1794 | 0 |
| `EnvCheckService.cs` | EnvCheckItem | 181 | 0 |
| `ErrorAggregationService.cs` | AggregatedError | 56 | 0 |
| `EtwTraceService.cs` | TraceKind | 361 | 0 |
| `EventsService.cs` | EventKind | 340 | 0 |
| `EvidenceCollection.cs` | EvidenceCollection | 279 | 0 |
| `EvidenceTimelineService.cs` | EvidenceTimelineService | 478 | 0 |
| `ExternalAcquisitionService.cs` | NullAcquisition | 50 | 0 |
| `ExternalReportService.cs` | ExternalReportService | 233 | 0 |
| `FactKeyDynamicCatalog.cs` | FactKeyDynamicCatalog | 85 | 0 |
| `FactQuery.cs` | QueryOperator | 44 | 0 |
| `FactStateLattice.cs` | FactStateLattice | 149 | 0 |
| `FakeCapacityTestService.cs` | FakeCapacityResult | 115 | 0 |
| `FanControlRow.cs` | FanControlRow | 89 | 0 |
| `FanCurveService.cs` | FanCurvePoint | 509 | 0 |
| `FeedbackService.cs` | FeedbackAttachment | 250 | 0 |
| `FileHash.cs` | — | 32 | 0 |
| `FirmwareService.cs` | FirmwareRow | 241 | 0 |
| `FrameTimeService.cs` | FrameProcessRow | 247 | 0 |
| `FrequencyTruthService.cs` | TurboRatioRow | 471 | 0 |
| `GpuAnalysisService.cs` | GpuAnalysisService | 220 | 0 |
| `GpuCodecService.cs` | GpuCodecService | 368 | 0 |
| `GpuRenderTestService.cs` | GpuRenderTestService | 390 | 0 |
| `GpuStressService.cs` | GpuStressService | 490 | 0 |
| `HardwareSnapshotService.cs` | HardwareSnapshotService | 488 | 0 |
| `HdrCapabilityService.cs` | HdrCapabilityRow | 222 | 0 |
| `HistoryStore.cs` | HistoryMetrics | 623 | 0 |
| `HtmlReportService.cs` | HtmlReportService | 92 | 0 |
| `ImcSmbusController.cs` | ImcSmbusController | 251 | 0 |
| `ImcSmbusDiscovery.cs` | WinRing0PciConfig | 79 | 0 |
| `InterruptAffinity.cs` | CpuInterruptRow | 163 | 0 |
| `InvisibleStallDecoder.cs` | InvisibleStallVerdict | 113 | 0 |
| `InvisibleStallService.cs` | ResidencyRow | 188 | 0 |
| `IoPortAccess.cs` | WinRing0IoPortAccess | 59 | 0 |
| `KernelModuleService.cs` | KernelModuleEntry | 280 | 0 |
| `KnowledgeCoverage.cs` | KnowledgeCoverage | 79 | 0 |
| `LanguageService.cs` | LanguageService | 364 | 0 |
| `LargePageDecoder.cs` | LargePageFacts | 122 | 0 |
| `LargePageService.cs` | LargePageService | 299 | 0 |
| `LatencyCurveService.cs` | LatencyBoundaryRow | 398 | 0 |
| `LicenseDecoder.cs` | LicenseVerdict | 83 | 0 |
| `LicenseService.cs` | LicenseService | 161 | 0 |
| `LocalApiHandler.cs` | LocalApiHandler | 68 | 0 |
| `LoopbackSignalService.cs` | LoopbackSignalStats | 37 | 0 |
| `M2AnalysisService.cs` | M2AnalysisService | 106 | 0 |
| `MachineAgeDecoder.cs` | DiskAge | 130 | 0 |
| `MachineAgeService.cs` | MachineAgeService | 110 | 0 |
| `MachineVerdict.cs` | VerdictLine | 89 | 0 |
| `MachineVerdictService.cs` | MachineVerdictService | 80 | 0 |
| `McaService.cs` | McaRow | 159 | 0 |
| `MemBandwidthMath.cs` | MemBandwidthRow | 268 | 0 |
| `MemBandwidthService.cs` | MemBandwidthService | 400 | 0 |
| `MemoryPoolService.cs` | MemoryPoolService | 168 | 0 |
| `MemoryService.cs` | MemOp | 222 | 0 |
| `MemoryTestService.cs` | MemoryTestRow | 455 | 0 |
| `MemoryTruthService.cs` | MemoryTruthService | 126 | 0 |
| `MigrationAggregator.cs` | MigrationHopRow | 155 | 0 |
| `MmioAccess.cs` | NotLoadedMmioReader | 44 | 0 |
| `MmioBackendSelector.cs` | MmioBackendSelector | 32 | 0 |
| `MotherboardAnalysisService.cs` | MotherboardAnalysisService | 152 | 0 |
| `NativeToolSource.cs` | NativeToolSection | 53 | 0 |
| `NetAdapterDecoder.cs` | NetAdapterProperty | 57 | 0 |
| `NetAdapterService.cs` | NetAdapterConfigRow | 164 | 0 |
| `NetworkService.cs` | NetworkService | 222 | 0 |
| `NetworkSpeedService.cs` | NodeProtocol | 275 | 0 |
| `NpuComputeBenchService.cs` | ComputeBenchResult | 50 | 0 |
| `NpuDetectionService.cs` | NpuDetectionService | 264 | 0 |
| `NvmeHealth.cs` | NvmeHealth | 69 | 0 |
| `NvmeLogDecoder.cs` | NvmeErrorEntry | 171 | 0 |
| `NvmePowerDecoder.cs` | NvmePowerStateRow | 279 | 0 |
| `NvmePowerService.cs` | NvmePowerService | 299 | 0 |
| `OptionalFeatureService.cs` | OptionalFeatureEntry | 77 | 0 |
| `OsAnalysisService.cs` | OsFactRow | 301 | 0 |
| `PciResourceAuditService.cs` | PciResourceAuditService | 422 | 0 |
| `PcieAnalysisService.cs` | PcieAnalysisService | 386 | 0 |
| `PcieLinkDecoder.cs` | PcieLinkRow | 246 | 0 |
| `PcieLinkService.cs` | PcieLinkService | 317 | 0 |
| `PcieVerifyFacts.cs` | PcieVerifyFacts | 33 | 0 |
| `PlatformTrustService.cs` | TrustRow | 196 | 0 |
| `PmuProgrammingService.cs` | PmuRoundResult | 132 | 0 |
| `PortUsageService.cs` | PortRow | 290 | 0 |
| `PowerDeliveryService.cs` | PowerDeliveryService | 317 | 0 |
| `PowerPolicyDecoder.cs` | PowerPolicyRow | 237 | 0 |
| `PowerPolicyService.cs` | PowerPolicyService | 240 | 0 |
| `ProfileService.cs` | Scene | 445 | 0 |
| `QueryExecutor.cs` | QueryExecutor | 55 | 0 |
| `QueryParser.cs` | QueryParseException | 113 | 0 |
| `RawRegisterCollectService.cs` | RawSnapshotIntegrity | 183 | 0 |
| `RawRegisterSnapshotService.cs` | RawRegisterRegion | 116 | 0 |
| `RawRegisterSnapshotStore.cs` | RawRegisterSnapshotStore | 54 | 0 |
| `RdtService.cs` | RdtCoreRow | 330 | 0 |
| `ReliabilityHistoryService.cs` | ReliabilityRow | 193 | 0 |
| `ReportService.cs` | ReportService | 861 | 0 |
| `ResizableBarDecoder.cs` | BarDeviceRow | 138 | 0 |
| `ResizableBarService.cs` | ResizableBarService | 288 | 0 |
| `RowhammerProbeService.cs` | RowhammerProbeResult | 151 | 0 |
| `RuleLoader.cs` | ExternalRuleEngine | 133 | 0 |
| `RuleValidator.cs` | RuleValidator | 75 | 0 |
| `SataAnalysisService.cs` | SataAnalysisService | 238 | 0 |
| `ScheduledTaskStartup.cs` | ScheduledTaskStartupInfo | 136 | 0 |
| `SelfTelemetry.cs` | TelemetryScan | 89 | 0 |
| `SensorLogService.cs` | SensorLogService | 111 | 0 |
| `SensorSanity.cs` | SensorSanity | 95 | 0 |
| `SensorService.Fans.cs` | SensorService | 74 | 0 |
| `SensorService.cs` | SensorService | 552 | 0 |
| `ServiceInventoryService.cs` | SvcEntry | 96 | 0 |
| `SettingsService.cs` | SettingsService | 559 | 0 |
| `SharedAiEndpoint.cs` | SharedAiEndpoint | 81 | 0 |
| `Shell.cs` | Shell | 66 | 0 |
| `ShrineMath.cs` | ShrineMath | 72 | 0 |
| `SlcCacheBenchService.cs` | SlcSample | 214 | 0 |
| `SleepDiagnosticsService.cs` | SleepDiagnosticsService | 187 | 0 |
| `SmbiosFacts.cs` | SmbiosFacts | 127 | 0 |
| `SmbiosService.cs` | SmbiosStruct | 616 | 0 |
| `SmbusBusLock.cs` | SmbusBusLock | 96 | 0 |
| `SmbusController.cs` | WinRing0SmbusIo | 405 | 0 |
| `SpdBus.cs` | SpdBusAddresses | 112 | 0 |
| `SpdConsistencyAuditService.cs` | SpdConsistencyAuditService | 488 | 0 |
| `SpdDecoder.cs` | SpdManufacturer | 184 | 0 |
| `SpdDetail.cs` | SpdModuleType | 76 | 0 |
| `SpdDetailDecoder.cs` | SpdDetailDecoder | 154 | 0 |
| `SpdDisplay.cs` | SpdDisplay | 192 | 0 |
| `SpdReader.cs` | SpdKind | 329 | 0 |
| `SpdSurveyor.cs` | SpdDirectRead | 123 | 0 |
| `SpecFactsCollector.cs` | SpecFactsCollector | 89 | 0 |
| `SpecSummary.cs` | SpecFacts | 79 | 0 |
| `StartupService.cs` | StartupEntry | 362 | 0 |
| `StorageProbeBreaker.cs` | StorageProbeBreaker | 105 | 0 |
| `StorageSmartService.cs` | SmartDriveRow | 930 | 0 |
| `StressBridgeService.cs` | StressBridgeService | 211 | 0 |
| `StressTestService.cs` | StressMode | 286 | 0 |
| `SuperIoHwmFactsService.cs` | SuperIoHwmFactsService | 275 | 0 |
| `SuperIoProbeService.cs` | SuperIoProbeService | 87 | 0 |
| `SuperPiService.cs` | SuperPiService | 272 | 0 |
| `SystemInfoService.cs` | SystemInfoService | 254 | 0 |
| `TbUsb4Service.cs` | TbUsb4Row | 44 | 0 |
| `TerminalService.cs` | TerminalService | 170 | 0 |
| `ThemeService.cs` | AppTheme | 369 | 0 |
| `ThermalStickyService.cs` | StickyBitRow | 87 | 0 |
| `ThreadMigrationService.cs` | ThreadMigrationService | 373 | 0 |
| `TimerFoundationService.cs` | TimerFoundationRow | 130 | 0 |
| `ToolboxService.cs` | ToolKind | 749 | 0 |
| `TopDownService.cs` | TopDownMath | 446 | 0 |
| `TpmSecureBootService.cs` | TrustSecurityRow | 66 | 0 |
| `TrayService.cs` | TrayService | 121 | 0 |
| `TrendSentinelService.cs` | SentinelFinding | 266 | 0 |
| `TroubleshootService.cs` | TroubleshootScenario | 104 | 0 |
| `UpdateCheckService.cs` | UpdateCheckService | 84 | 0 |
| `UpgradeAdvisor.cs` | UpgradePart | 452 | 0 |
| `UsbLinkDecoder.cs` | UsbPortRow | 245 | 0 |
| `UsbLinkService.cs` | UsbLinkService | 358 | 0 |
| `VerifyFactsCollector.cs` | VerifyFactsCollector | 79 | 0 |
| `VerifyRules.cs` | VerifyRule | 562 | 0 |
| `VerifyThresholds.cs` | VerifyThresholds | 45 | 0 |
| `VizPalette.cs` | VizPalette | 78 | 0 |
| `VolumeService.cs` | VolumeInfo | 119 | 0 |
| `VrmControllerService.cs` | VrmControllerService | 479 | 0 |
| `WheaErrorService.cs` | WheaRow | 130 | 0 |
| `WheaTimelineStore.cs` | WheaTimelineEvent | 57 | 0 |
| `WifiSignalService.cs` | WifiSignalRow | 204 | 0 |
| `WinRing0Bridge.cs` | WinRing0Bridge | 410 | 0 |
| `WinRing0MmioReader.cs` | WinRing0MmioReader | 58 | 0 |
| `WindowsUpdateHistoryService.cs` | WuHistoryEntry | 118 | 0 |
| `WingetService.cs` | WingetPackage | 418 | 0 |
| `WinsatService.cs` | WinsatService | 251 | 0 |
| `WiringDecisions.cs` | WiringDecisions | 47 | 0 |
| `WriteGate.cs` | WriteGate | 76 | 0 |
| `XiangqiEngine.cs` | — | 296 | 0 |
| `XsRegProbeContract.cs` | XsRegProbeContract | 61 | 0 |
| `ZhTerms.cs` | ZhTerms | 186 | 0 |

# Appendix F — Test file index

**433** test files, with **4568** `[Fact]`/`[Theory]` markers in total.

| Test file | Fact | Theory | Lines |
|---|---:|---:|---:|
| `CeilingDecoderTests.cs` | 56 | 0 | 548 |
| `HistoryAndEventsTests.cs` | 48 | 1 | 765 |
| `UsbLinkTests.cs` | 39 | 2 | 442 |
| `BenchLogTests.cs` | 40 | 0 | 471 |
| `UpgradeAdvisorTests.cs` | 31 | 4 | 467 |
| `EtwTraceServiceTests.cs` | 32 | 2 | 374 |
| `MemBandwidthMathTests.cs` | 31 | 2 | 336 |
| `QueryAndTrustFactsTests.cs` | 33 | 0 | 403 |
| `CpuIdTests.cs` | 32 | 0 | 296 |
| `VerifyRulesTests.cs` | 14 | 18 | 340 |
| `NetworkSystemInventoryFactsTests.cs` | 31 | 0 | 44 |
| `OperationsInventoryFactsTests.cs` | 31 | 0 | 311 |
| `AdvancedSupplementFactsTests.cs` | 30 | 0 | 301 |
| `EnvironmentDiagnosticsFactsTests.cs` | 28 | 0 | 295 |
| `InterchangeAndReportFactsTests.cs` | 28 | 0 | 415 |
| `SiliconQualityTests.cs` | 28 | 0 | 367 |
| `FactStateLatticeTests.cs` | 24 | 3 | 287 |
| `PowerPolicyTests.cs` | 27 | 0 | 275 |
| `ServerRoleConditional2FactsTests.cs` | 26 | 0 | 331 |
| `AutomationAndGuidanceFactsTests.cs` | 25 | 0 | 315 |
| `PcieLinkTests.cs` | 23 | 2 | 249 |
| `ProfileTests.cs` | 24 | 0 | 357 |
| `RecoveryPowerAndInteropFactsTests.cs` | 24 | 0 | 249 |
| `BottleneckAnalyzerTests.cs` | 22 | 1 | 247 |
| `CpuAffinityTests.cs` | 17 | 6 | 210 |
| `DecoderHardeningTests.cs` | 22 | 1 | 418 |
| `DeviceIconsTests.cs` | 6 | 17 | 359 |
| `FactRelationTests.cs` | 23 | 0 | 549 |
| `FirmwareDeepFactsTests.cs` | 23 | 0 | 202 |
| `MaintenanceFactsTests.cs` | 23 | 0 | 415 |
| `SelfIntegrityFactsTests.cs` | 22 | 1 | 370 |
| `SpiFlashTests.cs` | 23 | 0 | 289 |
| `DimmLayoutTests.cs` | 21 | 1 | 261 |
| `EngineeringGovernance2FactsTests.cs` | 22 | 0 | 222 |
| `FanCurveTests.cs` | 21 | 1 | 320 |
| `NetworkSecurityInventoryFactsTests.cs` | 22 | 0 | 33 |
| `SmbusControllerTests.cs` | 16 | 6 | 307 |
| `AiToolboxTests.cs` | 18 | 3 | 252 |
| `BusDeepDiveFactsTests.cs` | 21 | 0 | 196 |
| `DiagTests.cs` | 21 | 0 | 258 |
| `FinalInventoryFactsTests.cs` | 21 | 0 | 29 |
| `InventoryGovernanceFactsTests.cs` | 21 | 0 | 32 |
| `MaintenanceAndGovernanceFactsTests.cs` | 21 | 0 | 262 |
| `PciAndPpttTests.cs` | 21 | 0 | 338 |
| `PlatformDeepDiveFactsTests.cs` | 21 | 0 | 29 |
| `RecoveryDisplayFactsTests.cs` | 21 | 0 | 29 |
| `RecoveryInventoryFactsTests.cs` | 21 | 0 | 29 |
| `SecurityAuditSupplementFactsTests.cs` | 21 | 0 | 29 |
| `ServerRoleFilePrintFactsTests.cs` | 18 | 3 | 229 |
| `ZeroTrustFactsTests.cs` | 21 | 0 | 239 |
| `AcpiDeepFactsTests.cs` | 20 | 0 | 217 |
| `AudioLatencyJudgeTests.cs` | 17 | 3 | 232 |
| `CpuzReportParsingTests.cs` | 20 | 0 | 343 |
| `DeepBenchSecondWaveFactsTests.cs` | 20 | 0 | 206 |
| `EndpointPolicyFactsTests.cs` | 20 | 0 | 244 |
| `EvidenceTimelineServiceTests.cs` | 19 | 1 | 373 |
| `FileSystemVolumeFactsTests.cs` | 20 | 0 | 232 |
| `LifecycleAndGovernanceFactsTests.cs` | 20 | 0 | 279 |
| `PerformanceObservationFactsTests.cs` | 20 | 0 | 200 |
| `PlatformReconciliationFactsTests.cs` | 20 | 0 | 30 |
| `ScenarioSecondWaveFactsTests.cs` | 20 | 0 | 225 |
| `SmartDecoderTests.cs` | 20 | 0 | 371 |
| `ToolboxFilterTests.cs` | 20 | 0 | 199 |
| `TrendSentinelTests.cs` | 20 | 0 | 320 |
| `CoreHeatmapTests.cs` | 18 | 1 | 402 |
| `CoreTimeTests.cs` | 19 | 0 | 208 |
| `EraCalendarTests.cs` | 14 | 5 | 176 |
| `GpuDeepFactsTests.cs` | 19 | 0 | 169 |
| `StorageAndScenarioReconciliationFactsTests.cs` | 19 | 0 | 221 |
| `ThreadMigrationTests.cs` | 19 | 0 | 229 |
| `BiosMeTests.cs` | 18 | 0 | 168 |
| `ImcSmbusControllerTests.cs` | 16 | 2 | 265 |
| `PlatformGovernanceFactsTests.cs` | 18 | 0 | 26 |
| `SecurityAuditDeepFactsTests.cs` | 18 | 0 | 206 |
| `ServiceBootFactsTests.cs` | 18 | 0 | 199 |
| `AcpiDeepTests.cs` | 17 | 0 | 331 |
| `BitLockerRecoveryFactsTests.cs` | 14 | 3 | 168 |
| `ChecklistFactsTests.cs` | 14 | 3 | 244 |
| `EnvironmentRuntimeFactsTests.cs` | 17 | 0 | 281 |
| `MeasurementMethodologyFactsTests.cs` | 17 | 0 | 155 |
| `MediaProtectionFactsTests.cs` | 17 | 0 | 188 |
| `PlatformTrustTests.cs` | 17 | 0 | 123 |
| `SpdDecoderTests.cs` | 12 | 5 | 256 |
| `StorageDeepFactsTests.cs` | 17 | 0 | 199 |
| `VirtualizationJudgeTests.cs` | 15 | 2 | 278 |
| `BaselineForecastFactsTests.cs` | 16 | 0 | 183 |
| `DataSecurityDeepFactsTests.cs` | 16 | 0 | 193 |
| `DataSecurityFactsTests.cs` | 16 | 0 | 204 |
| `DriverAuditTests.cs` | 14 | 2 | 170 |
| `LocalSecurityAuditTests.cs` | 16 | 0 | 193 |
| `MicroarchProfileTests.cs` | 9 | 7 | 199 |
| `MultimediaAndExportFactsTests.cs` | 16 | 0 | 222 |
| `UpdateRetryFactsTests.cs` | 14 | 2 | 187 |
| `DeepBenchMethodology2FactsTests.cs` | 15 | 0 | 142 |
| `EcamAerTests.cs` | 15 | 0 | 272 |
| `EvidenceExportFactsTests.cs` | 15 | 0 | 142 |
| `FirmwareAuditFactsTests.cs` | 15 | 0 | 23 |
| `FrequencyTruthMathTests.cs` | 15 | 0 | 132 |
| `FsMetadataLatencyFactsTests.cs` | 15 | 0 | 184 |
| `MonitorJudgeTests.cs` | 15 | 0 | 180 |
| `NicLinkGapTests.cs` | 12 | 3 | 195 |
| `PcieNegotiationGapTests.cs` | 13 | 2 | 236 |
| `ScenarioThirdWaveFactsTests.cs` | 15 | 0 | 187 |
| `SensorSanityTests.cs` | 3 | 12 | 143 |
| `ServerComplianceFactsTests.cs` | 15 | 0 | 271 |
| `ServerRoleConditionalFactsTests.cs` | 14 | 1 | 198 |
| `ServerRoleDeepFactsTests.cs` | 14 | 1 | 230 |
| `SpdReaderTests.cs` | 14 | 1 | 280 |
| `UefiFvTests.cs` | 15 | 0 | 389 |
| `VrmControllerServiceTests.cs` | 11 | 4 | 356 |
| `AcpiFpdtFactsTests.cs` | 14 | 0 | 181 |
| `BenchErrorMarginTests.cs` | 14 | 0 | 158 |
| `ChipsetSecurityTests.cs` | 14 | 0 | 141 |
| `DeepBenchLegacyAdapterTests.cs` | 13 | 1 | 301 |
| `DevDiagAndCorrelationFactsTests.cs` | 14 | 0 | 181 |
| `DeviceComplianceFactsTests.cs` | 13 | 1 | 208 |
| `DisplayAdapterJudgeTests.cs` | 14 | 0 | 197 |
| `EngineeringGovernanceFactsTests.cs` | 14 | 0 | 134 |
| `ExportUxFactsTests.cs` | 14 | 0 | 136 |
| `LifecycleMaintenanceFactsTests.cs` | 14 | 0 | 22 |
| `MousePollRateFactsTests.cs` | 13 | 1 | 145 |
| `OfflineAnalysisFactsTests.cs` | 14 | 0 | 136 |
| `PlatformExpansionFactsTests.cs` | 14 | 0 | 22 |
| `ScheduledTaskStartupTests.cs` | 12 | 2 | 165 |
| `ServiceModeFactsTests.cs` | 12 | 2 | 137 |
| `SmbiosExtendedTests.cs` | 8 | 6 | 140 |
| `StorageQdJudgeTests.cs` | 14 | 0 | 176 |
| `AmdSmuServiceTests.cs` | 11 | 2 | 177 |
| `BaselineLearningTests.cs` | 12 | 1 | 207 |
| `BusCapabilityFactsTests.cs` | 13 | 0 | 145 |
| `ChangelogTests.cs` | 12 | 1 | 237 |
| `CrashLogTests.cs` | 13 | 0 | 166 |
| `EntropyMapTests.cs` | 13 | 0 | 172 |
| `HardwareSnapshotServiceTests.cs` | 13 | 0 | 301 |
| `PeInspectTests.cs` | 13 | 0 | 274 |
| `ResizableBarDecoderTests.cs` | 12 | 1 | 184 |
| `ZhTermsTests.cs` | 11 | 2 | 138 |
| `DeepBenchViewModelTests.cs` | 12 | 0 | 389 |
| `DimmEccJudgeTests.cs` | 10 | 2 | 124 |
| `DisplayLinkDecoderTests.cs` | 12 | 0 | 119 |
| `DpcAggregatorTests.cs` | 11 | 1 | 152 |
| `EdidParsingTests.cs` | 12 | 0 | 146 |
| `EvidenceLabIntegrationTests.cs` | 12 | 0 | 269 |
| `FactKeyCatalogTests.cs` | 12 | 0 | 205 |
| `IscsiSessionFactsTests.cs` | 12 | 0 | 138 |
| `JitterFactsTests.cs` | 12 | 0 | 123 |
| `NtfsVolumeFactsTests.cs` | 12 | 0 | 219 |
| `NvmePowerDecoderTests.cs` | 12 | 0 | 198 |
| `PciResourceAuditServiceTests.cs` | 11 | 1 | 197 |
| `ProductAndPlatformFactsTests.cs` | 12 | 0 | 118 |
| `SmbiosDeepFactsTests.cs` | 12 | 0 | 218 |
| `UncorePmuServiceTests.cs` | 11 | 1 | 125 |
| `AppCompatCacheDecoderTests.cs` | 9 | 2 | 215 |
| `AssetJudgeTests.cs` | 9 | 2 | 152 |
| `ClosingFactsTests.cs` | 11 | 0 | 157 |
| `DashboardLayoutTests.cs` | 10 | 1 | 158 |
| `DeepAccessTests.cs` | 11 | 0 | 263 |
| `DiagViewModelTests.cs` | 11 | 0 | 185 |
| `DriverMsrReaderTests.cs` | 11 | 0 | 217 |
| `FactCoverageReportTests.cs` | 11 | 0 | 164 |
| `HyperVInventoryFactsTests.cs` | 11 | 0 | 125 |
| `KnowledgeCoverageTests.cs` | 11 | 0 | 148 |
| `LatencyCurveTests.cs` | 11 | 0 | 121 |
| `LicenseDecoderTests.cs` | 11 | 0 | 103 |
| `MachineAgeDecoderTests.cs` | 11 | 0 | 130 |
| `MemoryChannelJudgeTests.cs` | 11 | 0 | 160 |
| `RoleSurfaceJudgeTests.cs` | 9 | 2 | 173 |
| `SmtContentionServiceTests.cs` | 11 | 0 | 247 |
| `SpdDetailTests.cs` | 10 | 1 | 207 |
| `StorageReliabilityFactsTests.cs` | 11 | 0 | 268 |
| `TpmReplayTests.cs` | 11 | 0 | 218 |
| `UserExperience2FactsTests.cs` | 11 | 0 | 112 |
| `VisualizationDeepFactsTests.cs` | 11 | 0 | 133 |
| `WingetCatalogTests.cs` | 10 | 1 | 144 |
| `AudioSpectrumServiceTests.cs` | 10 | 0 | 201 |
| `DeviceDiagnosticServiceTests.cs` | 8 | 2 | 185 |
| `DiskLatencyTests.cs` | 10 | 0 | 126 |
| `HealthAndBugCheckTests.cs` | 4 | 6 | 103 |
| `InvisibleStallDecoderTests.cs` | 10 | 0 | 106 |
| `MemoryNumaTlbLargePageServiceTests.cs` | 9 | 1 | 308 |
| `MetricHistoryTests.cs` | 9 | 1 | 136 |
| `ScenarioGuidanceFactsTests.cs` | 10 | 0 | 138 |
| `ShrineTests.cs` | 7 | 3 | 162 |
| `SmbiosFactsTests.cs` | 8 | 2 | 167 |
| `SpdDisplayTests.cs` | 10 | 0 | 157 |
| `StorageGpuPipelineServiceTests.cs` | 10 | 0 | 208 |
| `SuperIoHwmTests.cs` | 10 | 0 | 299 |
| `SyntheticFixtureTests.cs` | 10 | 0 | 198 |
| `TopDownMathTests.cs` | 9 | 1 | 123 |
| `TopologyHybridPlacementServiceTests.cs` | 10 | 0 | 328 |
| `TrendSentinelServiceTests.cs` | 10 | 0 | 248 |
| `AcpiTableTests.cs` | 9 | 0 | 142 |
| `BootBreakdownTests.cs` | 9 | 0 | 105 |
| `CoherenceLockServiceTests.cs` | 9 | 0 | 209 |
| `CoreLatencyTests.cs` | 9 | 0 | 84 |
| `CppcCapabilityFactsTests.cs` | 9 | 0 | 94 |
| `DeepBenchPcieTransferTests.cs` | 8 | 1 | 179 |
| `DeepBenchVramBandwidthTests.cs` | 8 | 1 | 180 |
| `DeviceIconsTableTests.cs` | 3 | 6 | 209 |
| `DramMappingInferenceServiceTests.cs` | 9 | 0 | 216 |
| `DramTrafficDecoderTests.cs` | 9 | 0 | 89 |
| `EventAnnotationAndDiskUsageTests.cs` | 9 | 0 | 135 |
| `ForensicReadyFactsTests.cs` | 9 | 0 | 118 |
| `FrameTimeAndSlcTests.cs` | 9 | 0 | 108 |
| `FuzzyMatchTests.cs` | 9 | 0 | 66 |
| `GpuPcieDecoderTests.cs` | 9 | 0 | 95 |
| `InterruptAffinityTests.cs` | 9 | 0 | 141 |
| `KernelDumpTests.cs` | 8 | 1 | 151 |
| `LifecycleSecurityFactsTests.cs` | 9 | 0 | 91 |
| `McaAndSecurityTests.cs` | 9 | 0 | 89 |
| `PlatformSecurityTests.cs` | 9 | 0 | 115 |
| `SetupApiLogTests.cs` | 9 | 0 | 142 |
| `SharedAiAndFeedbackTests.cs` | 7 | 2 | 139 |
| `SmbiosTests.cs` | 9 | 0 | 157 |
| `SpdConsistencyAuditServiceTests.cs` | 9 | 0 | 190 |
| `VisualizationFactsTests.cs` | 9 | 0 | 97 |
| `AiUpgradeTests.cs` | 7 | 1 | 206 |
| `CapabilityMatrixTests.cs` | 8 | 0 | 120 |
| `CliServiceTests.cs` | 8 | 0 | 179 |
| `CveOfflineFactsTests.cs` | 8 | 0 | 139 |
| `DriverMmioReaderTests.cs` | 8 | 0 | 155 |
| `EspScanTests.cs` | 8 | 0 | 170 |
| `EtwFilterViewFactsTests.cs` | 8 | 0 | 114 |
| `FiveElementsTests.cs` | 8 | 0 | 138 |
| `GovernanceKnowledgeFactsTests.cs` | 8 | 0 | 81 |
| `GpuCodecThroughputServiceTests.cs` | 8 | 0 | 163 |
| `GpuRasterTextureServiceTests.cs` | 8 | 0 | 200 |
| `NativeToolSourceTests.cs` | 8 | 0 | 138 |
| `NoticesAndErrorAggregationTests.cs` | 8 | 0 | 131 |
| `NvmeIdleVerdictTests.cs` | 8 | 0 | 116 |
| `PcieErrorFlagTests.cs` | 8 | 0 | 88 |
| `QueryTests.cs` | 8 | 0 | 152 |
| `RawRegisterCollectTests.cs` | 8 | 0 | 202 |
| `RestSampleNotDoingTests.cs` | 8 | 0 | 91 |
| `SpdDecoder5Tests.cs` | 8 | 0 | 165 |
| `SpiEntropyServiceTests.cs` | 8 | 0 | 188 |
| `VerifyFactsCollectorTests.cs` | 7 | 1 | 88 |
| `VerifyRuleTableTests.cs` | 7 | 1 | 93 |
| `AccentInkTests.cs` | 7 | 0 | 99 |
| `AtaIdentifyTests.cs` | 5 | 2 | 101 |
| `AudioBufferGlitchServiceTests.cs` | 7 | 0 | 157 |
| `BackendEnvironmentTests.cs` | 7 | 0 | 142 |
| `BoostRecoveryServiceTests.cs` | 7 | 0 | 204 |
| `BranchSpeculationServiceTests.cs` | 7 | 0 | 172 |
| `ConfidenceEngineServiceTests.cs` | 7 | 0 | 126 |
| `CpuFirmwareFactsTests.cs` | 7 | 0 | 146 |
| `DeepBenchDiskIoTests.cs` | 7 | 0 | 168 |
| `DeepBenchDispatchJitterTests.cs` | 6 | 1 | 128 |
| `DeepBenchGpuComputeTests.cs` | 6 | 1 | 127 |
| `DeepBenchMeasurementTests.cs` | 6 | 1 | 120 |
| `DeepBenchReviewFixTests.cs` | 7 | 0 | 255 |
| `KnowledgePackTests.cs` | 7 | 0 | 133 |
| `LargePageDecoderTests.cs` | 7 | 0 | 97 |
| `MachineVerdictTests.cs` | 7 | 0 | 107 |
| `MemoryTestRowTests.cs` | 7 | 0 | 90 |
| `NetAdapterRssTests.cs` | 7 | 0 | 69 |
| `PerformanceBudgetTests.cs` | 7 | 0 | 234 |
| `PmbusAndUpsTests.cs` | 7 | 0 | 109 |
| `PowerStateLatencyServiceTests.cs` | 7 | 0 | 212 |
| `RowhammerProbeTests.cs` | 7 | 0 | 89 |
| `SecurityScoreTests.cs` | 7 | 0 | 245 |
| `StorageIocpEngineServiceTests.cs` | 5 | 2 | 164 |
| `StorageProbeBreakerTests.cs` | 7 | 0 | 169 |
| `TimeBombTests.cs` | 6 | 1 | 131 |
| `UxSyntheticWorkloadServiceTests.cs` | 7 | 0 | 167 |
| `AuditLogTests.cs` | 6 | 0 | 104 |
| `BlueSquadronSmokeTests.cs` | 6 | 0 | 86 |
| `BoardEngineTests.cs` | 4 | 2 | 84 |
| `CmosTests.cs` | 6 | 0 | 124 |
| `DeepBenchIntegrationTests.cs` | 6 | 0 | 218 |
| `DeepBenchOrchestratorTests.cs` | 6 | 0 | 126 |
| `EnglishTreeTests.cs` | 6 | 0 | 184 |
| `EvidenceRowTests.cs` | 5 | 1 | 118 |
| `ExternalReportServiceTests.cs` | 6 | 0 | 91 |
| `GauntletMultiDomainServiceTests.cs` | 6 | 0 | 145 |
| `IpmiDecoderTests.cs` | 6 | 0 | 137 |
| `LanguageRoundTripTests.cs` | 6 | 0 | 172 |
| `ListenPortFactsTests.cs` | 6 | 0 | 102 |
| `MemoryTruthMathTests.cs` | 6 | 0 | 57 |
| `MountedDevicesFactsTests.cs` | 6 | 0 | 95 |
| `NumaTopologyTests.cs` | 6 | 0 | 116 |
| `PresentFramePacingServiceTests.cs` | 6 | 0 | 196 |
| `RawRegisterDiffTests.cs` | 6 | 0 | 73 |
| `RuleEngineTests.cs` | 5 | 1 | 289 |
| `ShimcacheFactsTests.cs` | 6 | 0 | 121 |
| `SimpleModeTests.cs` | 6 | 0 | 94 |
| `SpdSurveyorTests.cs` | 6 | 0 | 191 |
| `SpecSummaryTests.cs` | 6 | 0 | 87 |
| `SpiFlashCompareTests.cs` | 6 | 0 | 166 |
| `SpiFlashHashTests.cs` | 6 | 0 | 163 |
| `ThroughputDegradationServiceTests.cs` | 6 | 0 | 170 |
| `TopologyCoreBandwidthServiceTests.cs` | 6 | 0 | 156 |
| `TroubleshootServiceTests.cs` | 5 | 1 | 66 |
| `TsodTests.cs` | 4 | 2 | 111 |
| `UiSmokeTests.cs` | 6 | 0 | 551 |
| `UncorePmuFactsTests.cs` | 6 | 0 | 128 |
| `UnitConverterTests.cs` | 3 | 3 | 64 |
| `VizPaletteTests.cs` | 5 | 1 | 86 |
| `WifiTpmTbHdrTests.cs` | 6 | 0 | 65 |
| `WriteGateTests.cs` | 6 | 0 | 131 |
| `YCruncherParserTests.cs` | 6 | 0 | 40 |
| `ByovdCompareTests.cs` | 5 | 0 | 125 |
| `DeepBenchCatalogTests.cs` | 5 | 0 | 169 |
| `DeepBenchCryptoTests.cs` | 5 | 0 | 72 |
| `DiskIoTests.cs` | 5 | 0 | 58 |
| `EtlReadbackTests.cs` | 5 | 0 | 84 |
| `GpuCodecServiceTests.cs` | 5 | 0 | 58 |
| `HardeningAdvisorTests.cs` | 5 | 0 | 83 |
| `HardwareRandomBenchAdapterTests.cs` | 5 | 0 | 149 |
| `HelpCatalogTests.cs` | 5 | 0 | 111 |
| `KeyRangeGuardTests.cs` | 5 | 0 | 81 |
| `MchbarTests.cs` | 5 | 0 | 99 |
| `MediaVerificationTests.cs` | 5 | 0 | 77 |
| `MemoryEccWheaStressAdapterTests.cs` | 5 | 0 | 134 |
| `NicHealthTests.cs` | 4 | 1 | 83 |
| `NvmeHealthTests.cs` | 4 | 1 | 103 |
| `PciKnowledgeTests.cs` | 5 | 0 | 67 |
| `PeripheralBusTests.cs` | 4 | 1 | 80 |
| `PlainTextCatalogTests.cs` | 5 | 0 | 134 |
| `PmuProgrammingTests.cs` | 5 | 0 | 97 |
| `PrefetchFactsTests.cs` | 5 | 0 | 113 |
| `PrefetchHeaderDecoderTests.cs` | 3 | 2 | 80 |
| `SecurityAuditTests.cs` | 5 | 0 | 91 |
| `SignatureVerdictCacheTests.cs` | 5 | 0 | 138 |
| `SlcSustainedWriteAdapterTests.cs` | 5 | 0 | 140 |
| `SmbusBusLockTests.cs` | 5 | 0 | 102 |
| `StorageFlushDurabilityTests.cs` | 5 | 0 | 146 |
| `StorageWriteIntegrityTests.cs` | 5 | 0 | 134 |
| `SuperIoProbeTests.cs` | 3 | 2 | 117 |
| `TopologyDeepenTests.cs` | 5 | 0 | 122 |
| `Tpm2Tests.cs` | 5 | 0 | 107 |
| `UpdateCheckServiceTests.cs` | 3 | 2 | 65 |
| `WinRing0MmioReaderTests.cs` | 5 | 0 | 100 |
| `WpbtFactsTests.cs` | 5 | 0 | 62 |
| `AmdSecurityFactsTests.cs` | 4 | 0 | 105 |
| `AudioEndpointFactsTests.cs` | 3 | 1 | 83 |
| `AuditPolicyTests.cs` | 4 | 0 | 73 |
| `ChassisAndHpaTests.cs` | 4 | 0 | 92 |
| `CliIntegrityAndSbomTests.cs` | 4 | 0 | 99 |
| `CliVerifyAuditTests.cs` | 4 | 0 | 109 |
| `CpuMicroarchBenchTests.cs` | 4 | 0 | 89 |
| `FitDecoderTests.cs` | 4 | 0 | 71 |
| `HeatScaleRenderSnapshotTests.cs` | 4 | 0 | 178 |
| `HtmlReportTests.cs` | 4 | 0 | 83 |
| `MegaRaidDecoderTests.cs` | 4 | 0 | 74 |
| `MemoryEncryptionTests.cs` | 4 | 0 | 96 |
| `NetworkStackLatencyAdapterTests.cs` | 4 | 0 | 90 |
| `NpuDetectionTests.cs` | 3 | 1 | 50 |
| `NvmeOverlappedIoTests.cs` | 4 | 0 | 106 |
| `OobFactsTests.cs` | 4 | 0 | 72 |
| `PciBarsTests.cs` | 4 | 0 | 76 |
| `PcieAerTests.cs` | 4 | 0 | 57 |
| `PeripheralAndSurfaceTests.cs` | 4 | 0 | 44 |
| `ProbeWiringGuardTests.cs` | 4 | 0 | 100 |
| `RawRegisterSnapshotStoreTests.cs` | 4 | 0 | 87 |
| `RawSnapshotUiTests.cs` | 4 | 0 | 146 |
| `ServiceOrphanGateTests.cs` | 4 | 0 | 165 |
| `SmartThresholdTests.cs` | 4 | 0 | 101 |
| `ThemeSwitchTests.cs` | 4 | 0 | 250 |
| `UefiBootFactsTests.cs` | 4 | 0 | 85 |
| `VerifyModelsTests.cs` | 4 | 0 | 59 |
| `WifiBssDecoderTests.cs` | 2 | 2 | 87 |
| `AmdAdlFactsTests.cs` | 3 | 0 | 58 |
| `BufferbloatTests.cs` | 2 | 1 | 39 |
| `Bus0InventoryTests.cs` | 3 | 0 | 126 |
| `CameraAndEnterpriseStorageTests.cs` | 3 | 0 | 67 |
| `CorpusUploadTests.cs` | 3 | 0 | 77 |
| `CxlFactsTests.cs` | 3 | 0 | 75 |
| `DebugConfigTests.cs` | 3 | 0 | 51 |
| `DocumentationIntegrityTests.cs` | 3 | 0 | 92 |
| `EmbeddedLhm094Tests.cs` | 3 | 0 | 103 |
| `EnvironmentGuardTests.cs` | 3 | 0 | 144 |
| `EventLogSummaryTests.cs` | 3 | 0 | 72 |
| `EvidenceReloadTests.cs` | 3 | 0 | 109 |
| `FactKeyRuntimeReconcileTests.cs` | 3 | 0 | 126 |
| `ForensicBanGateTests.cs` | 3 | 0 | 102 |
| `IoPortAccessTests.cs` | 3 | 0 | 68 |
| `ItemAutomationNameTests.cs` | 3 | 0 | 47 |
| `KernelModuleTests.cs` | 3 | 0 | 72 |
| `LoopbackSignalTests.cs` | 3 | 0 | 45 |
| `MachineVerdictServiceTests.cs` | 3 | 0 | 64 |
| `MemoryPoolServiceTests.cs` | 3 | 0 | 36 |
| `NativeTimingsTests.cs` | 3 | 0 | 64 |
| `NetOffloadFactsTests.cs` | 3 | 0 | 73 |
| `PmuCapabilityTests.cs` | 3 | 0 | 74 |
| `PrivacyDenylistTests.cs` | 3 | 0 | 77 |
| `PublicSpecTests.cs` | 3 | 0 | 105 |
| `ReleaseIntegrityTests.cs` | 3 | 0 | 72 |
| `ReleasePipelineTests.cs` | 3 | 0 | 86 |
| `ServiceInventoryTests.cs` | 3 | 0 | 65 |
| `SmbiosDifferentialTests.cs` | 3 | 0 | 69 |
| `SpecRefCoverageTests.cs` | 3 | 0 | 41 |
| `SpiFlashMapTests.cs` | 3 | 0 | 63 |
| `SuperIoKnowledgeTests.cs` | 2 | 1 | 79 |
| `TimeSyncFactsTests.cs` | 3 | 0 | 95 |
| `TpmFactsTests.cs` | 3 | 0 | 97 |
| `WheaTests.cs` | 3 | 0 | 46 |
| `WindowsThroughputDegradationEngineTests.cs` | 3 | 0 | 48 |
| `WindowsUpdateHistoryTests.cs` | 3 | 0 | 80 |
| `XamlResourceKeyTests.cs` | 3 | 0 | 167 |
| `XsRegProbeContractTests.cs` | 3 | 0 | 102 |
| `AssetChangeDetectorTests.cs` | 2 | 0 | 75 |
| `BootTimingTests.cs` | 2 | 0 | 44 |
| `DataSovereigntyTests.cs` | 2 | 0 | 65 |
| `DeepBenchHonestyTests.cs` | 2 | 0 | 43 |
| `GpuRowTests.cs` | 2 | 0 | 37 |
| `HardcodedColorTests.cs` | 2 | 0 | 120 |
| `LanguageServiceTests.cs` | 2 | 0 | 27 |
| `LanguageTreeTests.cs` | 2 | 0 | 100 |
| `LevelZeroFactsTests.cs` | 2 | 0 | 49 |
| `LocalApiTests.cs` | 2 | 0 | 71 |
| `MeasurementQuietTests.cs` | 2 | 0 | 107 |
| `OptionalFeatureTests.cs` | 2 | 0 | 50 |
| `PowerShellModuleTests.cs` | 2 | 0 | 43 |
| `ReconcileVerdictCardTests.cs` | 2 | 0 | 89 |
| `RunTextBindingTests.cs` | 2 | 0 | 86 |
| `SettingsPersistenceTests.cs` | 2 | 0 | 88 |
| `WinRing0BridgeTests.cs` | 2 | 0 | 55 |
| `WiringGuardTests.cs` | 2 | 0 | 137 |
| `AppInfoTests.cs` | 0 | 1 | 18 |
| `LatencyCurveRenderTests.cs` | 1 | 0 | 150 |
| `SiliconCardRenderTests.cs` | 1 | 0 | 109 |
| `WarningBaselineTests.cs` | 1 | 0 | 86 |
| `WindowsBoostRecoveryEngineTests.cs` | 1 | 0 | 42 |
| `WindowsPowerStateLatencyEngineTests.cs` | 1 | 0 | 36 |
| `CrashFolderCollection.cs` | 0 | 0 | 19 |
| `FakeImcPci.cs` | 0 | 0 | 123 |
| `FakeSmbusIo.cs` | 0 | 0 | 145 |
| `PropertyTests.cs` | 0 | 0 | 91 |
| `SyntheticFixtures.cs` | 0 | 0 | 170 |
| `TestSuiteBaseline.cs` | 0 | 0 | 32 |
| `WpfCollection.cs` | 0 | 0 | 27 |
| `WpfEnv.cs` | 0 | 0 | 30 |

# Appendix G — Document index

**51** documents under `docs/`. Titles are given as they appear in the repository.

| Document | Title | Bytes |
|---|---|---:|
| `docs/AGENT_CONTEXT.md` | XinSpect AI 開發上下文（快速上手筆記） | 1579 |
| `docs/CAPABILITY-ADDITIONS-2026-10-09.md` | 還能加什麼——校準版目錄的增量判定（2026-10-09） | 4519 |
| `docs/CAPABILITY-DOMAINS-2026-10-09.md` | 12 能力域整合判定（2026-10-09） | 13850 |
| `docs/DATA-SOVEREIGNTY.md` | WP39 資料主權聲明 | 1315 |
| `docs/DEPLOYMENT.md` | WP37 部署與企業維運 | 1361 |
| `docs/EC-RISK-ASSESSMENT.md` | WP31：EC（Embedded Controller）唯讀存取風險評估記錄 | 2329 |
| `docs/INDUSTRIAL-CATALOG-2026-10-10.md` | XinSpect 功能目錄・工業級規格 v1.0（2026-10-10） | 36572 |
| `docs/INDUSTRIAL-CATALOG-VOL2-2026-10-11.md` | XinSpect 功能目錄・工業級規格【第二組】（2026-10-11） | 24289 |
| `docs/INTEGRATE-VS-DISPATCH-2026-10-09.md` | 整合優先、調度最後：對照清單的重寫（2026-10-09） | 10960 |
| `docs/ITERATIONS.md` | XinSpect 迭代帳本 | 33638 |
| `docs/MEASUREMENT-METHODOLOGY.md` | WP33 量測方法學（Deep Bench／訊號／延遲量測的通用規則） | 1088 |
| `docs/NEXT-STEPS-2026-10-08.md` | 曦覽 XinSpect — 現況與下一步 | 3850 |
| `docs/PMU-SANDBOX-PLAN.md` | WP27 PMU 沙箱驗證方案（骨架） | 780 |
| `docs/PROGRAM-ULTIMATE-2026-10-10.md` | XinSpect 極致化總綱 — 2026-10-10 | 41729 |
| `docs/PROGRESS-PLAN-2026-10-11.md` | XinSpect 600 項推進計畫（2026-10-11） | 55126 |
| `docs/ROADMAP-G6.md` | G6 全線路線圖（V7 廣度線＋平台線，2026-10-03 立） | 1489 |
| `docs/ROUND-PERF-DISK-BREAKER-2026-10-13.md` | 本輪：證據「更新」速度——儲存協定查詢斷路器（2026-10-13） | 6301 |
| `docs/ROUND-PERF-SIGCACHE-2026-10-12.md` | 本輪：證據「更新」速度——Authenticode 指紋快取（2026-10-12） | 2001 |
| `docs/ROUND-VOL2-ACD-2026-10-11.md` | Vol2 三批次（A／C／D）一次做完 — 2026-10-11 | 10520 |
| `docs/ROUND-VOL2-B-FS-2026-10-11.md` | Vol2 批次 B（FS 檔案鑑識主線）第一～三波 — 2026-10-11 v2.57／v2.58／v2.59 | 3840 |
| `docs/ROUND-VOL2-SV-2026-10-12.md` | Vol2 批次 D（G4 伺服器角色）第一波 — 2026-10-12（並行開發：本輪未動版號） | 3395 |
| `docs/ROUND-WIRING-GUARD-2026-10-10.md` | 接線守門那一輪 — 2026-10-10 | 6542 |
| `docs/SERVER-WORKSTATION-EXPANSION-2026-10-08.md` | 曦覽 XinSpect — 伺服器與工作站功能擴展總表 | 8457 |
| `docs/TOOL-CAPABILITY-GAP-2026-10-09.md` | 工具清單對照：XinSpect 能加什麼、學什麼、併入什麼 | 5785 |
| `docs/TOOL-CATALOG-2026-10-09.md` | Windows 底層診斷 / 調試 / 取證工具整合校準版 | 13708 |
| `docs/UNIFIED-PIPELINE-2026-10-09.md` | 統一管線重寫：整合為正，調度為最後手段 | 4123 |
| `docs/archive-2026-10/CODE-REVIEW-2026-09-29.md` | XinSpect 全碼審查報告（2026-09-29） | 8392 |
| `docs/archive-2026-10/HANDOFF-2026-09-29.md` | XinSpect 碼審肅清 HANDOFF — 2026-09-29 | 4074 |
| `docs/archive-2026-10/HANDOFF-2026-09-30.md` | XinSpect 碼審肅清 HANDOFF — 2026-09-30（接續 09-29） | 4207 |
| `docs/archive-2026-10/HANDOFF-2026-10-01.md` | XinSpect HANDOFF — 2026-10-01 | 2279 |
| `docs/archive-2026-10/HANDOFF-2026-10-10.md` | XinSpect HANDOFF — 2026-10-10 | 5700 |
| `docs/archive-2026-10/HANDOFF-DEEP-REGISTER-2026-10-02.md` | XinSpect 深層暫存器計畫 — 移交 Z Code（2026-10-02） | 10672 |
| `docs/archive-2026-10/HANDOFF-ZCODE-ITER20-2026-10-02.md` | XinSpect 深層暫存器・20 輪迭代後交接（Z Code → 下一個 AI，2026-10-02） | 11380 |
| `docs/archive-2026-10/MASTER-INDEX-2026-10-02.md` | 曦覽 XinSpect ＋ My iLove PDF ・ 總索引 | 10002 |
| `docs/archive-2026-10/PROGRAM-2026-09-29.md` | XinSpect 完整體計畫 — PROGRAM (2026-09-29) | 2541 |
| `docs/archive-2026-10/TASK-GAP-6-2026-10-03.md` | TASK-未落地六項・2026-10-03 | 6987 |
| `docs/feature-backlog.md` | XinSpect 功能 backlog(2026-09-25) | 5826 |
| `docs/gap-blueprint-2026-10-08.md` | 曦覽功能擴展藍圖 × 實際程式碼 落差分析 | 13430 |
| `docs/research-supplement-2026-10-08.md` | 曦覽調研補充（2026-10-08） | 4032 |
| `docs/spec/CERTIFICATION-PLAN.md` | XinSpect 認證計畫（骨架） | 717 |
| `docs/spec/LIMITATIONS.md` | XinSpect 公開限制（Limitations） | 1168 |
| `docs/spec/METHODOLOGY.md` | XinSpect 量測方法學（公開版） | 1897 |
| `docs/spec/corpus-upload.v1.md` | XinSpect corpus 貢獻包格式 v1（骨架） | 1009 |
| `docs/spec/query-language.v1.md` | XinSpect 事實查詢語言 v1 | 1319 |
| `docs/superpowers/plans/2026-09-04-used-hardware-verification-batch1.md` | 二手驗機・矛盾稽核（第一批）實作計畫 | 50745 |
| `docs/superpowers/plans/2026-10-01-xinspect-deep-bench-20-phase-1.md` | XinSpect Deep Bench 20 Phase 1 Implementation Plan | 18669 |
| `docs/superpowers/plans/2026-10-01-xinspect-deep-bench-20-phase-2.md` | XinSpect Deep Bench 20 — Phase 2 執行計畫 | 1621 |
| `docs/superpowers/plans/deepbench-phase1-workspace/ledger.md` | Deep Bench Phase 1 inline ledger | 1683 |
| `docs/superpowers/specs/2026-09-04-used-hardware-verification-design.md` | 二手驗機・矛盾稽核 ・ 設計（2026-09-04） | 9482 |
| `docs/superpowers/specs/2026-10-01-xinspect-deep-bench-20-design.md` | XinSpect Deep Bench 20 設計規格 | 10828 |
| `docs/xinspect-master-plan-2026-10-08.md` | 曦覽 XinSpect 擴展總藍圖（2026-10-08） | 9962 |

# Appendix H — Glossary

Definitions here follow this project's usage, which in places differs from general industry usage.

### Core model

| Term | Definition |
|---|---|
| Fact | One observation, represented as a `HardwareFact` instance. A fact is not "the truth" but "what some source said about something at some time". |
| Fact key | The unique identifier of a fact, dotted. Keys are a property of the *program*: which exist depends on which services exist. |
| Static key | A key fixed at compile time in `FactKeyCatalog` — 844 in this build. |
| Dynamic family | A key family whose membership the machine determines, registered by prefix in `FactKeyDynamicCatalog` — 23 in this build. |
| Fact-key catalogue | The full static key set in `FactKeyCatalog`, compared key by key against the source by a test. |
| Prefix | The first dotted segment of a key, denoting a domain. 107 in this build. |

### Trust and availability

| Term | Definition |
|---|---|
| Trust level | `Measured` (directly measured here), `Reported` (self-reported by the source), `Derived` (inferred from other facts), `Unknown`. Not a quality grade but a statement about inference-chain length. |
| Availability | `Present`, `NotSupported`, `InsufficientPrivilege`, `ReadError`, `NotApplicable`. |
| Three-state honesty | The core principle: an unreadable value must state why, and must never be filled with zero or a typical value. |
| Reduced reading trust | The semantics of rule `R-CPU-04`: with a virtualisation layer detected, hardware-direct facts are supplied by it, so their trust is reduced. This is a methodological warning, not a hardware anomaly. |

### Reconciliation

| Term | Definition |
|---|---|
| Reconciliation | Comparing what several sources say about the same thing. |
| Rule | One unit of reconciliation, a `VerifyRule`. 18 in this build. |
| Verdict | The output of reconciliation: exactly match, conflict or undecidable. |
| Required facts | A rule's declared inputs; when any is missing the engine returns undecidable itself, so rule implementations never handle missing values. |
| Scope | Whether a rule runs machine-wide or per disk (`VerifyScope.Machine` / `VerifyScope.Disk`). |
| Evidence list | The raw fact values attached to a conflict verdict, making the verdict re-checkable. |

### Coverage

| Term | Definition |
|---|---|
| Coverage report | The per-key statement of whether any rule considers it. |
| Covered | Listed as an input by at least one rule. |
| Explicit exemption | Deliberately not reconciled, with a reason. 562 in this build. |
| Uncovered | Neither considered by a rule nor exempt. Not an error, but it must be a known number. |
| Coverage target | Deliberately not set: forcing full coverage manufactures padding rules whose "match" verdicts are false signals. |

### Sources and acquisition

| Term | Definition |
|---|---|
| Hardware layer | Values read by bypassing the OS (MSR, PCI configuration space, SMBus, SMART, NVMe, Super I/O, CMOS). Trust level `Measured`. |
| Firmware layer | Descriptions produced by platform firmware at build time (ACPI tables, SMBIOS, UEFI variables). Trust level `Reported`. |
| Operating system layer | The OS and driver view of the hardware (WMI, registry, event logs, performance counters, ETW, X509). Trust level `Reported`. |
| XsRegProbe | The project's whitelist read-only kernel-mode driver: it handles only register accesses declared in the list. |
| Fact-reading layer | The typed reading interface above the driver, split into MMIO and I/O port paths. |
| SMBus | The shared serial bus carrying memory modules' SPD and TSOD. Access requires mutual exclusion. |
| MSR | Model-specific register; per-core reads yield microcode revision, frequency and power state. |
| Circuit breaker | A mechanism that stops retrying a source within the current run once repeated failure or timeout is detected. |
| Degradation | The handling where a failed source's facts are presented with a read-failure availability while other facts proceed. |

### Measurement

| Term | Definition |
|---|---|
| Self-validating measurement | Verifying that the measurement mechanism itself works before reporting a value. |
| perft | A chess node count, used here as the mathematical checksum for the chess benchmark. |
| Error margin | The uncertainty estimate of a measurement, used to separate signal from noise. |
| Condition record | The environmental conditions captured with a measurement (frequency, timings, power policy, background load) — the precondition for cross-session comparison. |
| Run session | One execution unit in the deep-test centre. |
| Invisible stall | A system stall that cannot be attributed to one process or component, and which conventional metrics (CPU utilisation, disk queue) do not show. |

### Output and interface

| Term | Definition |
|---|---|
| Snapshot | The result of one complete fact collection, with a measurement time. |
| Time capsule | The serialisation container for a snapshot, specified in `docs/spec/snapshot.schema.v1.json`. |
| Canonical | A property of the serialisation: identical facts produce byte-identical output. |
| Query language | A small language for asking structured questions of a snapshot, specified in `docs/spec/query-language.v1.md`; seven fields, three operators, clauses joined by AND. |
| Hardware change timeline | The comparison of two snapshots; itself a form of reconciliation (in time). |
| Audit log | A hash-chained log of operational events, verifiable independently of the application. |
| Decision registry | The practice of registering a human-judgement fork as data (decision, reason, conditions) so reasons do not evaporate with staffing. |
| Write gate | The module through which all write operations pass. |

### Engineering

| Term | Definition |
|---|---|
| Guard test | A test verifying an invariant rather than a feature, used to prevent a class of defect from recurring. |
| Dead probe wiring | A probe that collects and is wired up but whose result nothing consumes. It fails no test, so it must be checked mechanically. |
| Zombie prefix | A registered dynamic-family prefix with no producer in the source; judged by a guard test. |
| Mutation testing | Making small semantic changes to source and checking that tests fail; used to find ineffective tests. |
| Golden vector | A byte-level test input computed by hand from the specification, verifying understanding of the specification rather than of the implementation. |
| Mutation score | The quantitative mutation-testing metric. The target for this build is 82%. |
| Reproducible baseline | A figure produced by scanning the source, re-verifiable at any time, rather than counted by hand. |

---
