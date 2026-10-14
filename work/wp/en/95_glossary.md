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
