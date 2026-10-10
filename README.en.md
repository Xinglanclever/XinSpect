[繁體中文](README.md) · [简体中文](README.zh-CN.md) · English

# XinSpect

A free, open-source hardware verification, monitoring, and security auditing tool for Windows. Ships as a single portable executable. Read-only by default; write operations are gated behind explicit user consent. Does not collect or transmit any user data.

![Version](https://img.shields.io/badge/version-2.56-4C8DFF)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0A7EA4)
![Framework](https://img.shields.io/badge/.NET-10.0--windows%20(WPF)-512BD4)
![Tests](https://img.shields.io/badge/tests-3781%20passed-3FB950)
![Mutation Score](https://img.shields.io/badge/Stryker-82%25-8B5CF6)
![License](https://img.shields.io/badge/license-MIT-green)

XinSpect is built with WPF (.NET 10) using MVVM architecture. It integrates LibreHardwareMonitor for sensing, Intel XTU bridging for overclocking, NVIDIA NVML/NVAPI for GPU control, WebView2 for the embedded browser, and a custom WinRing0-based fact-reading layer with an XsRegProbe whitelist read-only driver. The "Spect" in the name means observer: it doesn't draw conclusions about your hardware — it presents what the hardware says, what the firmware says, and what the OS says side by side, marking anything it can't read, letting the verifier draw their own conclusions.

**[Download v2.55 Olympus](https://github.com/Xinglanclever/XinSpect/releases/tag/v2.56)** — single portable executable with BlueSquadron guard built in. 

## Design Principles

These principles are enforced by unit tests and machine checks, not just documentation:

- **Three-state reporting** — every fact carries an `availability` field: `Present`, `NotSupported`, `InsufficientPrivilege`, `ReadError`, `NotApplicable`. When data can't be read, the reason is shown — never filled with 0, typical values, or stale data.
- **Auditable sources** — every fact records its origin (register address, WMI class, API). Register decoders must carry `SpecRef` annotations, verified by reflection-based machine checks.
- **Self-validating measurements** — chess benchmarks use perft as a mathematical checksum; bandwidth conversions self-verify with known-size workloads before reporting; PMU programming verifies write-readback consistency each round.
- **Mutation testing** — pure decoders are in a separate class library with Stryker mutation score 82%, which caught a real bug (extended family carry was documented but not implemented).
- **Not-found ≠ not-present** — the query language matches items that exist but can't be read, returning the reason; syntax errors return 400 with guidance, never empty results.

## Download

| File | Size | Purpose |
|---|---|---|
| [XinSpect.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v2.56/XinSpect.exe) | 30.4 MB | Main program (includes BlueSquadron guard) |
| [BlueSquadronBridge.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v2.56/BlueSquadronBridge.exe) | 6.5 MB | Standalone guard process |
| [XinSpectDeploy.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v2.56/XinSpectDeploy.exe) | 7 KB | One-click installer (downloads and deploys automatically) |

## Key Features

**Hardware Verification** — SMART threshold failing-now detection, chassis intrusion (SMBIOS Type 3), HPA hidden capacity, fake-capacity write testing, NVMe WCTEMP warnings, microcode cross-verification (MSR vs registry), memory module SPD direct-read, PCIe link negotiation vs capability, USB link speed vs capability.

**Security Auditing** — BYOVD driver comparison against Microsoft's recommended block list, Defender exclusion list audit, event log clearing detection (1102), non-Microsoft root certificate audit, USBSTOR usage traces, driver signature audit, BlueSquadron six-defense-line real-time posture assessment with ETW threat timeline.

**Processor Deep Dive** — TME/SGX memory encryption status, C-state residency, PMU capability exploration and programming verification, die topology (CPUID 0x1F), TjMax, frequency truth (MPERF/APERF), performance ceiling attribution (thermal/power/current/BLAS walls).

**Mainboard** — SPI flash audit (FLOCKDN/FREG/PR, BIOS region SHA-256, reference image comparison), ACPI table analysis (MCFG/HEST/BERT/SLIT/CEDT), Super I/O HWM sensors, CMOS/RTC, ReBAR live status, POST codes.

**Cross-Reconciliation** — 26 rules checking semantic and pipeline consistency across multiple sources; contradictions highlighted in red with both sides named.

**Deep Bench** — 38-item Run Session covering CPU AES/SHA, cache/DRAM latency ladders, GPU raster/texture/codec, storage QD ladder, NUMA/TLB/large-page costs, synthetic workloads. Raw samples only — no weighted composite scores.

## Documentation

| Document | Content |
|---|---|
| `docs/spec/METHODOLOGY.md` | Fact source contract: three-state, SpecRef, cross-reconciliation |
| `docs/spec/LIMITATIONS.md` | Public limitations: design decisions and capability boundaries |
| `docs/DATA-SOVEREIGNTY.md` | Data sovereignty: zero network API in fact collection (machine-checked) |
| `docs/EC-RISK-ASSESSMENT.md` | EC port access risk assessment (concluded: not implemented) |
| `docs/DEPLOYMENT.md` | Deployment and enterprise operations |

## Privacy

- Zero network API in the fact-collection and decoding layer (machine-checked source scan)
- All data stored locally; deleting files deletes data — no cloud copies
- Machine ID is one-way hash derived; sensitive values masked by default
- Self-telemetry off by default, anonymous, local-only

## License

[MIT License](LICENSE). Third-party components (Intel XTU SDK, LibreHardwareMonitor, etc.) are governed by their respective licenses.

By：Xinglanclever
