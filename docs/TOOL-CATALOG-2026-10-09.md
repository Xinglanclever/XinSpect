# Windows 底層診斷 / 調試 / 取證工具整合校準版

> **收錄說明**：本檔是使用者 2026-10-09 的校準版全文，**照原樣收錄、未改寫**（語言、措辭、分類都是使用者的裁定）。
> 校準原則（使用者定調）：**「重寫整合」為最高優先級**——先按問題域重寫、去重、校準；**「調度／排序」只作為最低優先級**——僅在最後給「實在不可行時的最小選型路徑」。原文中大量重複段落已合併，同一工具只在主類別詳述。
> 這份目錄是能力判定的**對照來源**。判定本身見：
> - `docs/CAPABILITY-DOMAINS-2026-10-09.md`——12 域逐域判定（對前一份清單）
> - `docs/CAPABILITY-ADDITIONS-2026-10-09.md`——**本檔校準後的增量判定**（還能加什麼）

---

# Windows 底层诊断 / 调试 / 取证工具整合校准版

> 校准原则：
> 1. 不再按原文出现顺序堆叠，而按“能力域”重组。
> 2. 同一工具只保留一个主条目，别名、生态、重复描述合并。
> 3. 区分“工具本体”和“生态组件”，例如 smartctl 属于 smartmontools，UEFIExtract 属于 UEFITool 生态。
> 4. 厂商专用、原生内置、开源框架、便携工具分开标注。
> 5. 最后才给“最小调度路径”，不是主输出。

---

## 1. 底层硬件访问、总线与协议分析

- **RWEverything**：用户态直接读写 PCI/PCIe 配置空间、内存、I/O 空间、SMBIOS 等。硬件/BIOS/驱动工程师常用。
- **HE – Hardware Read & Write Utility**：与 RWEverything 类似，偏 PCI Express 读数、索引内存、Super I/O、PCI/内存/I/O 访问。
- **PawnIO**：可脚本化内核模式驱动，让用户态程序受控访问硬件；含 LpcACPIEC 模块，可只读 ACPI EC。
- **Intel System Bring-up Toolkit**：Intel 官方系统启动/验证工具集，支持 BIOS/UEFI、固件、驱动、内核调试与 JTAG 硬件级调试。
- **Bus Hound**：经典总线协议分析工具，可捕获解析 USB、PCI、PCIe、SPI、I2C 等。
- **peripheral-forensic**：解析 setupapi.dev.log，生成 USB、FireWire、Thunderbolt、PCIe 外设连接时间线与安全风险。
- **Teledyne LeCroy LinkExpert / Summit**：PCIe 协议级分析顶级工具，支持 PCIe、NVMe、CXL 的 RAS 错误注入与数据包捕获。
- **Kandou Besso**：PCIe Retimer 诊断工具，提供 EyeScope 眼图、BER 误码率监控。
- **lsdsk**：命令行存储诊断，按控制器和 PCIe 路径分组驱动器，发现 SSD/控制器瓶颈。
- **Brokkr Diagnostics**：NVIDIA GPU 与 InfiniBand 诊断，含 GPU 硬件分析、PCIe 拓扑验证、lspci 诊断。
- **PCAN-Explorer 7**：专业 CAN CC/FD/XL 总线分析、监控、模拟。
- **BUSMASTER**：开源/经济型 CAN 总线测试开发工具。
- **Free USB Analyzer**：轻量 USB 监控，解析 USB 传输层。
- **USBPcap**：开源 USB 嗅探器，导出供 Wireshark 分析。
- **pcileech-mcp-server / StrataDMA / Aetheris**：DMA/PCILeech 相关，内存读写、地址转换、物理内存分析、取证。

---

## 2. 内核、驱动、转储与实时调试

