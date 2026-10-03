繁體中文 · [简体中文](README.zh-CN.md)

# 曦览 XinSpect

一款免费开源、运行于 Windows 的原生硬件验机、监控与安全稽核工具。以单一执行文件发布，免安装；对硬件与系统的读取以只读为原则，少数涉及写入的功能均设有同意闸门并明确标注风险。本程序不收集、不上传任何用户数据。

![版本](https://img.shields.io/badge/version-2.5.0-4C8DFF)
![平台](https://img.shields.io/badge/platform-Windows%20x64-0A7EA4)
![框架](https://img.shields.io/badge/.NET-10.0--windows%20(WPF)-512BD4)
![测试](https://img.shields.io/badge/tests-3094%20passed-3FB950)
![突变分数](https://img.shields.io/badge/Stryker-82%25-8B5CF6)
![授权](https://img.shields.io/badge/license-MIT-green)

![总览](gallery2.png)

## 定位

曦览不对硬件下结论。它把三个来源的陈述并列摊开：硬件自己说的（MSR、PCI 配置空间、SMBUS 设备、SMART）、固件说的（ACPI 表、SMBIOS、UEFI 变量）、操作系统说的（WMI、注册表、事件日志、性能计数器），读得到就列出数值与来源，读不到就标示原因。当两个来源对同一件事有不同的说法，交叉对账引擎会把矛盾列为红字。判读留给使用者。

适用场景：二手电脑买卖前的验机、组装后的硬件确认、系统不稳定时的归因、安全状态稽核、长期性能监控。

## 设计原则

以下原则不是宣言，每一条都有单元测试或机器检查守着：

**三态标注。** 每笔事实带 `availability` 字段：`Present`（读到）、`NotSupported`（平台不提供）、`InsufficientPrivilege`（权限不足）、`ReadError`（读取失败）、`NotApplicable`（本机无此硬件）。读取失败时程序显示原因文字，数值字段留空。代码中不存在以 0、0xFF、典型值或上一次读值填补缺口的路径；违反这条规则的实现会在代码审查与测试阶段被拦下。

**来源可稽核。** 每笔事实的 `source` 字段记录数据出处：寄存器地址与位位置、WMI 类名称、API 名称。寄存器解码器的方法必须附加 `SpecRef` 属性（文档、章节、寄存器、位元），`SpecRefRegistry` 以反射逐一检查已注册解码器的每个公开方法，缺引用即测试失败。

**非自造验证。** 量测类功能在输出数字前先验证量测本身：棋类跑分以 perft 叶节点数为检核码（数学常数，偏离即计算错误而非性能差异）；内存带宽换算前以已知大小的负载自我验证，对不上时只输出原始计数器值；PMU 编程验证逐轮确认写入读回一致与还原完整。

**不重复造轮也不重复宣称。** 同一事实若已有多个来源，程序不做单来源宣称，而是交叉比对。两来源一致时给「一致」，不一致时给「矛盾」并列出双方，任一缺席时给「无法验证」。

**查不到不等于没有。** 查询语言与本机 API 对「存在但读不到」的条目照样返回匹配与原因；语法错误返回 400 与修正指引，不以空结果伪装。

## 系统架构

| 组件 | 形态 | 职责 | 权限 |
|---|---|---|---|
| 曦览主程序 | WPF（.NET 10，单档发布） | 界面、事实收集、对账、报告 | 多数功能不需要；MSR／SMART／Security log 需系统管理员 |
| WinRing0 | 既有核心驱动（反射加载 LibreHardwareMonitor Ring0 模块） | MSR／PCI 配置空间／物理内存 MMIO 读取主力 | 系统管理员 |
| XsRegProbe | 本专案自写的白名单只读驱动（源码在 `XsRegProbe/`） | 允许清单内的 MSR／MMIO 读取备援；允许清单外一律拒绝；退出即卸载 | 系统管理员，需自行编译签名 |
| Intel XTU 桥接 | net48 独立进程（Release 建置自动内置） | 承载 XTU SDK 供 CPU 超频 | 系统管理员 |
| 蓝色中队守护进程 | .NET 10 console（Release 建置自动内置） | ETW 即时威胁侦测、驱动基线、stdin/stdout JSON IPC | 用户权限 |

内置机制：Release 建置时以 MSBuild Target 发布桥接程序并内置为资源；运行期首次使用时解压至 `%LOCALAPPDATA%\XinSpect\`，先以 SHA-256 对内嵌正本验证、目录 ACL 收紧为 SYSTEM＋Administrators，验证不过即拒绝执行。Debug 建置不内置，以开发路径搜寻。

## 下载与系统需求

| 文件 | 大小 | 用途 |
|---|---|---|
| [XinSpect.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v2.5.0/XinSpect.exe) | 29,716,269 bytes | 主程序。已内含蓝色中队守护进程 |
| [BlueSquadronBridge.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v2.5.0/BlueSquadronBridge.exe) | 6,502,948 bytes | 独立守护进程。仅在需要脱离主程序单独运行防护时使用 |

系统需求：Windows 10 1903 或更新、Windows 11 x64；.NET 10 Desktop Runtime（可改用自包含发布）。WinRing0 为双用途驱动，部分企业环境的应用程序控制政策可能拦截，拦截时相关事实标示「权限不足」，程序不降级宣称。

## 功能详述

以下依主界面左侧导航的分组顺序说明。每一节先列数据来源，再列边界（不做什么、为什么）。

### 总览

**我的电脑／总览。** 来源：WMI（Win32_Processor、Win32_PhysicalMemory、Win32_VideoController、Win32_DiskDrive、Win32_BaseBoard）、SMBIOS、CPUID。呈处理器／主板／内存／显卡／存储的规格摘要。内存插槽配置图依 SMBIOS 的物理排列绘制，实心代表有模块（附容量与速率）、虚线代表空槽；通道字母仅在固件插槽命名可辨识时标示，仅有 `DIMM0/DIMM1` 命名的板子会标明「无法辨识通道」，不做推论。

**瓶颈诊断。** 来源：各页已收集的事实。将温度墙、功耗墙、单线程、内存、存储、显卡、驱动 DPC、电源策略、MCA 平台事件等指标合并评估，依优先序排列。每条判断附产生它的原始数字与对应的深入量测页面。未被量测涵盖的项目列于「还没纳入判断的部分」，不计入结论。

**AI 评价。** 来源：用户设定的 Ollama 或 OpenAI 兼容端点。将本机硬件摘要交由语言模型生成评语，提示词可自定义。此为全程序少数会产生网络流量的功能之一，仅在用户主动呼叫时发生。

### 处理器

来源：CPUID、MSR（经 WinRing0 逐核绑定读取）、注册表。

- **CPUID 解读**：家族／型号／stepping 含 extended family 进位（此进位曾因实现漏做而由突变测试发现并修正）；指令集支持；缓存拓扑；die 拓扑（CPUID leaf 0x1F，未支持时标示 NotSupported，不退回 leaf 0xB 推测）。
- **微码修订**：双来源交叉——CPU 侧读 MSR 0x8B 高 32 位（逐物理核绑定，不一致时列出各核清单而不取单核值）；Windows 侧读注册表 `Update Revision`（8 字节与 4 字节两种实测布局皆支持，双 DWORD 皆非零的歧义标示「不解码」）。两来源一致／矛盾由对账规则裁决。
- **TjMax**：MSR 0x1A2 bits[23:16]。
- **频率真相**：MPERF/APERF 比值与 Turbo 阶梯，取样前后完整还原。
- **TME／SGX 内存加密**：TME 以 CPUID leaf 7 ECX bit25 与 MSR 0x982（启用位与算法栏）判定；SGX 以 leaf 7 ECX bit30 判定。未支持如实标示。
- **C-state 驻留**：MSR 0x60D（C2）、0x3FC（C3）、0x3F9（C6）、0x3FA（C7），单位 µs，平台未实现的项目标 NotSupported。
- **PMU**：能力探索（CPUID leaf 0xA：版本、通用与固定计数器数量与位宽）为只读且已出货；编程验证（写入 IA32_FIXED_CTR_CTRL 使能位→读回一致→已知工作量→计数器活动确认→还原读回确认，3 轮聚合）采同意闸门，写入范围仅限使能位、以 OR 并入原值、不触及 PMI 位。此功能标注「多轮测试・不保证可用」，与 wpr 等参考工具的对照（沙箱方案 S5）尚未执行。
- **性能天花板**：TCC 节流温度、PL1/PL2 与时间窗、电流限制、供电警报、Turbo 倍频表直接读自 MSR；限制原因寄存器为黏滞纪录位；另有用户触发的逐窗撞墙量测（基线／整数／AVX2／AVX-512）以 APERF/MPERF 量有效倍频。输出为单句归因（温度墙／功耗墙／电流墙／供电过热／自主 P-state／多核上限／缺口不在硬件）。只读，不清除黏滞位。

### 内存

来源：SMBUS（经白名单地址）、SMBIOS、WMI、ACPI SLIT。

- **SPD 直读**：DIMM 的制造商、序列号、时序参数，每笔标明读取的总线；TSOD 温度传感器。
- **插槽配置**：物理排列、通道辨识（见总览节）。
- **ECC 现况**、标称与实际速率对照。
- **Rowhammer**：程序仅提供风险声明与压力探测，不做正规施测。压力探测在自拥有的连续缓冲区内以两个热点高频读写、xorshift 样本逐字节验证（热点偏移排除）；需要用户明确同意（服务层对未同意呼叫直接拒绝）；界面与结果均标注「未经过校验」——usermode 无法执行 clflush，此探测不保证触发 Rowhammer，结果不构成内存可靠性的结论。多轮模式（10 轮聚合）同样标注：多轮零翻转不表示具备抗性。
- **NUMA**：拓扑（GetNumaHighestNodeNumber／ProcessorMaskEx）、节点距离矩阵（ACPI SLIT，宣告与数据不符时拒解）、TLB 与大分页成本量测。

### 主板

来源：SMBIOS、ACPI（经 GetSystemFirmwareTable）、Super I/O 设置端口、PCI 配置空间。

- **机箱开启侦测**：SMBIOS Type 3（System Enclosure）offset 12 的 Security Status。值 5（Intrusion detected）表示固件记录了机壳开启事件——这是拆机的固件级证据，界面以警示色呈现。机箱类型（offset 5）一并解出。
- **Super I/O**：0x2E/0x4E 设置端口进入、芯片 ID 与厂商 ID（名称对照取自 coreboot superiotool）、HWM 传感器（LDN 4 基址→base+5/+6 读取）：风扇 RPM（ITE 公式 1,350,000÷(divisor×count)，count 0/0xFFFF 为无效）、温度（8-bit 二补数）、电压（LSB 16 mV，标明未经主板分压校准）。设置模式在任何路径（含例外）都以 finally 退出。
- **PCI 盘点**：Bus 0 三十二槽扫描，多功能位元决定 function 扫描；PCI-SIG 类别码知识库转角色名称；BAR 资源解码（大小需写入探测，故只列地址与类型）。
- **ReBAR 实况**：查 Windows 实际指派给设备的内存范围（生效值，非能力宣称值），不需驱动。
- **SPI 闪存稽核**：HSFSTS 旗标（FLOCKDN/WRSDIS/FDOPSS）、FRAP 区域权限、FREG 地图、PR 保护范围；BIOS 区 SHA-256；与用户提供的参考映像逐 4 KB 块比对（大小不符拒比）。
- **ACPI 表列**：表头校验和验证；MCFG（ECAM）、HEST（硬件错误源）、BERT（开机错误记录）、SLIT（节点距离）、CEDT（CXL 固定内存窗口，CFMWS 逐栏）、HPET、FADT（PM timer 区块）。无对应表时标 NotApplicable。
- **CMOS/RTC**：电池电压（VRT）、时钟（BCD/12h 解码）、PC-AT 校验和；绝不写 0x71。

### 存储设备

来源：SMART_RCV_DRIVE_DATA ioctl（disk.sys 代理，不用 ATA_PASS-THROUGH）、NVMe Storage Query Property、WMI。

- **SMART 属性与门槛**：READ DATA（0xD0）取属性表、READ THRESHOLDS（0xD1）取门槛表。「现值 ≤ 门槛且门槛非 0」判定为现正低于门槛（failing now），逐项摊开；门槛 0 依规范为无门槛，不评比。
- **NVMe**：健康记录（log page 0x02）全解、WCTEMP 警告（Identify Controller offset 0x14A 对照合成温度）、错误记录（log page 0x01）、电源状态表对实测闲置唤醒延迟。
- **HPA 隐藏容量**：ATA IDENTIFY 的最大 LBA 对照 Win32_DiskDrive.Size——OS 可见少于固件声明即 HPA 作用中，换算隐藏容量。DCO 需厂商私有命令，标示 NotSupported 并说明原因。
- **表面扫描**：顺序读取逐块（默认 1 MB）量延迟，超过 100 ms 标慢、读取失败标错误。SMART 是固件的自述，这是程序自己读到的。
- **假容量写入验证**：在目标磁盘以可重现样本（chunkIndex 混入种子）写入至指定上限或写满，FlushToDisk 后读回逐字节验证，结束删除暂存文件。需要明确同意；写入量可观且可能加剧濒死媒体损耗，界面与说明均标注风险。此功能未接入任何自动流程，仅在明确呼叫时执行。
- 通电时数与机龄推估、QD 梯度性能。

### 显卡

来源：NVML（NVIDIA）、NVAPI、D3DKMT、注册表。

- NVML：温度、频率、功耗、风扇、温度阈值、退休页（nvmlDeviceGetRetiredPages_v2，single/double-bit 合计；本测试机实测该卡不支持退休页报告，标 ReadError）。
- GPU 深测：光栅填充率、纹理取样、H.264 编码、计算管线、PCIe 频宽（D3D11 readback 对 CPU 参考比对，防止全零输出误判为有效）。
- HDR 能力：EDID CTA-861 HDR Static Metadata＋DisplayConfig 开关。
- 显示链路真相：连接技术、像素时钟、色彩编码、位元深度、所需影像数据率——频宽不足时驱动自行降色度，设置界面不会反映。
- TDR 设置：TdrLevel/TdrDelay/TdrDpcDelay 注册表值；未设置时标明 Windows 默认值。
- 超频：NVML 功耗／风扇／温度监控＋NVAPI 时脉调整，快照→回读验证→看门狗→自动还原。

### 网络

来源：WMI（MSFT_NetAdapterStatistics、Win32_NetworkAdapter）、wlanapi、注册表。

- 接口统计：错误与丢弃计数非零的接口逐条列出（驱动劣化、线材、交换器端口的第一手指纹）。
- MAC OUI 对照：IEEE 登记的知名前缀子集（比照 Super I/O 知识库模式），未收录如实标示。
- Wi-Fi：RSSI、频道（BSS list 中心频率依 IEEE 802.11 Annex E 换算，频率在等差之外回 null 不猜）、认证类型；接口未连线时如实列示「未连线」，不与「无接口」混报。
- 网络卸载（Checksum/RSS）、网卡高级属性、网速测试、网络延迟量测。

### 安全

固件安全页与防护页。来源：MSR、PCI、UEFI 变量、WMI、X509Store、事件记录、用户提供的清单档。

- **固件安全寄存器**：BIOS_CNTL（SMM_BWP/BLE/WE）、SMRAMC（D_LCK/D_OPEN）、ME 状态（HFSTS1，先验 0:16.0 为 Intel HECI）、IA32_FEATURE_CONTROL（Lock/VMX）、IA32_DEBUG_INTERFACE（ENABLE/LOCK/DEBUG_OCCURRED）、SPI 旗标与区域权限。每项给裁决文字，不利裁决（BLE=0、D_LCK=0、调试端口开放）以警示色呈现。
- **交叉对账**：26 条规则（外部化于 `Rules/builtin.json`，每条附 SpecRef 与误报条件）对同一批事实做语义与管线一致性检查。判决三种：一致／矛盾／无法验证。矛盾整列红字并列出双方输入。
- **平台可信度**：hypervisor 存在位与签名、VBS/HVCI（Win32_DeviceGuard）、核心代码完整性选项、Invariant TSC。VBS 执行中时明确告知：MSR 类卡片只能当参考。
- **BYOVD 比对**：加载中核心模块对微软「建议的驱动程序封锁规则」比对。清单 XML 由用户提供（放入程序目录或以 CLI 指定），零网络存取；比对两道——文件名（不分大小写）与 SHA-256/SHA-1（对实文件计算）。命中列出模块路径与规则依据；语义是「在封锁清单上的攻击面事实」，不是中毒判决。
- **安全鉴识**：Defender 排除清单（MSFT_MpPreference，逐条摊开）、Security log 1102 记录清除事件（需提权，权限不足与查询失败分开标示）、非微软本机信任根（X509Store Root，MITM 证书风险面）、USBSTOR 使用痕迹、驱动签名稽核（Win32_PnPSignedDriver 的 IsSigned）。
- **蓝色中队（防护页）**：六防线即时态势（DMA 与内存保护、固件与启动链、CPU 缓解、存储与数据、驱动与对抗、系统攻击面）＋ETW 即时威胁时间轴。守护进程已内置主程序（见系统架构节），安全页滑动开关控制启停并记入设置档；关闭时只读态势评估照常。
- **深层存取豁免开关**：产生自签 CA（RSA-4096，只放行这一张凭证，不开全机 test-signing）→ 安装 XsRegProbe 服务。关闭＝停止并删除服务、只移除自己 CA 的 thumbprint。非提权时不做任何变更。

### 系统与软件层

事实实验室的软件面。来源：WMI、注册表、事件记录、LSA、COM、psapi。

Windows Update 历史（WUA COM：最新一笔、30 天内安装数、失败计数）；服务盘点（Win32_Service：总数、执行中、自动、停用、非系统目录服务）；事件记录摘要（System log 7 天内严重＋错误，最常见来源×事件 ID）；稽核政策（LSA LsaQueryInformationPolicy，九类别等级）；机器原则档（Registry.pol 存在与写入时间指纹，不解析二进制）；选用功能（Win32_OptionalFeature：Hyper-V Hypervisor／虚拟机平台／WSL／容器，WMI 未回报的项目标「未回报」而非「停用」）；核心模块加载清单（EnumDeviceDrivers，非系统目录模块逐档 Authenticode 验证）；开机参数（SystemStartOptions：核心调试、测试签名）；开机计时（Diagnostics-Performance Event 100 的 BootTime）；USB 拓扑；摄影机列举；企业存储（iSCSI/MPIO 看 SCM 服务、FC 看 WMI HBA、NVMe-oF 标示侦测路径未实现）。

### 深测中心

38 项 Run Session。目录全量登记：可执行与延后项目都会摊开，不把未跑的项目说成量测。只并列原始样本、可信度（High/Medium/Low/Insufficient）与限制，不加权合成总分；跨域、跨数据体排名不成立。混合不同量测配置的样本会被混池判定拦下（梯子型指标逐点呈现）。

测项涵盖：CPU AES/SHA 吞吐、Load-to-use/ILP/branch 延迟、分支式矩阵、RDRAND/RDSEED、Intel PMU Top-down、核心延迟、核心到核心搬运频宽、SMT sibling 竞争、cache bandwidth/latency 阶梯、lock-scaling、NUMA/TLB/大分页对照、DRAM 映射推论、内存频宽、合成 JSON 往返、D3D11 光栅/纹理、GPU 光栅/纹理/编码、存储复合（FlushToDisk→读回→GPU 哈希→回读比对）、QD 梯度、耦合 loopback、WASAPI 信号级、多域 gauntlet、UX 合成负载、统计引擎自我稽核（已知答案合成样本集逐案例验证分类行为）。

### 监控与量测

- **传感器**：LibreHardwareMonitor 引擎，温度／时脉／电压／风扇／负载；迷你悬浮窗口、系统匣、超标警示（横幅＋气泡）、CSV 导出、历史回放。
- **性能**：棋类跑分（perft 检核）、算力图（离线天梯，来源 topcpu.net）、帧时间监测、DPC 延迟、线程迁移（ETW Context Switch 事件四层归因：SMT 兄弟／同 LLC／跨 LLC／跨 NUMA；扣掉量测自身线程；行程排名依迁移率）、L3 未命中与 DRAM 流量（已知负载自我验证）、大页与地址转换成本（同一指标链在 4 KB/2 MB 页各跑一次）、NPU 检测。
- **硬核只读量测**：SMI 次数（固件在 OS 看不见的模式处理，工作管理员恒为 0%；只给次数不乘推测耗时）、MCA/WHEA 机器检查、核心间延迟矩阵、RDT 缓存占用、电源政策、BIOS 与 ME 微码。

### 证据实验室

- **时间胶囊**：全机事实快照。SHA-256 完整性信封（canonical JSON）、匿名机器识别（单向哈希派生）、敏感值遮蔽（保留需主动指定）。跨快照逐栏差分；资产生命周期事件自动分类（内存／处理器／显卡／存储／主板的新增、移除、变更；其余变更标「状态」不冒充硬件变更）。
- **原始寄存器快照**（.xinraw）：PCI 安全寄存器、ACPI 整表、MSR、SPIBAR、MCHBAR 的原始字节；SHA-256 信封，篡改拒载；挥发位元（SMI 计数等）标注。
- **HTML 报告**：单档自足、尾端 SHA-256 标记可离线重算验证。
- **corpus 贡献包**（格式 v1 骨架）：只接受遮蔽版快照、身份键逐键排除；上传通路刻意未实现，重开前需逐次同意、本地预览、数据主权声明与服务器端不改写四项条件。

### 工具箱与实用工具

- **工具箱**：Windows 内置工具一键开启；八十余款第三方硬件工具的官方下载捷径。写固件、整碟抹除类项目挂「危险」／「注意」徽章并写明最坏情况；无官方发布站的工具不收录。
- **硬件检测**：屏幕坏点、鼠标按键／滚轮／回报率、键盘逐键与 NKRO、喇叭声道与扫频、动态拖影与帧间隔。纯原生输入事件，零外部依赖。
- **超频与风扇**：CPU 超频（XTU 桥接，倍频与电压在同一张目标时脉规划卡，含类比电压表）；显卡超频（NVML/NVAPI）；系统风扇手动调速与一键还原自动（真实写入 Super I/O）。
- **系统工具**：一键装机（winget）、垃圾清理、大档扫描、连接埠占用、Hosts 编辑器、右键菜单管理、Windows 授权、睡眠与唤醒、DNS 切换、内存整理、开机启动项、运算稳定性压测、蓝屏分析、系统引导修复。
- **显示与输入**：屏幕色域、USB 链路、PCIe 链路。
- **内置浏览器**（WebView2）与**终端机**（cmd.exe）。
- **高级驱动分析／操作系统分析**：驱动签名、日期与关键类别老化；OS 层安全性事实。

### 风险与同意闸门

下表列出所有涉及写入或潜在风险的操作，及其防护机制：

| 功能 | 写入内容 | 防护 |
|---|---|---|
| Rowhammer 压力探测 | 自拥有缓冲区内高频读写 | 同意闸门（UI 勾选＋服务层拒绝）；「未经过校验」标注；无自动接线 |
| 多轮压力模式 | 同上，10 轮 | 同上；明写多轮零翻转不表示具备抗性 |
| PMU 编程验证 | IA32_FIXED_CTR_CTRL 使能位（OR 并入，不碰 PMI） | 同意闸门；每轮 finally 还原原值；「多轮测试・不保证可用」标注 |
| 假容量写入验证 | 目标磁盘大量写入（至上限） | 同意闸门；危险声明（加剧濒死媒体损耗）；结束删档；无自动接线 |
| 磁盘表面扫描 | 无（纯读取） | 仅读取；可取消 |
| 深层存取开关 | 安装自签 CA 与驱动服务 | 非提权拒做；关闭完全移除；只放行自己的凭证 |
| CPU／显卡超频、风扇调速 | 硬件状态写入 | 快照→回读验证→看门狗→自动还原铁律 |

## CLI 与自动化

```
XinSpect.exe --json evidence [--query <查询>] [--out <文件>]
```

退出码：0＝全部事实 Present；2＝部分事实为三态；1＝致命错误。

查询语言：一行一子句，字段为 key／category／source／value／availability／since／until，运算子为 `=`（精确）、`~=`（包含）、`^=`（前缀）；availability 支持别名（ok／error／no-permission）。规格全文见 `docs/spec/query-language.v1.md`，解析器与文档由测试对账。

PowerShell：`XinSpect.psm1` 提供 `Get-XinSpectEvidence`。

本机 API：`GET /api/facts` 返回全部事实（canonical JSON，与时间胶囊同一形状）；`POST /api/query` 接受查询语言全文。设计为 loopback 只读；HTTP 监听壳未自动启动。

## 公开规格与文件

| 文件 | 内容 |
|---|---|
| `docs/spec/snapshot.schema.v1.json` | 时间胶囊 JSON Schema（2020-12），与实际序列化逐键机器对账 |
| `docs/spec/query-language.v1.md` | 查询语言规格（解析器与文档由测试对账） |
| `docs/spec/METHODOLOGY.md` | 事实来源契约：三态、SpecRef、交叉对账、突变测试 |
| `docs/MEASUREMENT-METHODOLOGY.md` | 量测方法学：混池禁令、时间源分工、临界值成文 |
| `docs/spec/LIMITATIONS.md` | 公开限制：设计裁决与能力边界逐条 |
| `docs/EC-RISK-ASSESSMENT.md` | EC 埠存取风险评估（结案：不实现，重开条件成文） |
| `docs/PMU-SANDBOX-PLAN.md` | PMU 编程沙箱验证方案（S1–S5） |
| `docs/DATA-SOVEREIGNTY.md` | 数据主权声明（含零网络 API 的机器检查） |
| `docs/DEPLOYMENT.md` | 部署与企业维运（发布、CLI、数据位置、升级回滚） |
| `docs/ITERATIONS.md` | 完整迭代账本（60+ 批次、逐轮内容、测试数、提交） |

## 品质保证

- **单元测试**：3094 项，全部通过。涵盖每个解码器的金标向量、每个服务的三态行为、每条对账规则的逐情境行为。
- **突变测试**：Stryker 对 XinSpect.Decoders 类库执行，分数 82%。知识表数据档（SuperIoKnowledge/PciKnowledge）刻意排除——逐条字符串断言等同快照重复。
- **SpecRef 覆盖**：反射检查已注册解码器的每个公开方法都有规格引用。
- **Property 测试**：FsCheck 对遮罩差分、MCFG 编码等性质做随机验证。
- **差分测试**：SMBIOS 内存解码与微软 Win32_PhysicalMemory 对照（同表双实现）。
- **文档对账**：README 版号、Changelog、csproj 三处一致由测试把关；规格文件与解析器的漂移会红灯。

## 隐私与数据主权

- 事实搜集与解码层的源码经机器检查扫描，不含任何网络 API（HttpClient、TcpClient 等）。全程序仅有的网络功能：AI 评价、意见回馈、网速测试、网络延迟量测——全部需要用户主动触发。
- 数据仅存于本机：审计日志在 `%ProgramData%\XinSpect\Audit\`、驱动凭证在 `%ProgramData%\XinSpect\Driver`、快照与报告在用户指定路径。删除文件即完成删除，无云端副本。
- 匿名机器识别为单向哈希派生（`sha256:` 前缀），不含序号原文。敏感值默认遮蔽。
- 自我遥测默认关闭、匿名、仅本机存取。

## 已知限制

- 突变测试仅涵盖纯解码器类库；主程序的特权通路层由注入式测试与金标向量承担。
- PMU 编程验证尚未与参考工具对照（沙箱方案 S5 未执行）。
- Rowhammer 压力探测非保证触发，不能作为内存可靠性的结论。
- DCO（Device Configuration Overlay）需厂商私有命令，未实现。
- MCHBAR 内存时序（tCL/tRCD/tRP/tRAS）属 MRC 训练结果区，Intel 公开文件未定义，不出值。
- PCH 世代名称对照未对准出处前不出值。
- Wi-Fi 连线态字段需接口实际连线才能读取。
- HVCI／VBS 开启时 MSR 可能被拦截或回虚拟值，可信度裁决在平台可信度页明示。

完整限制清单见 `docs/spec/LIMITATIONS.md`。

## 从源码建置

```
git clone https://github.com/Xinglanclever/XinSpect.git
cd XinSpect
dotnet build XinSpect.csproj -c Release
dotnet test Tests/XinSpect.Tests.csproj
dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Release 建置会自动发布并内置 XTU 超频桥接（net48）与蓝色中队守护进程；Debug 建置不内置以缩短建置时间。XsRegProbe 驱动需自行编译签名（`XsRegProbe/BUILD-给使用者.md`）。

## 版本沿革

- **v2.5.0 Olympus**（2026-10-04）：系列更名 Everest→Olympus；WinRing0 回归主力；交叉对账 26 条；验机杀手级检测（SMART failing-now、机箱开启、HPA、假容量验证、WCTEMP、TDR、退休页）；安全鉴识（BYOVD、Defender 排除、1102、信任根、USBSTOR）；处理器深化（TME/SGX、C-state、PMU、die 拓扑、SLIT）；系统软件层（Update 历史、服务、稽核政策、核心模块、USB、屏幕、网卡）；蓝色中队内置本体；深测中心 38 项；公开规格；DeepBench 启动崩溃修复；突变测试解锁。
- **v2.1.0 Everest**（2026-10-02）：固件安全页、深层寄存器、XsRegProbe 驱动、深层存取开关、Deep Bench 20、守护进程三版。

## 授权

本专案以 [MIT License](LICENSE) 释出。内置第三方组件（Intel XTU SDK、LibreHardwareMonitor、TraceEvent、NAudio 等）受其各自授权条款约束，不在本专案 MIT 授权范围内。WinRing0 为双用途驱动，本程序的 MSR 读取亦依赖它；使用者应仅在自有或获授权的机器上使用。

## 作者

By：Xinglanclever
