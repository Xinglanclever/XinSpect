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