- **WinDbg**：微软官方内核调试器，分析 .dmp、蓝屏、WHEA_UNCORRECTABLE_ERROR (0x124)，加载符号后 `!analyze -v`。
- **LiveKd**：不重启系统，在本地实时运行 Kd/WinDbg 内核调试器。
- **kn-live-dbg**：TUI 控制台实时内核调试，无需重启。
- **Windows Kernel Explorer (WKE)**：支持 XP 到 Win11，查看内核对象、驱动、回调。
- **Driver Verifier**：Windows 内置驱动验证，检测内存损坏、死锁、资源泄漏。
- **KASAN**：内核地址清理器，检测驱动非法内存访问。
- **DriverExplorer**：Rust 编写，内核驱动枚举、签名验证、SCM 操作、深色 GUI。
- **DriverStudio / SoftICE**：经典驱动开发套件与内核级调试器，历史标杆。
- **HyperDbg**：Hypervisor 辅助调试器，用于逆向、模糊测试、高级系统分析。
- **HardwareBPsHook**：C 语言硬件断点钩子库，操作调试寄存器。
- **VEH Debugger**：基于向量化异常处理的进程内调试器，支持硬件断点、反调试绕过。
- **PoolMon**：WDK 内核池内存监控，定位内核泄漏池标签。
- **Process Monitor (Procmon)**：Sysinternals 实时监控文件、注册表、进程/线程活动。
- **NotMyFault**：Sysinternals 故障注入工具，故意蓝屏、死机、内核内存泄漏。
- **PassMark BurnInTest**：硬件稳定性压力测试，专业版支持故障注入。
- **ApiValidator**：WDK 工具，验证驱动 API 对通用 Windows 驱动是否有效。
- **DevCon**：命令行设备管理，自动化驱动安装与设备配置。
- **DrvEye**：Windows 内核驱动静态分析与漏洞分类，发现 IOCTL、符号链接、BYOVD 风险。
- **DriverSight**：内核驱动 IOCTL 漏洞扫描器。
- **DriverRookie**：PowerShell 驱动版本检查与更新报告。
- **Surveyor (eSentire Labs)**：内核回调、ETW 会话、驱动分析、系统状态可见性。
- **YDArk**：X64 内核 ARK，进程/驱动/SSDT/Hook 检测，Win7-Win11。
- **NtWarden**：ImGui + DirectX 11，系统检查，覆盖进程、服务、网络、内核、ETW、注册表；可选内核驱动。
- **KSword**：源码可见 ARK、内核调试、系统取证，支持 Win10/11 x64。
- **Kdrill**：Python 内核分析，评估 Rootkit。
- **Kellect**：基于 ETW 的内核级事件日志收集器，低 CPU 占用。
- **ATool**：安天内核分析与 Rootkit 检测。
- **KVC**：Ring-0 进程保护、内存取证、凭据提取、驱动签名强制控制。
- **Comprehensive Kernel Debugging for Windows Developers**：SANS 内核调试速查海报。
- **DTrace on Windows**：Windows Server 2025 起内置，内核级动态追踪。
- **whesvc**：Windows 11 硬件错误/性能诊断服务，核心在 windiag.dll，内嵌 Lua。

---

## 3. 固件、UEFI、ACPI、EC 与虚拟化底层

- **UEFITool**：跨平台解析、提取、修改 UEFI 固件镜像。
- **UEFIExtract**：UEFITool 生态组件，分解 UEFI 镜像，层次视图呈现模块与资源。
- **CERT UEFI Parser (CMU SEI)**：程序分析恢复 UEFI 数据结构，输出文本、JSON、SBOM-ready JSON。
- **CHIPSEC (Intel)**：PC 平台安全框架，覆盖硬件、BIOS/UEFI、平台配置，含安全测试套件。
- **Velociraptor UEFI Artifacts**：对 EFI 系统分区做磁盘分析与 YARA 检测。
- **BIOSUtilities**：跨平台固件研究/修改工具套件。
- **efiXplorer**：IDA 插件，UEFI 固件分析与逆向自动化。
- **pybinwalk**：Binwalk v3 Python 绑定，固件签名扫描与提取。
- **reap-cli**：递归提取固件与分区镜像，支持 eMMC/flash、GPT、Rockchip PARM。
- **ACPICA**：Intel 官方 ACPI 工具集，ASL 编译器、反汇编器、诊断工具。
- **ACPI Debugger**：开源 Windows ACPI 调试，分析 BIOS/UEFI 中的 ACPI 表。
- **windows-pc-stability-evaluation**：通过 ACPI 结构与 DPC 延迟评估系统稳定性，使用 WPR/WPA、acpidump、iasl。
- **H2ODDT Pro / Insyde H2ODDT Pro**：骁龙 X 系列 PC 的 BIOS 调试方案，支持 USB/UART 本地与远程调试。
- **PafishX**：通过 CPUID、WMI、注册表、硬件、时序检测虚拟机/沙箱。
- **Coreinfo**：Sysinternals，显示 CPU 虚拟化能力与 NUMA 拓扑。
- **PawnIO LpcACPIEC**：只读访问 ACPI 嵌入式控制器。
- **EC-Access-Tool**：实现 ACPI EC 规范，读写 EC RAM。

---

## 4. 内存诊断与内存取证

