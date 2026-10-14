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