- **MemTest86 (PassMark)**：独立 U 盘启动，14 种 RAM 测试算法，可检测 Row Hammer，服务器/工作站首选。
- **Windows Proactive Memory Diagnostics**：Win11 重启时自动触发内存扫描。
- **Volatility 3 / Volatility Framework**：内存取证事实标准，支持 Windows/Linux/macOS。
- **LovelyMem**：Rust + Tauri 2 可视化内存取证，集成 MemProcFS、Volatility 2/3。
- **DumpIt / WinPmem**：内存获取工具，常与 Volatility 配合。
- **Zada-Xor**：Rust Windows 内部机制恶意软件调查库。
- **StrataDMA**：C++17 包装 MemProcFS/VMMDLL，DMA 内存 API、进程自省、扫描。
- **KVC**：Ring-0 内存取证、凭据提取、驱动签名控制。
- **KAPE**：Kroll 取证分流框架，在线系统或镜像收集工件。
- **Elcomsoft System Recovery**：数字取证分流，支持 Server 2025、BitLocker 密钥导出、SRUM 分析。
- **Elcomsoft Quick Triage (EQT)**：快速现场数据采集与初步分析。
- **windows-ir-toolkit**：应急响应取证包，现场收集、内存分析、证据链追踪。
- **Aetheris**：高级系统仪表与取证套件，含 MFT 树状图、句柄表、DMA 物理内存分析。
- **BitProbe**：Windows 优先取证编排框架，权限感知自动化流水线。
- **tlvb**：自主 Windows 取证 IR 代理，结合 Sigma/Hayabusa/ATT&CK STIX。
- **Live Forensicator**：应急响应 PowerShell 脚本。
- **SentinelX**：Windows 安全事件排查、Sysmon 管理、攻击链分析、取证快照。
- **萤火 (yinghuo)**：轻量图形应急响应客户端，账号/进程/网络/持久化/日志/文件取证。
- **Glow**：硬件安全审计，分析 TPM 2.0、Secure Boot、VBS、HVCI、IOMMU。

---

## 5. 存储、NVMe 与文件系统诊断

- **smartmontools / smartctl / GSmartControl**：同一生态。smartctl 命令行，GSmartControl 图形界面；读取 S.M.A.R.T.、NVMe 健康日志，脚本化批量检查。
- **openSeaChest (Seagate)**：开源跨平台，支持 SATA/SAS/NVMe/USB，健康、配置、固件、安全擦除。
- **Clear Disk Info**：便携，诊断 HDD/SSD/NVMe，解析 S.M.A.R.T. 与 NVMe 健康日志。
- **WatuDisk / 挖兔硬盘精灵**：免费无广告，SATA/NVMe，六档颜色预警、开机静默体检、HTML 报告。
- **HD Tune Pro**：S.M.A.R.T. + 磁盘表面错误扫描，识别坏道。
- **Victoria**：老牌硬盘检测修复，2026 版优化 NVMe，底层读写测试。
- **SeaTools for Windows**：希捷官方硬盘/SSD 测试诊断。
- **厂商专用工具箱**：如 Corsair SSD Toolbox，固件更新、健康度、安全擦除。
- **DiskSpd (Microsoft)**：存储负载生成与性能测试，服务器/云基础设施基准。
- **WizTree / TreeSize**：极速磁盘空间分析，WizTree 直接读 MFT，TreeSize 报告更深入。
- **AllInsight**：存储分析、设备健康、安全清理，兼 CPU/内存/GPU/磁盘/网络监控。
- **Get-PhysicalDisk**：PowerShell 内置，`Get-StorageReliabilityCounter` 快速看可靠性计数器。
- **lsdsk**：按控制器和 PCIe 路径分组，发现 SSD/控制器瓶颈。
- **SPECS**：便携单文件，WMI + S.M.A.R.T. + SSD 安全基准 + 实时监控 + 评分 + HTML/PDF/JSON 报告。

---

## 6. 性能、ETW、延迟与系统监控

- **Windows Performance Toolkit (WPT)**：含 WPR + WPA，微软官方系统级性能分析。
- **xperf**：WPT 中命令行 ETW 捕获分析工具。
- **PerfView**：微软 .NET 团队，ETW 性能分析，托管/非托管代码。
- **etw-mcp**：MCP 服务器，分析 ETW .etl，报告 CPU、热点函数、DPC 健康。
- **wpa-mcp**：C# MCP 服务器，暴露 WPA/ETW 分析，诊断启动慢、进程创建慢。
- **ALPA (Amazing Latency Performance Audit)**：ETW 深度洞察，输入延迟、微卡顿、DPC/ISR 内核延迟。
- **LatencyMon**：诊断 DPC/中断延迟，音频卡顿、高延迟定位。
- **Intel PresentMon**：捕获图形应用帧持续时间与延迟，支持 DX/OpenGL/Vulkan。
- **Superluminal**：Windows 性能分析器，高频采样运行中进程。
- **System Informer**：多用途系统监控，实时图表、句柄搜索、网络连接、磁盘/GPU 活动，Process Explorer 现代替代。
- **Sysmon**：Sysinternals 系统监控服务，进程创建、网络连接、文件更改等。
- **Process Monitor / PoolMon**：见内核调试。
- **性能监视器 (PerfMon)**：内置性能计数器收集与查看。
- **Windows Monitor**：Win10/11 一体化监控，100+ 事件类型。
- **PRS SYSTEM**：赛博朋克主题桌面/CLI，PyQt6 + psutil 实时硬件遥测。
- **SysInfo (Rogit-28)**：Rust 高性能遥测，CPU/GPU/内存/存储/网络/事件日志。
- **Sentinella**：跨平台开源监控，TUI、Web 仪表板、远程监控。
- **LiteMonitor**：轻量可定制桌面硬件监控，支持 FPS、插件。
- **rtop / pstop**：Rust 终端系统监控，类似 btop/htop。
- **Zyphor**：Zig 编写，极致性能系统观测，零分配、缓存局部性。
- **Deansbury**：Windows 设备树查看器，SetupAPI 树状探索。
- **GPUView**：微软 GPU/CPU 活动性能分析，读取 .etl 图形化呈现。

---

## 7. GPU、图形与计算调试

- **GPU-Z**：传感器日志、BIOS 读取，分析 GPU 降频、功耗墙。
- **Nvidia Inspector**：NVIDIA 驱动级设置、GPU 状态监控、硬件信息。
- **Brokkr Diagnostics**：NVIDIA GPU + InfiniBand 综合诊断，GPU 硬件、PCIe 拓扑。
- **GPUd (NVIDIA NGC)**：开源守护进程，监控 NVIDIA GPU 与主机健康，检测 Xid、SXid、ECC。
- **RenderDoc**：免费独立图形调试器，Vulkan、D3D11/12、OpenGL，单帧捕获。
- **NVIDIA Nsight**：GPU 计算开发环境，调试/分析 CUDA、OpenCL、DirectCompute。
- **GPU Inspector**：GPU 图形调试器，支持 Vulkan、D3D12/11、Metal、OpenGL ES。
- **GFXReconstruct**：捕获回放图形 API 调用，Vulkan、DX12。
- **CodeXL**：GPU 调试器、GPU/CPU 分析器、图形帧分析器、静态着色器/内核观察。
- **PVRCarbon**：图形 API 使用检查，逐帧调试 GPU 状态与渲染输出。
- **GAPID**：Google 图形 API 调试器，OpenGL ES、Vulkan。
- **Remotery**：单 C 文件实时 CPU/GPU 分析器，浏览器查看器。
- **gpufl (GPUFlight)**：开源 CUDA/ROCm/HIP 性能剖析客户端，Python 包，结构化日志 + Agent 采集。
- **Intel PresentMon**：图形性能帧时间与延迟。

---

## 8. 网络、无线、USB、Thunderbolt、音频与总线

- **PsPing**：Sysinternals，TCP ping、延迟测量、带宽测试。
- **NETworkManager**：网络扫描、诊断、远程访问，2026 版含防火墙工具、Speedtest。
- **NetSonar**：ICMP/TCP/UDP/TLS/DNS/HTTP 探测与监控。
- **Pktmon (Packet Monitor)**：Windows 内置跨组件网络诊断，报告丢包位置与原因。
- **netsh trace**：内置组件级网络跟踪，捕获客户端/服务器场景。
- **NetReplay**：捕获、记录、重放 TCP 流量，支持 TLS，Scapy + Npcap。
- **PingMedic**：网络故障排除，ping 监控、速度测试、适配器诊断、Wi-Fi 检查。
- **hoppy**：更友好的 ifconfig/netstat/ping 替代，跨平台单二进制。
- **NetSuperAdapterTool**：下一代网络诊断修复，集成数据包分析、AI 故障排除、安全加固。
- **Ninja (is-leeroy-jenkins)**：WPF 统一界面，Wi-Fi 分析、IP/端口扫描、Ping、Traceroute、DNS、LLDP/CDP。
- **Qube Network Diagnostics (ND-300)**：跨平台，用户模式/技师模式，8 项核心诊断。
- **Wireshark / Npcap / Scapy**：抓包分析生态，常与 Pktmon、NetReplay、USBPcap 配合。
- **TI WiLink 无线工具套件**：Bluetooth Logger、WLAN gLogger、LQM、HCITester，RF 验证。
- **LOG Tool (Realtek Ameba)**：串口日志，捕获蓝牙主机/固件日志，读写 Wi-Fi 寄存器。
- **Windows 11 USB4 能力检查**：设置 > 蓝牙和设备 > USB > USB4 集线器和设备。
- **USB4/Thunderbolt 链路排查**：BIOS 中 USB4 PCIE Tunneling 开关、设备管理器黄色感叹号、PCI ID 检查。
- **PyAudioFixerWin11**：Python 脚本，诊断修复 Win11 音频，含 Realtek 专项。
- **Free USB Analyzer / USBPcap / Bus Hound**：USB 监控、嗅探、协议分析。
- **PCAN-Explorer 7 / BUSMASTER**：CAN 总线。
- **peripheral-forensic**：外设连接时间线。

---

## 9. 电源管理与能效诊断

- **powercfg /energy**：内置，60 秒后台分析，生成 HTML 能效报告。
- **powercfg /batteryreport**：电池使用历史、容量衰减报告。
- **powercfg /systempowerreport**：系统电源报告，替代旧 systemsleepdiagnostics。
- **SleepStudy**：Modern Standby 诊断，分析唤醒进程/硬件中断。
- **Powir**：便携电源策略面板，汇总唤醒源、监控充电阈值。
- **ThrottleStop**：Intel CPU 降压与 throttling 审计，监控/调整电压与功耗限制。
- **SMUDebugTool**：AMD Ryzen SMU、PCI、MSR、电源表直接硬件访问。
- **ACPICA / ACPI Debugger / windows-pc-stability-evaluation**：ACPI 底层与稳定性。
- **PawnIO LpcACPIEC / EC-Access-Tool**：EC 访问。
- **whesvc / DTrace on Windows**：原生诊断与动态追踪。

---

## 10. 安全、Rootkit、取证与应急响应

- **Sysmon**：系统监控服务，安全分析与故障排查。
- **SentinelX**：安全事件排查、Sysmon 管理、攻击链、取证快照。
- **KAPE**：DFIR 分流框架。
- **Elcomsoft System Recovery / Quick Triage**：取证分流与快速现场采集。
- **Live Forensicator**：应急响应 PowerShell。
- **windows-ir-toolkit**：现场收集、内存分析、证据链。
- **Aetheris**：系统仪表与取证套件，DMA 物理内存分析。
- **BitProbe**：取证编排自动化流水线。
- **tlvb**：自主取证 IR 代理。
- **萤火 (yinghuo)**：轻量图形应急响应。
- **Glow**：硬件安全基线审计。
- **Volatility 3 / LovelyMem / KVC / Zada-Xor / StrataDMA**：内存取证与调查。
- **ATool / KSword / Kdrill / Kellect / YDArk / NtWarden / Surveyor / WKE**：内核安全、ARK、Rootkit 检测。
- **DrvEye / DriverSight / DriverRookie / DriverExplorer**：驱动漏洞与版本分析。
- **CHIPSEC / CERT UEFI Parser / Velociraptor UEFI Artifacts**：固件安全。
- **PafishX / Coreinfo**：虚拟化检测与能力检查。

---

## 11. 综合硬件诊断、系统信息与便携工具

- **SPECS**：便携单文件，WMI + S.M.A.R.T. + SSD 基准 + 实时监控 + 评分 + HTML/PDF/JSON。
- **NWinfo**：轻量开源系统信息，硬件/网络报告，导出 JSON/YAML/LUA。
- **MB-Nexus-Cockpit**：自动配置 LibreHardwareMonitor、PawnIO、smartmontools。
- **PC-Check Windows**：Eurosoft 专业硬件诊断，可与 UEFI 裸机测试结合。
- **PC-AI**：本地 LLM 驱动 PC 诊断优化，PowerShell 模块，本地处理。
- **laptop-check**：零依赖 CLI，验证二手笔记本真实规格与健康，可编译独立 exe。
- **OxidPulse**：轻量超快硬件精确诊断，原生 Windows x86_64/ARM64。
- **wfdiag**：WindowsForum 官方诊断，49 个任务，覆盖系统/硬件/存储/网络/安全/软件/日志/图形/驱动/性能/调试。
- **Windows Monitor / PRS SYSTEM / SysInfo / Sentinella / LiteMonitor**：实时监控与遥测。
- **System Informer**：现代多用途系统监控。

---

## 12. 驱动开发与验证

- **WDK**：Windows 驱动工具包。
- **Driver Verifier**：驱动验证。
- **DevCon**：设备管理命令行。
- **ApiValidator**：API 兼容验证。
- **KASAN**：内核地址清理器。
- **DriverExplorer**：驱动查看与管理。
- **DriverStudio / SoftICE**：经典驱动开发调试。
- **WinDbg / LiveKd / HyperDbg / HardwareBPsHook / VEH Debugger**：调试与钩子。
- **Comprehensive Kernel Debugging for Windows Developers**：SANS 速查。

---

# 重要去重与校准说明

1. **NotMyFault** 在原文多次出现，归入“故障注入/内核调试”，不是通用诊断。
2. **Driver Verifier** 重复出现，归入“驱动验证/内核调试”。
3. **smartctl、smartmontools、GSmartControl** 是同一生态：smartctl 是命令行核心，GSmartControl 是 GUI。
4. **UEFIExtract 与 UEFITool** 是同一生态，UEFIExtract 不应与 UEFITool 完全并列。
5. **WPT、WPR、WPA、xperf** 是同一套件。
6. **Surveyor、YDArk、NtWarden、CERT UEFI Parser、NetReplay、gpufl、SPECS、wfdiag、DTrace、whesvc** 在原文重复大段，已合并。
7. **System Informer** 原文重复，归入系统监控。
8. **powercfg /energy、/batteryreport、/systempowerreport** 是同一内置工具的不同参数。
9. **H2ODDT Pro 与 Insyde H2ODDT Pro** 为同一工具。
10. **Elcomsoft System Recovery 与 Quick Triage** 同厂不同定位：前者恢复/取证分流，后者快速现场采集。
11. **GPU-Z、Nvidia Inspector** 偏监控/驱动级设置，不是协议级 GPU 调试器。
12. **Brokkr Diagnostics 与 GPUd** 偏 NVIDIA 数据中心/GPU 健康，不等同于 RenderDoc/Nsight。
13. **Windows Proactive Memory Diagnostics 与 MemTest86** 不同：前者系统内置轻量，后者独立深度。
14. **Clear Disk Info 与 WatuDisk** 功能相近，但 WatuDisk 更偏免费中文便携与预警报告。
15. **厂商专用工具箱** 与通用 smartctl 不同，通常结合固件、安全擦除、品牌 SSD 健康。

---

# 最低优先级：实在不可行时的最小调度/选型路径

> 仅当无法做完整整合、只能按场景临时调度时使用。

- **蓝屏 / WHEA / 驱动崩溃**：WinDbg + LiveKd + Driver Verifier + PoolMon + NotMyFault + DTrace/whesvc。
- **PCIe / 高速链路 / Retimer**：RWEverything/HE + Teledyne LeCroy + Kandou Besso + lsdsk + Brokkr。
- **存储 / NVMe / 硬盘健康**：smartctl/GSmartControl + Clear Disk Info + openSeaChest + DiskSpd + WizTree + Get-PhysicalDisk。
- **性能卡顿 / 微卡顿 / 输入延迟**：WPR/WPA + PerfView + LatencyMon + ALPA + PresentMon + System Informer。
- **电源 / 睡眠 / 续航**：powercfg /energy + /batteryreport + /systempowerreport + SleepStudy + Powir + ThrottleStop + SMUDebugTool。
- **固件 / UEFI / ACPI 安全**：UEFITool/UEFIExtract + CHIPSEC + CERT UEFI Parser + ACPICA + Velociraptor UEFI。
- **安全取证 / 应急响应**：Sysmon + KAPE + Volatility 3 + LovelyMem + windows-ir-toolkit + YDArk/NtWarden/KSword。
- **GPU / 图形 / 计算**：GPU-Z + Nvidia Inspector + GPUView + RenderDoc + Nsight + gpufl + GPUd/Brokkr。
- **网络 / 丢包 / 协议复现**：Pktmon + netsh trace + PsPing + NETworkManager + NetSonar + NetReplay + Wireshark。
- **驱动开发 / 验证**：WDK + Driver Verifier + DevCon + ApiValidator + KASAN + WinDbg + LiveKd + HyperDbg。
