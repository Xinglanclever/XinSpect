繁體中文 · [简体中文](README.zh-CN.md)

# 曦览 XinSpect

一款免费开源、运行于 Windows 的原生硬件验机、监控与安全稽核工具。以单一执行文件发布，免安装；对硬件与系统的读取以只读为原则，少数涉及写入的功能均设有同意闸门并明确标注风险。本程序不收集、不上传任何用户数据。

![版本](https://img.shields.io/badge/version-2.51-4C8DFF)
![平台](https://img.shields.io/badge/platform-Windows%20x64-0A7EA4)
![框架](https://img.shields.io/badge/.NET-10.0--windows%20(WPF)-512BD4)
![测试](https://img.shields.io/badge/tests-3661%20passed-3FB950)
![突变分数](https://img.shields.io/badge/Stryker-82%25-8B5CF6)
![授权](https://img.shields.io/badge/license-MIT-green)

## 一、项目定位

曦览（XinSpect）以 WPF（.NET 10）编写、MVVM 架构，整合 LibreHardwareMonitor 传感引擎、Intel XTU 超频桥接、NVIDIA NVML／NVAPI 显卡控制、WebView2 内置浏览器，以及本专案自写的 WinRing0 事实读取层与 XsRegProbe 白名单只读驱动。名称中的「Spect」取自拉丁语的检视者：本工具不对用户的硬件下结论，而是把三个来源的陈述并列摊开——

1. **硬件自己说的**：MSR（型号特定寄存器）、PCI 配置空间、SMBUS 上的 SPD／TSOD、ATA SMART 与 NVMe 健康记录、Super I/O 寄存器、CMOS；
2. **固件说的**：ACPI 表（MCFG、HEST、BERT、SLIT、CEDT、HPET、FADT）、SMBIOS 结构、UEFI 变量；
3. **操作系统说的**：WMI（CIM 类别与 MSFT 高级类别）、注册表、事件日志、性能计数器、ETW 追踪、X509 证书存储区。

三个来源对同一件事的说法可能不同。读得到就列出数值与来源字段；读不到就标示原因（平台不支持、权限不足、读取失败）；当两个来源对同一件事有不同的说法，交叉对账引擎会把矛盾列为红字并列出双方。判读留给使用者。

适用场景：

- **二手电脑买卖前的验机**：机箱开启侦测（SMBIOS 入侵事件）、HPA 隐藏容量（翻新碟与容量窜改的直接证据）、SMART 现正低于门槛判定、微码修订交叉核对、驱动签名稽核、Defender 排除清单稽核、USBSTOR 使用痕迹、非微软根凭证稽核、假容量写入验证；
- **组装与升级后的硬件确认**：SPD 直读与插槽配置、内存通道、PCIe 链路协商速度、USB 链路速度、显示链路色彩编码、ReBAR 生效状态；
- **系统不稳定时的归因**：瓶颈诊断、性能天花板（温度墙／功耗墙／电流墙归因）、隐形停顿（SMI）、DPC 延迟肇事驱动、线程迁移、机器检查纪录；
- **安全状态稽核**：固件安全寄存器（BIOS_CNTL、SMRAMC）、蓝色中队六防线态势、BYOVD 封锁清单比对、深层存取豁免开关；
- **长期性能监控**：即时传感、历史回放、CSV 导出。

## 二、设计原则

以下原则不是宣言，每一条都有单元测试或机器检查守着。

### 2.1 三态标注

每笔事实带 `availability` 字段，取值五种：`Present`（读到）、`NotSupported`（平台不提供）、`InsufficientPrivilege`（权限不足）、`ReadError`（读取失败）、`NotApplicable`（本机无此硬件）。读取失败时界面显示原因文字，数值字段留空。

程序中不存在以 0、0xFF、典型值或上一次读值填补缺口的路径。三个例子说明这条规则的落实方式：Super I/O 风扇转速的计数值 0 与 0xFFFF 在 ITE 规格中是无效值，程序显示「无效计数（停转或未接）」而不是换算出 0 RPM；注册表微码修订的两个 DWORD 皆非零时属于布局歧义，程序显示「歧义——不解码」并附上原始 hex 供人工稽核；NVMe 的 WCTEMP 字段为 0 时表示控制器未提供，程序标示「未提供」而不是拿 0 去做比较。

### 2.2 来源可稽核

每笔事实的 `source` 字段记录数据出处：寄存器地址与位位置（例如「MSR 0x8B bits[63:32]，逐物理核绑定读取」）、WMI 类名称、ioctl 名称、API 名称。寄存器解码器的方法必须附加 `SpecRef` 属性，内容为文档名称、章节、寄存器与位元位置；`SpecRefRegistry` 以反射逐一检查已注册解码器的每个公开方法，缺引用即测试失败。目前已注册并受检查的解码器涵盖 SPI 闪存、芯片组安全、平台安全 MSR、PCIe AER、ACPI 表、CMOS、TSOD、Super I/O、平台可信度、PCI 知识库、PCI BAR、Super I/O 知识库、Wi-Fi BSS、屏幕连接接口、SuperIO HWM、CPU 拓扑、SLIT、PMU。

### 2.3 非自造验证

量测类功能在输出数字之前先验证量测本身：

- 棋类跑分以 perft 叶节点数为检核码。perft 是数学常数，算出别的数字代表这台机器算错了，而不是比较慢；
- 内存带宽换算前以已知大小的负载自我验证，对不上时只输出原始计数器值，不换算成带宽；
- L3 未命中计数同样先自我验证再换算；
- PMU 编程验证逐轮确认写入读回一致与还原完整；
- 统计引擎以已知答案合成样本集逐案例验证分类行为（紧密样本应分类为 High、发散应为 Low、NaN 不得渗漏），不符整场拒收。

### 2.4 突变测试

纯解码器抽成独立类库 `XinSpect.Decoders`（无 WPF 依赖、无特权呼叫），Stryker 突变测试对其执行，分数 82%。突变测试在开发过程中抓到过真实缺陷：`CpuGeneration.DecodeSignature` 的文档声称处理 extended family 进位，实现却漏做——这个缺陷对所有 Family 6 的消费级处理器没有影响，但在 base family 为 0xF 的场合会给出错误的家族判定。知识表数据档（SuperIoKnowledge、PciKnowledge）刻意排除在突变范围外：对查表数据逐条做字符串断言等同快照重复，保护价值低于维护成本。

### 2.5 查不到不等于没有

查询语言与本机 API 对「存在但读不到」的条目照样返回匹配并附原因；语法错误返回 400 与修正指引。把查错伪装成空结果、把读不到伪装成没有，都属于本专案定义的诚实违反。

## 三、系统架构

| 组件 | 形态 | 职责 | 权限 |
|---|---|---|---|
| 曦览主程序 | WPF（.NET 10，单档发布） | 界面、事实收集、对账、报告、深测、超频 | 多数功能不需要；MSR／SMART／Security log 需系统管理员 |
| WinRing0 | 既有核心驱动（反射加载 LibreHardwareMonitor 的 Ring0 模块） | MSR／PCI 配置空间／物理内存 MMIO 读取主力 | 系统管理员 |
| XsRegProbe | 本专案自写的白名单只读驱动（C 源码在 `XsRegProbe/`） | 允许清单内的 MSR／MMIO 读取备援；允许清单外一律拒绝；进程退出即卸载 | 系统管理员；.sys 需用户自行编译签名 |
| Intel XTU 桥接 | net48 独立进程（Release 建置自动内置） | 承载 XTU SDK（.NET 10 已移除的 WCF 旧版堆叠）供 CPU 超频 | 系统管理员 |
| 蓝色中队守护进程 | .NET 10 console（Release 建置自动内置） | ETW 即时威胁侦测、驱动基线比对、以 stdin/stdout JSON IPC 与主程序通讯 | 用户权限 |

**内置机制**：Release 建置时，MSBuild Target `BuildAndEmbedXtuBridge` 与 `BuildAndEmbedBlueSquadronBridge` 分别发布两个桥接程序为单档并内置为资源。运行期首次使用时解压至 `%LOCALAPPDATA%\XinSpect\` 下的专属目录：先以 SHA-256 对内嵌正本验证、目录 ACL 收紧为 SYSTEM＋Administrators（停用继承），验证不过即拒绝执行。这道防线的理由：程序以高权限执行而解压目录在用户配置文件内，若仅以文件大小判断是否沿用既有副本，同一用户的中完整性程序可预先植入同长度的恶意执行文件等待被高权限执行。Debug 建置不内置，改以逐层向上搜寻项目输出。

**事实模型**：全部事实统一为 `HardwareFact`（key、category、name、value、numericValue、unit、source、trust、availability、measuredAtUtc）。`trust` 字段标示可信层级：`Measured`（本工具量到）、`Reported`（系统或固件自述）、`Derived`（由其他事实推导）、`Unknown`（不可用）。时间胶囊、查询语言、本机 API、HTML 报告全部消费同一模型。

## 四、下载与系统需求

| 文件 | 大小 | 用途 |
|---|---|---|
| [XinSpect.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v2.51/XinSpect.exe) | 31,270,187 bytes | 主程序。蓝色中队守护进程已内置 |
| [BlueSquadronBridge.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v2.51/BlueSquadronBridge.exe) | 6,502,948 bytes | 独立守护进程。仅在需要脱离主程序单独运行防护时使用 |

> **本表的字节数为本版（v2.51）实际发布的文件大小**；请以 Release 页面列出的文件为准。

系统需求：Windows 10 1903 或更新、Windows 11 x64；.NET 10 Desktop Runtime（自包含发布则免装）。显卡深测需要 D3D11 兼容设备；MSR 读取、SMART ioctl、Security 事件日志需以系统管理员执行——没有权限时相关项目标示「权限不足」，程序不会假装成功，也不会静默降级。

## 五、快速上手

1. 以系统管理员身份执行 `XinSpect.exe`（不提权也可以执行，多数 usermode 功能照常，特权功能会如实标示）。
2. 左侧导航的「**固件安全**」页：查看 BIOS 写入保护、SMRAM 锁定、交叉对账判决卡；启用「深层存取」可让 SPI 闪存与 PCIe AER 事实翻成真值。
3. 「**存储设备**」页：逐碟 SMART 属性与 failing-now 判定、NVMe 健康与 WCTEMP、HPA 隐藏容量。
4. 「**深测中心**」：设定储存根后按 Quick 或 Full 执行 38 项量测；结果为原始样本并列，不含加权总分。
5. 「**防护**」页：蓝色中队态势评估与即时威胁时间轴；守护进程开关在此页。
6. 「**证据实验室**」：建立时间胶囊（快照），之后可逐栏差分、导出 HTML 报告或原始寄存器快照。

## 六、功能详述

以下依主界面左侧导航的分组与页面顺序说明。每一节先列数据来源，再说明显示内容与边界（不做什么、为什么）。

### 6.1 总览分组

**我的电脑（简易模式首页）**。健康度、温度、硬件清单与故障排查。为不熟悉硬件术语的用户提供单页结论；进阶模式下不列在侧边栏，命令面板仍可搜寻。

**总览**。整机规格与即时状态一览：CPU（型号、核心线程、时脉、负载）、主板（厂商徽章与型号）、内存（容量、通道、速率）、显卡（型号、显存）、存储（容量与活动）。CPU 型号旁展示官方 logo；主板厂商以徽章呈现。

内存插槽配置图依 SMBIOS 的物理排列绘制：实心代表有模块（标容量与速率）、虚线代表空槽。通道字母仅在固件插槽命名可辨识时标示（例如 `ChannelA-DIMM0`）；只有 `DIMM0/DIMM1` 命名的板子会标明「无法辨识通道」。不做推论的理由：猜错通道会导致用户把内存插到错误的插槽。

**瓶颈诊断**。把散在各页的读值合起来回答「现在卡住这台机器的是什么」。指标涵盖：温度墙、功耗墙、单线程、内存、存储、显卡、驱动 DPC、电源策略、MCA 平台事件，依「该先看哪一条」排序。每条判断附产生它的原始数字与对应的深入量测页面链接。未被量测涵盖的项目列于「还没纳入判断的部分」——没量到的数据不会被当成没问题。

**AI 评价**。接 Ollama 或任意 OpenAI 兼容端点，把本机硬件摘要交给语言模型生成评语。提示词可自定义；API 地址与密钥由用户设定。此为全程序少数会产生网络流量的功能之一，仅在用户主动呼叫时发生。

### 6.2 处理器

数据来源：CPUID（usermode）、MSR（经 WinRing0 逐物理核绑定读取）、注册表。

- **规格与拓扑**：家族／型号／stepping（含 extended family 进位——此进位曾因实现漏做由突变测试发现并修正）；指令集支持（含 AVX-512、AES-NI 等）；缓存阶层；die 拓扑（CPUID leaf 0x1F 的 Module/Tile/Die 层级；leaf 未支持时标 NotSupported，不退回 leaf 0xB 推测——0xB 没有 die 层级，推测出来的「单 die」是编造）。
- **微码修订（双来源交叉对账）**：CPU 侧读 MSR 0x8B 高 32 位，逐物理核绑定读取，各核不一致时列出逐核清单而不取单核值冒充全机；Windows 侧读注册表 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\Update Revision`，实测存在 8 字节与 4 字节两种布局，两者都支持；双 DWORD 皆非零的歧义标示「不解码」并附原始 hex。两来源的对照结果由交叉对账规则裁决。
- **TjMax**：MSR 0x1A2 bits[23:16]，单位 °C。
- **温度／频率真相**：MPERF/APERF 比值与 Turbo 阶梯量测，取样前后完整还原计数器。
- **TME／SGX 内存加密**：TME 以 CPUID leaf 7 ECX bit25 判断支持、MSR 0x982（TME_ACTIVATE）bits[3:0] 判断启用、bits[7:4] 解算法（0＝AES-XTS-128、1＝AES-XTS-256、其余未收录如实标）；SGX 以 leaf 7 ECX bit30 判断。平台不支持标 NotSupported。
- **C-state 驻留**：Package 层级的 C2（0x60D）、C3（0x3FC）、C6（0x3F9）、C7（0x3FA）累计驻留微秒。MSR 读取失败或平台未实现时逐项三态。
- **PMU**：能力探索（CPUID leaf 0xA：PMU 版本、每逻辑 CPU 通用计数器数量与位宽、固定计数器数量与位宽）为只读且已出货；编程验证为同意闸门功能——写入范围仅限 IA32_FIXED_CTR_CTRL（0x38D）的固定计数器 0 使能位、以 OR 并入原值（不覆写其他计数器的既有设置）、不触及 PMI 位元（不使能中断即无打断风暴）；每轮「启用→读回一致→已知工作量→计数器活动确认→还原原值→读回确认」，3 轮聚合，任何路径（含例外）以 finally 还原。界面与结果标注「多轮测试・不保证可用」：与 wpr 等参考工具的对照（沙箱方案 S5）尚未执行。
- **NPU 检测**：侦测 Intel／AMD／Qualcomm NPU 并回报驱动与估算算力。

**性能天花板**（同分组的深度页）回答「为什么跑不到该有的频率」：TCC 节流温度、PL1/PL2 功耗墙与时间窗、电流限制（IccMax）与供电警报、Turbo 倍频限制表全部直接读自 MSR——不是规格书数字；限制原因寄存器（MSR 0x1FC）为黏滞纪录位，记录自开机以来撞过的墙；再加用户亲自触发的逐窗撞墙量测（基线／整数／AVX2／AVX-512）以 APERF/MPERF 量有效倍频与作用中核心数，最后归因成一句判决：温度墙、功耗墙、电流墙、供电过热、自主 P-state、多核涡轮上限，或「缺口不在硬件」。全程只读，不写入也不清除任何黏滞位；能量计未通过自我验证时只给原始计数、不换算成瓦。

### 6.3 内存

数据来源：SMBUS（白名单地址直读）、SMBIOS、WMI、ACPI SLIT、Windows 内存 API。

- **SPD 直读**：PCH SMBus 与处理器 iMC SMBus 逐条尝试，读到的每笔标明来源总线；解出制造商、序列号、模块型号、标称与实际速率、主要与次要时序（tCL/tRCD/tRP/tRAS 及子时序）。读不到的总线记原因，不把空插槽当故障。
- **TSOD 温度传感器**：TSE2004 兼容温度（bits[15:4] 二补数 1/16°C），白名单只读地址 0x18–0x1F。
- **ECC 现况**、插槽配置与通道辨识（辨识不出就直说，见 6.1）。
- **Rowhammer**：程序提供风险声明与压力探测，不做正规施测。压力探测在自拥有的连续缓冲区内以两个热点高频读写，xorshift 样本（chunkIndex 混入种子）逐字节验证，热点偏移排除于验证之外；需要用户明确同意（UI 勾选＋服务层对未同意呼叫直接丢例外）；界面与结果均标注「未经过校验」——usermode 无法执行 clflush，此探测不保证触发 Rowhammer，结果不构成内存可靠性的结论。多轮模式（10 轮独立探测聚合）同样标注：多轮零翻转不表示具备 Rowhammer 抗性。
- **NUMA**：拓扑（GetNumaHighestNodeNumber／GetNumaNodeProcessorMaskEx；刻意不走 GetLogicalProcessorInformationEx 的变长结构解析——GROUP_AFFINITY 偏移随 Windows 版本演进，解析错位会把遮罩当事实）、节点距离矩阵（ACPI SLIT：宣告节点数与实际数据不符时拒解）、TLB 与大分页成本量测（同一份乱序指标链在 4 KB 页与 2 MB 大页各跑一次，唯一变量是页面大小；「配不出大页」与「大页没有效益」分开陈述）、跨 NUMA 对照。

### 6.4 主板

数据来源：SMBIOS（MSSmbs_RawSMBiosTables）、ACPI（GetSystemFirmwareTable）、Super I/O 设置端口 0x2E/0x4E、PCI 配置空间（WinRing0）、CMOS 端口。

- **机箱开启侦测**：SMBIOS Type 3（System Enclosure）offset 12 的 Security Status。值 5 表示固件记录了机壳开启事件——这是拆机的固件级证据，界面以警示色呈现并建议追问来源。offset 5 的机箱类型（Tower／桌面／Rack Mount 等）一并解出。
- **BIOS／芯片组**：版本、日期、厂商。
- **Super I/O**：0x2E/0x4E 设置端口以两种进入序列尝试，只读芯片 ID 与厂商 ID（名称对照取自 coreboot superiotool 的 ite.c/nuvoton.c，出处标明），设置模式在任何路径（含例外）以 finally 退出——把芯片留在设置模式是系统风险。HWM 传感器：选 LDN 4（环境控制器）取基址后，以 base+5（index）／base+6（data）读取——风扇 RPM（ITE 公式 1,350,000÷(divisor×count)，count 0/0xFFFF 为无效，不回 0 RPM）、温度（8-bit 二补数）、电压（LSB 16 mV，标明「未经主板分压校准」——芯片端读值不等于实际电压）。
- **PCI Bus 0 盘点**：三十二槽扫描，多功能位元决定 function 扫描深度；PCI-SIG 类别码知识库转角色名称（未收录标「未收录」）；每设备列出 BAR 地址与类型。BAR 大小需写入探测（写全 F 读遮罩），本工具对 PCI 配置空间只读，故不出大小。
- **ReBAR 实况**：查 Windows 实际指派给设备的内存范围——那是生效的窗口而非能力宣称值。刻意不走 PCI 配置空间（ReBAR 能力结构在 0x100 之后，传统 CF8/CFC 机制到不了），也不需要驱动或管理员权限。覆盖率只当事实列出：BAR 尺寸是 2 的次方，12 GB 的卡最大只拿得到 8 GB 窗口，属规格使然，不以此扣分。
- **SPI 闪存稽核（三层）**：第一层寄存器旗标——HSFSTS 的 FLOCKDN/WRSDIS/FDOPSS（只报位元不判决）、FRAP 的 BIOS 区写入权限、PR0–4 保护范围；第二层 FREG 地图推导闪存总大小与 BIOS 区位置；第三层 BIOS 区 SHA-256（单次大读优先，失败退 4 KB 分块，标明「哈希＝可读面」）；可与用户提供的参考映像逐 4 KB 块比对（大小不符诚实拒比）。全部需驱动，未加载时带已知 SPIBAR 地址三态。
- **ACPI 表列**：表头校验和验证；MCFG（PCIe ECAM 基址与总线范围）、HEST（硬件错误源表）、BERT（开机错误记录区）、SLIT（节点距离矩阵）、CEDT（CXL 固定内存窗口，CFMWS 逐栏：BaseHPA／WindowSize／InterleaveWays）、HPET（计时器存在）、FADT（PM timer 区块地址）。平台没有对应表时标 NotApplicable——无此硬件不是错误。
- **CMOS/RTC**：VRT 电池电压（0x0D）、即时时钟（BCD 与 12/24 小时解码）、世纪字节、PC-AT 校验和；地址 0x70 的 bit7 保留 NMI，绝不写 0x71。
- **UEFI 开机设定**：Secure Boot 四态、AuditMode、DeployedMode、SetupMode（GetFirmwareEnvironmentVariableEx，SeSystemEnvironmentPrivilege 启用含 ERROR_NOT_ALL_ASSIGNED 检查）。
- **POST 代码**：I/O 0x80 读取，0xFF 与 0x00 不解码。

### 6.5 存储设备

数据来源：SMART_RCV_DRIVE_DATA ioctl（disk.sys 代理——刻意不用 ATA_PASS-THROUGH，曾实测卡 IRP；由磁盘类驱动代理的 SMART 通路久经验证且无弄挂用户磁盘的风险）、NVMe Storage Query Property、WMI Win32_DiskDrive。

- **SMART 属性与门槛（failing-now）**：READ DATA（features 0xD0）取 30 笔属性（ID、现值、最差、原始六字节）；READ THRESHOLDS（features 0xD1）取门槛表。「现值 ≤ 门槛且门槛非 0」判定为现正低于门槛（failing now），逐项摊开并标注「现值 X ≤ 门槛 Y」。门槛 0 依 SMART 规范为无门槛，不评比。重新配置扇区（0x05）、待对映扇区（0xC5）、无法修正扇区（0xC6）另有健康裁决。
- **NVMe**：健康记录（log page 0x02）全解——合成温度、可用备用、已用寿命百分比、数据单位读写量；WCTEMP 警告（Identify Controller offset 0x14A 的 u16 对照合成温度，达标即警告；0＝未提供如实标）；错误记录（log page 0x01）；电源状态表（各阶功耗与进入／离开延迟）对实测「刻意闲置 N 毫秒后第一笔 4K 读取要多久」——闲置后第一笔为什么慢，两者同一量级才敢归因于省电状态。
- **HPA 隐藏容量**：ATA IDENTIFY 的最大 LBA（words 100–103）对照 Win32_DiskDrive.Size——OS 可见少于固件声明即 HPA 作用中，换算隐藏扇区数与 GB。这是翻新碟与容量窜改的直接证据。DCO 需厂商私有命令（无公开 usermode 通路），标 NotSupported 并说明原因。
- **磁盘表面扫描**：顺序读取逐块（默认 1 MB，上限可调）量延迟，超过 100 ms 标慢、读取失败标错误。SMART 是固件的自述，这是程序自己读到的——两者互补。以 `\\.\C:` 开启逻辑卷不需管理员权限，仅能读不能写。
- **假容量写入验证**（H2testw 式）：在目标磁盘以可重现样本（xorshift，chunkIndex 混入种子）写入至指定上限或写满，FlushToDisk 后读回逐字节验证，结束删除暂存文件。「写不进去／读回不一致」是假容量卡（标 512GB 实为 8GB 的刷板卡）与劣化碟的直接证据。需要明确同意——写入量可观且可能加剧濒死媒体损耗，界面与说明均标注风险。此功能未接入任何自动流程，仅在明确呼叫时执行。
- 通电时数与机龄推估（SMART Power-On Hours＋出厂日期启发式）、QD 梯度性能曲线、容量／固件／序列号、NVMe 识别数据。

### 6.6 显卡

数据来源：NVML（NVIDIA Management Library）、NVAPI、D3DKMT、EDID、注册表。

- **NVML**：温度、频率、功耗（毫瓦）、风扇、温度阈值、**退休页**（nvmlDeviceGetRetiredPages_v2，single-bit 与 double-bit ECC 合计——NAND 瑕疵退休计数；本测试机实测该卡不支持退休页报告，标 ReadError 并写明原因）。
- **GPU 深测**：光栅填充率（全屏幕三角形＋取样）、纹理取样（单／八取样）、H.264 编码（Media Foundation SinkWriter）、计算管线（D3D11 buffer SRV）。所有 GPU 输出经 readback 对 CPU 参考值逐字节比对——防止全零输出被误判为有效量测。
- **HDR 能力**：EDID CTA-861 HDR Static Metadata（亮度范围与支持旗标）＋DisplayConfig 查 Windows HDR 开关；读不到标未知不猜。
- **显示链路真相**：连接技术、实际像素时钟、色彩编码（RGB/YCbCr 4:4:4/4:2:2/4:2:0）、位元深度、所需影像数据率。频宽不足时驱动会自行降色度，Windows 设置界面照样写着 4K144——这里列出的是实况。
- **TDR 设置**：TdrLevel/TdrDelay/TdrDpcDelay 注册表值；未设置时标明 Windows 默认值（Level 3、Delay 2 秒），不把「未设置」说成「已设置」。
- **超频**：NVML 功耗／风扇／温度监控＋NVAPI 时脉偏移；铁律为快照→回读验证→看门狗→自动还原。

### 6.7 网络

数据来源：WMI（MSFT_NetAdapterStatistics、Win32_NetworkAdapter、MSFT_NetAdapterChecksumOffload/RSS）、wlanapi、注册表。

- **接口统计**：错误与丢弃计数非零的接口逐条列出（RX/TX 错误、RX/TX 丢弃）——驱动劣化、线材、交换器端口问题的第一手指纹。全部归零明确标示「干净」，与「读不到」分开。
- **MAC OUI 对照**：IEEE 登记的知名前缀子集（Intel、Realtek、ASUS、GIGABYTE、Microsoft Hyper-V 虚拟、VMware 虚拟、Apple、NVIDIA 等），未收录标「未收录」、格式坏标「无法解析」。
- **Wi-Fi**：RSSI（附解读文字：-30 极佳到 -85 微弱，中间值不硬贴等级）、频道（WLAN_BSS_ENTRY 的 ulChCenterFrequency 依 IEEE 802.11 Annex E 换算——2.4 GHz 等差、ch14 特例、4.9/5/6 GHz 各自公式，频率在等差之外回 null 不猜）、认证类型（WPA2/WPA3 Enterprise/Personal 等）、BSSID。接口未连线时如实列示「未连线」，不与「无接口」混报。
- 网络卸载实际启用状态（Checksum Offload、RSS——防御式属性读取，属性未提供标「属性未提供」不冒充停用）、网卡高级属性、网速测试、网络延迟量测。

### 6.8 安全（固件安全页＋防护页）

数据来源：MSR、PCI 配置空间、UEFI 变量、WMI（Win32_DeviceGuard、Win32_Tpm）、X509Store、事件记录、注册表、用户提供的清单档。

**固件安全寄存器**（逐项下裁决，不利裁决以警示色呈现）：

- BIOS_CNTL（0xDC）：SMM_BWP、BLE、WE——BIOS 写入保护综合裁决；
- SMRAMC（0x88）：D_LCK、D_OPEN——SMRAM 锁定状态；锁定下 D_OPEN=1 的非法组合由对账规则抓出；
- ME 状态（HFSTS1）：先验 PCI 0:16.0 为 Intel HECI 才解读；
- IA32_FEATURE_CONTROL（0x3A）：Lock 与 VMX 位；
- IA32_DEBUG_INTERFACE（0xC80）：ENABLE／LOCK／DEBUG_OCCURRED（鉴识线索：本机曾被调试器附着）；
- SPI 旗标与区域权限（见 6.4）。

**交叉对账判决卡**：26 条规则（外部化于 `Rules/builtin.json`，每条附 SpecRef 与误报条件，可直接分享）对同一批事实做语义与管线一致性检查——硬件语义族（微码、SMRAM、UEFI 状态机：SB=1 而 SetupMode 密钥未部署等）、管线一致性族（BIOS_CNTL↔写入面、FRAP↔暴露面、SPI↔MMIO 后端、MSR↔后端、微码 Windows↔CPU）、来源交叉族（Secure Boot 双来源、UEFI 变量 vs 注册表平台）。判决三种：一致（绿）／矛盾（红，具名列出双方）／无法验证（灰，任一输入缺席）。实机曾抓到真矛盾：SMM_BWP=1 但 SMRAM 锁定交叉不符。

**平台可信度**：hypervisor 存在位（CPUID 1 ECX bit31）与签名（0x40000000，仅在位 31 为 1 时读取——不支持的叶会回最大标准叶的内容并解出假厂商）、VBS/HVCI（Win32_DeviceGuard：「已设定」≠「执行中」，只有 Running 才生效）、核心代码完整性选项（NtQuerySystemInformation 103）、Invariant TSC。VBS 执行中时明确告知：Windows 本身是 Hyper-V 上的一个分区，MSR 可能被拦截、遮罩或回虚拟值，MSR 类卡片只能当参考。

**BYOVD 逐驱动比对**：加载中核心模块对微软「建议的驱动程序封锁规则」比对。清单 XML 由用户提供（自微软文档下载后放入程序目录 `Rules\byovd-blocklist.xml` 或以 CLI 指定），零网络存取；比对两道——文件名（不分大小写）与 SHA-256/SHA-1（对实文件计算，哈希缓存）。命中列出模块路径与规则依据；语义是「在微软建议封锁清单上的攻击面事实」，不是中毒判决——这些驱动多半是厂商正常工具（超频、灯效、诊断），问题在于其任意读写能力可被滥用。真正的阻挡要靠 Windows 内建的弱点驱动程序封锁清单。

**安全鉴识**：

- Defender 排除清单（MSFT_MpPreference 的 ExclusionPath/Process/Extension）逐条摊开——排除就是「扫毒永远不看这里」；零排除明确标示「扫毒涵盖完整」；
- 事件记录清除侦测（Security log Event 1102）：历史清除次数与最近一次时间；Security log 需提权，权限不足与查询失败分开标示；
- 非微软本机信任根：X509Store（Root／LocalMachine）中主体不含 Microsoft 的凭证——Superfish 一类 MITM／监控凭证的风险面，列出主体与有效期供判读，不下中毒结论；
- USBSTOR 使用痕迹：`Enum\USBSTOR` 下的设备安装记录（含早已拔除的）；
- 驱动签名稽核：Win32_PnPSignedDriver 的 IsSigned，未签名逐条。

**蓝色中队（防护页）**：六防线即时安全态势评估——DMA 与内存保护、固件与启动链、CPU 缓解、存储与数据、驱动与对抗、系统攻击面，每防线有分数、严重度与摘要；加强建议逐条列出。ETW 即时威胁侦测时间轴（驱动加载、可疑进程）。守护进程已内置主程序（见系统架构节），安全页滑动开关控制启停并记入设置档；关闭时只读态势评估照常。

**深层存取豁免开关**：产生自签 CA（RSA-4096、CA=TRUE，存于 ACL 限定的 ProgramData 路径）→ 装入 Root＋TrustedPublisher（只放行这一张，不开全机 test-signing）→ 安装并启动 XsRegProbe 服务。关闭＝停止并删除服务、只移除自己 CA 的 thumbprint（找不到凭证档就不扫库误删）。非提权时不做任何变更。启用后 SPI 闪存、PCIe AER 等驱动依赖事实免重启翻成真值。

### 6.9 系统与软件层（事实实验室的软件面）

数据来源：WMI、注册表、事件记录、LSA、COM（WUA）、psapi。

- **Windows Update 历史**：WUA COM（Microsoft.Update.Session）——最新一笔（标题＋安装日期）、总笔数、近 30 天安装数、失败／中止计数含最近一次标题；无日期不猜，空历史是「0 笔」不是读不到；COM 不可用（服务未启动）三态。
- **服务盘点**：Win32_Service——总数、执行中、自动启动、停用、**非系统目录服务**（执行文件路径不在 \Windows\ 下，引号感知解析，例举前三名；这是第三方常驻面，数量本身不下安全结论）。
- **事件记录摘要**：System log 反向走访 7 天内严重＋错误，最常见「来源 事件ID×次数」前 3；读到 7 天外即停不整表扫；导出 canonical JSON。
- **稽核政策**：LSA LsaQueryInformationPolicy（PolicyAuditEventsInformation，只读）——稽核总开关与九类别等级逐类描述（0 未设定不列、规范外等级如实标「等级 N」）；x64 结构手算偏移。
- **机器原则档**：Registry.pol 的存在与最后写入时间指纹——只指纹不解析二进制；无档标 NotSupported（可能从未被网域或本机群组原则下过设定），不是错误。
- **选用功能**：Win32_OptionalFeature——Hyper-V Hypervisor／虚拟机平台／WSL／容器四目标的启用／停用／不存在（照抄系统 InstallState 口径）；**WMI 没回报的功能标「未回报」，不等于停用**。
- **核心模块加载清单**：psapi EnumDeviceDrivers——\Windows\ 下模块只计数（签名面由驱动稽核的 Win32_PnPSignedDriver 涵盖）；**非系统目录的加载模块逐档 wintrust Authenticode（DRIVER_ACTION_VERIFY）**——未通过带原始 NTSTATUS 码、文件不存在等无法验证者如实列名，通过者只计数。
- **开机参数**：SystemStartOptions 原样解析——核心调试（DEBUG／DEBUGPORT）、测试签名（TESTSIGNING）；没有关键字＝「未启用」（Present 的没有），整个读不到才是 ReadError；原始字符串全文附上可稽核。
- **开机计时**：Diagnostics-Performance Event 100 的 BootTime（毫秒，Windows 自己量的，只解读不评级）；最近开机＝Win32_OperatingSystem.LastBootUpTime。事件缺席＝NotSupported、查询失败＝ReadError，两种「没有」分得清楚。
- **USB 拓扑**：Win32_USBControllerDevice 依赖对——控制器数、设备数、最忙碌控制器（供电与频宽冲突排查指纹）；WMI 依赖对只有一层，更深 hub 树不猜。
- **摄影机列举**：PNPClass Camera/Image 逐台名称与状态照抄系统口径——「存在但状态 Error」与「不存在」是两回事。
- **企业存储**：iSCSI（MSiSCSI 服务状态）、MPIO（服务存在与否）、FC HBA（WMI root\wmi）、NVMe-oF（没有公开侦测 API，标「侦测路径未实现」）——无此硬件 NotApplicable、侦测路径不存在 NotSupported，两者都不是错误。

### 6.10 交叉对账引擎

26 条规则分三族：

- **硬件语义族**：微码一致性（Windows 说的 vs CPU 说的）、SMRAM 锁定与 D_OPEN 的非法组合、UEFI 状态机（Secure Boot=1 而 SetupMode 密钥未部署＝状态机警讯；Audit×Deployed 互斥）；
- **管线一致性族**：BIOS_CNTL↔BIOS 写入面、FRAP↔BIOS 暴露面、SPI 控制器↔MMIO 后端、MSR↔MSR 后端、MCHBAR 基底↔主机桥盘点、TjMax↔MSR 后端、HVCI↔环境裁决、SPI 哈希↔地图、哈希↔MMIO 后端、PCIe AER 扫描↔ECAM；
- **来源交叉族**：Secure Boot 双来源（UEFI 变量 vs 注册表）、UEFI 变量存在 vs 注册表否定。

方法学备注：涉及「A 缺席」且两侧都可能缺席时，单条规则无法两向涵盖（引擎守卫把缺席键转 Unverifiable）——拆成两条方向性规则，各宣告保证 Present 的一侧。管线规则的「后端事实缺席＝矛盾条件」者刻意不列输入键，规则内 TryLookup 自行裁决。规则以 JSON 外部化，每条附 SpecRef 与误报条件；行为等价定义为 Relation 层逐条逐情境一致（68 情境），由测试钉住。

## 七、深测中心（38 项 Run Session）

深测中心是一场 Run Session 的集中量测界面：选择 Quick 或 Full 档案、指定储存根与暂存预算，逐项执行并即时回报进度。**阅读界线**先讲清楚：只并列各项量测的原始样本、可信度（High/Medium/Low/Insufficient）与限制，不加权合成单一总分；跨域、跨数据体的排名不成立（GPU 的 200 分和磁盘的 200 分没有共同单位）。混合不同量测配置的样本会被混池判定拦下——梯子型指标（STREAM kernel×threads、缓存工作集、磁盘 QD ladder）各点是不同配置，平均无意义，混池时改逐点列。38 项全量登记，可执行与延后项目都会摊开，不把未跑的说成量测。

测项全表（依目录顺序，节录）：

| 测项 | 内容 |
|---|---|
| cpu.aes-sha | AES-NI 与 SHA-NI 吞吐 |
| cpu.load-use / ilp / branch | Load-to-use 延迟、ILP、分支延迟与误预测 |
| cpu.branch-matrix | 分支式矩阵乘法 |
| cpu.rdrand-rdseed | 乱数指令吞吐 |
| cpu.pmu-topdown | Intel PMU Top-down 管线归因（只读取样） |
| cpu.core-latency | 核心间延迟矩阵 |
| cpu.core-to-core | 核心到核心搬运频宽 |
| cpu.smt-sibling | SMT 兄弟线程竞争 |
| cache.bandwidth / latency | 缓存阶梯频宽与延迟（L1/L2/L3/DRAM） |
| cache.lock-scaling | lock 前缀指令的扩展性 |
| numa.* | NUMA/TLB/大分页对照（TLB working-set 扫描以互质 stride 逐页扫） |
| memory.dram-mapping | DRAM 地址映射推论（顺序 stride 走访曲线，仅推论不宣称） |
| memory.bandwidth | 内存频宽（多线程 STREAM） |
| memory.numa-tlb-largepage | 三子项独立判定（本机 Administrator 无 SeLockMemoryPrivilege 时大分页项如实标未执行） |
| ux.synthetic-workloads | 合成 JSON 往返、SHA-256、数据转换序列 |
| gpu.raster-texture | D3D11 全屏幕三角形填充率＋单／八取样纹理（readback 对 CPU 参考色逐字节比对） |
| gpu.codec-throughput | Media Foundation H.264 编码（内存内，不摄影） |
| gpu.compute-pipeline | D3D11 计算管线（buffer SRV） |
| storage.composite | 暂存档 FlushToDisk→读回→GPU FNV-1a→回读与 CPU 逐元素比对 |
| storage.qd-ladder | 磁盘 QD 梯度 |
| storage.io-gpu-pipeline | 存储→GPU 管线 |
| audio.wasapi | WASAPI 信号级 |
| gauntlet.multi-domain | CPU／内存／存储三域并行分窗取样＋early/late 保留率 |
| confidence.engine | 统计引擎自我稽核（7 个已知答案合成样本集逐案例验证，不符整场拒收） |

高负载警告：执行期间 CPU／内存／GPU／存储测试会建立 `XinSpect.deepbench.tmp` 与 `XinSpect.loop.tmp`，不触既有文件；结束、例外或取消都会删除，启动前会检查剩余空间。深测历史只保存在本机设定数据夹，不上传。

## 八、监控与量测工具

**传感器**。LibreHardwareMonitorLib 引擎，所有传感器的完整明细总表：温度、时脉、电压、风扇、负载。迷你悬浮窗口与系统匣模式；超标警示（可设定门槛，横幅＋气泡通知）；CSV 导出；每秒更新。

**历史回放**。数周的温度／负载走势回放与统计。

**健康**。温度／负载／容量汇整的健康总评，含磁盘表面扫描入口。

**性能**。跑分（棋类 perft 检核——中国象棋／西洋棋的叶节点数是数学常数，算出别的数字是这台机器算错了）、烤机、缓存延迟、磁盘性能。

**算力图**。CPU／内存／GPU／存储／NPU 各维度算力视觉化。

**绘图管线测试**。WPF 绘图管线性能：填充率、3D 几何、文字渲染；走软件光栅化，量的是 WPF 管线而非 GPU 硬件——与深测中心的 GPU 深测（D3D11）互补。

**帧时间监测**。任何程序的真实帧时间与 1% Low。ETW 事件收数，不注入目标程序。

**DPC 延迟**。排出造成音频爆音／输入停顿的肇事驱动（ETW）。

**线程迁移**。线程在核心之间弹跳的频率与每一跳丢掉哪一层缓存。ETW Context Switch 事件（零驱动、需管理员），记下每条线程上次落在哪颗核，换核即一次迁移，依拓扑四层归因：同物理核心（SMT 兄弟，L1/L2 都在）、同末级缓存内换核、跨末级缓存（L3 要重拉）、跨 NUMA（内存变远端）。边收边累加不留原始事件，并扣掉量测自身线程。行程排名依迁移率而非绝对次数。刻意不下判决：迁移多寡取决于工作型态，不是缺陷。

**隐形停顿（SMI）**。系统管理中断的次数与封装／核心 C-state 驻留。SMI 由固件在 OS 看不见的模式处理，工作管理员恒为 0% 但音频会爆；硬件只留下次数，没有每次待了多久，所以只给频率，不乘一个猜出来的耗时。

**NVMe 电源状态**。碟宣告的电源状态表（各阶功耗、进入与离开延迟）对实测「刻意闲置 N 毫秒后第一笔 4K 读取要多久」。两者同一量级才敢归因于省电状态。

**Resizable BAR 实况**。显卡的内存窗口被撑开了没有。ReBAR 要 BIOS 开、驱动支持、CSM 关掉、纯 UEFI 开机、开机碟是 GPT，缺一个就不生效，而 Windows 没有任何地方告诉你现况。程序问 Windows 实际指派给设备的内存范围——生效值而非能力宣称值，不需驱动或管理员权限。

**大页与地址转换成本**。同一份乱序指标链、同样大小的工作集，在 4 KB 页与 2 MB 大页上各跑一次；唯一变量是页面大小，两者的差就是走表代价。配不出大页与大页没有效益分开讲。

**L3 未命中与 DRAM 实际流量**。架构效能事件数出真的去了内存几次，先以已知大小负载自我验证；对不上只给原始计数，不换算成频宽。

**算力图／NPU 检测**。见 6.2 与 6.7。

## 九、证据实验室

**时间胶囊**。全机事实快照：所有来源的事实并入单一 JSON（3094 项测试背后的事实模型），附 SHA-256 完整性信封（canonical UTF-8 JSON 逐字节哈希）与匿名机器识别。敏感值默认遮蔽；「保留敏感值」必须由用户主动指定。跨快照逐栏比较：不同机器（匿名识别不同）直接拒比，避免把两台机器的差异误认成硬件变更；变更分四种（新增／消失／变更／未变），其中**某来源这次读不到会明确列为消失，不以旧值填补**。资产生命周期事件自动分类：差分结果按 key 前缀映射到资产类（内存 mem./spd.、处理器 cpu.、显卡 gpu.、存储 disk./nvme./smart.、主板 board.），其余变更照列但标「状态」——固件设定变了不是硬件变了，不冒充。比较动作写入审计日志（哈希链：Sequence 连续、PreviousHash 串接、逐笔重算，改中间一笔必失败）。

**原始寄存器快照**（.xinraw）。PCI 安全寄存器、ACPI 整表、MSR、SPIBAR、MCHBAR 的原始字节；SHA-256 信封，载入时逐字节验完整性——被窜改或损毁的文件拒载。挥发位元（SMI 计数、DEBUG_OCCURRED、HSFSTS 状态）标注为挥发。raw 不匿名化，分享前请自行确认内容。

**HTML 报告**。单档自足（无外部资源）、五特殊字符自订转义（WebUtility.HtmlEncode 会把 ° 转数字实体破坏可读性，故自写）、读不到／警示列样式、尾端 SHA-256 标记——`Verify()` 离线重算，哈希盖「值挖空的完整文档」。

**corpus 贡献包**（格式 v1 骨架）。只接受遮蔽版快照、身份键逐键排除；上传通路刻意未实现——重开前需四项条件：逐次同意、本地预览、数据主权声明、服务器端不改写。

## 十、风险与同意闸门

下表列出所有涉及写入或潜在风险的操作。除此之外的功能全部只读。

| 功能 | 写入内容 | 防护机制 |
|---|---|---|
| Rowhammer 压力探测 | 自拥有缓冲区内高频读写 | 同意闸门（UI 勾选＋服务层对未同意呼叫丢例外）；「未经过校验」标注；无自动接线 |
| 多轮压力模式 | 同上，10 轮独立探测 | 同上；明写「多轮零翻转不表示具备 Rowhammer 抗性」 |
| PMU 编程验证 | IA32_FIXED_CTR_CTRL 使能位（OR 并入原值，不碰 PMI 位元） | 同意闸门；每轮 finally 还原原值；「多轮测试・不保证可用」标注；S5 对照未执行 |
| 假容量写入验证 | 目标磁盘大量写入（至上限或写满） | 同意闸门；危险声明（加剧濒死媒体损耗）；结束删档；无自动接线 |
| 磁盘表面扫描 | 无（纯读取） | 仅读取；可取消；逻辑卷不需管理员 |
| 深层存取开关 | 安装自签 CA 与驱动服务 | 非提权拒做；关闭完全移除；不开全机 test-signing |
| CPU／显卡超频 | 倍频／电压／时脉偏移／功耗／风扇 | 快照→回读验证→看门狗→自动还原 |
| 系统风扇调速 | Super I/O 风扇寄存器 | 一键还原自动；与传感共用同一引擎 |
| 系统引导修复 | SFC/DISM/CHKDSK 执行 | 执行并记录输出；命令为微软官方工具 |

## 十一、CLI 与自动化

```
XinSpect.exe --json evidence [--query <查询>] [--out <文件>]
XinSpect.exe --compare-flash <参考镜像> [--out <文件>]
XinSpect.exe --verify-audit [日志路径] [--out <文件>]
```

退出码：0＝全部事实 Present；2＝部分事实为三态；1＝致命错误。CLI 在单一实例逻辑之前分支——不建具名信号、不触发多开对话框，适合脚本与排程。

查询语言：一行一子句；`#` 开头为注解；值含空格吃到行尾。字段七种（key／category／source／value／availability／since／until），运算子三种（`=` 精确、`~=` 子字串包含不分大小写、`^=` 前缀）；availability 支持别名（ok／error／no-permission）。子句之间为 AND。语法错误丢 ParseException 带修正指引。范例：

```
key ^= spi.
category = 固件安全
availability = error
since = 2026-10-01
value ~ FLOCKDN
```

PowerShell 模块 `XinSpect.psm1`：`Get-XinSpectEvidence` 封装 CLI 呼叫（文件必须 UTF-8 BOM，机器检查守住）。

本机 API handler：`GET /api/facts` 返回全部事实（canonical JSON，与时间胶囊同一形状）；`POST /api/query` 接受查询语言全文（单一含点 token 兼容旧前缀语意；无运算子的 nonsense 输入回 400 带修正指引）。设计为 loopback 只读、匿名机器识别；HTTP 监听壳刻意不启动（判定登记表有决定与条件：开监听＝新增本机攻击面，属用户决定）——headless 的出口由 CLI 承担（--json evidence），审计日志的 App 外验证走 --verify-audit。

## 十二、公开规格与文档

| 文件 | 内容 |
|---|---|
| `docs/spec/snapshot.schema.v1.json` | 时间胶囊 JSON Schema（2020-12）：顶层与 fact 定义、三态与可信度列举；numericValue/unit 条件忽略成文。与实际序列化逐键机器对账 |
| `docs/spec/query-language.v1.md` | 查询语言 v1 规格：七字段三运算子、别名表、「查不到≠没有」语意、ParseException 拒静默 |
| `docs/spec/METHODOLOGY.md` | 事实来源契约：诚实契约五条、量测路径五层、交叉对账、突变测试、性能预算 |
| `docs/MEASUREMENT-METHODOLOGY.md` | 量测方法学：量不到是三态不是零、混池禁令、单调钟与墙钟分工、临界值成文、flaky 处理 |
| `docs/spec/LIMITATIONS.md` | 公开限制：七项设计裁决＋六项能力边界＋两项环境依赖 |
| `docs/EC-RISK-ASSESSMENT.md` | EC 埠存取风险评估：交易交错、burst 破坏、症状延迟显现；结案裁定不实现，重开条件成文 |
| `docs/PMU-SANDBOX-PLAN.md` | PMU 编程沙箱验证方案 S1–S5（最小写入回读、已知工作量核对、三次清除无残留、与 wpr 并行不干扰） |
| `docs/DATA-SOVEREIGNTY.md` | 数据主权声明：数据位置、零网络 API 机器检查、匿名化、删除即终结 |
| `docs/DEPLOYMENT.md` | 部署与企业维运：发布形态、CLI、数据位置、升级回滚、HVCI 限制 |
| `docs/ITERATIONS.md` | 完整迭代账本：60+ 批次、逐轮内容、测试数、提交哈希 |
| `docs/ROADMAP-G6.md` | G6 路线图与诚实界线（EC 直写、Rowhammer、PMU 各有闸门） |
| `docs/feature-backlog.md` | 110 条候选功能的盘点 |

## 十三、品质保证

- **单元测试**：3094 项，全部通过。涵盖每个解码器的金标向量（手算字节级）、每个服务的三态行为、每条对账规则的逐情境行为（68 情境）、每个查询语言子句。
- **突变测试**：Stryker 对 `XinSpect.Decoders` 类库执行，分数 82%。曾抓到「文档说有 extended family 进位、实现漏做」的真 bug。
- **SpecRef 覆盖**：反射检查已注册解码器的每个公开方法都有规格引用（文档、章节、寄存器、位元）。
- **Property 测试**：FsCheck 对遮罩差分永不入列性质、MCFG 编码往返等做随机验证——曾当场抓到遮罩位元心算错 0xA007 vs 0xA807。
- **差分测试**：SMBIOS 内存解码与微软 Win32_PhysicalMemory 对照（同表双实现：模块数、总容量、标称与实际速度）。fixture 过期（换 RAM）会红，讯息已写明先查 fixture。
- **文档对账**：README 版号、Changelog、csproj 三处一致由测试把关；规格文档与解析器的漂移会红灯（PublicSpecTests、DataSovereigntyTests 扫描源码）。
- **金标向量的实绩**：TPM rc 偏移两次写错靠金标抓回；IPMI SEL Generator ID 是 u16 被长度检查抓回；TPM2 大端序 count/eventSize 在 offset 11/49——测试向量写错两次靠测试互抓。

## 十四、隐私与数据主权

- **事实搜集与解码层零网络 API**：`DataSovereigntyTests` 扫描 Services 与 Decoders 的全部源码，HttpClient/TcpClient/UploadString 等出现即红灯。全程序仅有的网络功能：AI 评价、意见回馈、网速测试、网络延迟量测——全部需要用户主动触发，且都在允许清单内（清单与数据主权文档同步钉死）。
- **数据仅存本机**：审计日志 `%ProgramData%\XinSpect\Audit\audit.json`（哈希链，可离线验证）、驱动凭证 `%ProgramData%\XinSpect\Driver`（ACL 限 SYSTEM/Administrators）、时间胶囊与原始快照在用户指定路径。删除文件即完成删除，没有云端副本、没有备份同步、没有远端残留。
- **匿名化**：机器识别为单向哈希派生（`sha256:`＋64 hex），不含序号原文。敏感值默认遮蔽，保留必须用户主动指定。corpus 贡献包只收遮蔽版，身份键比 Sensitive 旗标更严地逐键排除。
- **自我遥测**默认关闭、匿名、只记数字与时间、本机存取、环形上限 500。
- **审计日志**只记中继数据（动作、范围、结果摘要、哈希），不记事实内容；不上传云端、不做区块链。

## 十五、已知限制

- 突变测试仅涵盖纯解码器类库；主程序的特权通路层由注入式测试与金标向量承担，无法突变测。
- PMU 编程验证尚未与参考工具对照（沙箱方案 S5 未执行），标「多轮测试・不保证可用」。
- Rowhammer 压力探测非保证触发（usermode 无 clflush），不能作为内存可靠性的结论。
- DCO 需厂商私有命令，未实现。
- MCHBAR 内存时序（tCL/tRCD/tRP/tRAS）属 MRC 训练结果区，Intel 公开文档未定义，不出值——这是「不是待办遗漏」的设计裁决。
- PCH 世代名称对照未对准出处前不出值。
- Wi-Fi 连线态字段需接口实际连线；未连线时如实标示。
- HVCI／VBS 开启时 MSR 可能被拦截或回虚拟值，可信度裁决在平台可信度页明示。
- ATA_PASS-THROUGH 刻意不用（曾实测卡 IRP）；SMART 走 disk.sys 代理的 SMART_RCV_DRIVE_DATA。
- IPMI 带外管理通路（KCS/SSIF/NCSI）与 MegaRAID BBU/VD/PD 布局未验证——本机无硬件可验证的通路不出货，只出讯息解码。
- EC 埠存取结案不实现（见风险评估文档）。
- 规则市集（需签名信任模型）与二手平台 API、SDK/模拟器（M9/M10）刻意不做。

## 十六、从源码建置

```
git clone https://github.com/Xinglanclever/XinSpect.git
cd XinSpect
dotnet build XinSpect.csproj -c Release
dotnet test Tests/XinSpect.Tests.csproj
dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

- Release 建置会自动发布并内置 XTU 超频桥接（net48）与蓝色中队守护进程；Debug 建置不内置以缩短建置时间（开发环境由 BlueSquadronBootstrap 逐层向上搜寻项目输出）。
- 测试命令建议加 `-p:BaseOutputPath=obj/_verify/`——若用户正在跑已发布的 XinSpect.exe，apphost 文件锁会挡默认输出路径。
- XsRegProbe 驱动：`XsRegProbe/` 内为 C 源码与建置手册（`BUILD-给使用者.md`），需 WDK 编译并签名（窄路自签 CA 即可，与深层存取开关同一流程）。
- 项目结构：`XinSpect/`（主程序）、`XinSpect.Decoders/`（纯解码器类库，突变测试对象）、`Tests/`（单元测试）、`BlueSquadron/`（守护进程）、`Bridge/`（XTU 桥接）、`XsRegProbe/`（驱动源码）、`docs/`（规格与账本）、`Rules/`（外部化对账规则）。

## 十七、版本沿革

- **v2.51 Olympus**（2026-10-10）：写入闸门最小版——全库 17 个硬件写入呼叫点（PMU 编程、RDT、DRAM 流量、TopDown、SMN、SMBus、CMOS 端口…）全部经过 WinRing0Bridge 三个写入 API，闸门因此在底层：每次 MSR／PCI 配置空间／I/O 端口写入自动记账（时间、目标、呼叫者、写入值、成败）。同意闸门（用户点头才跑）不变，这里补的是「点头之后到底写了什么」可回答——CLI --json evidence 输出新增 writeAudit 段。守门：源码断言桥接三个写入方法各自记账＋Services/ 内不得有第二组 Ring0 原生写入入口。测试 +4（3661 绿）。
- **v2.50 Olympus**（2026-10-10）：建置警告清零＋基线守门——主专案 13 条警告全数修掉（CS8604/CS8629 可空dereference、CS0219 未用变量、SYSLIB0057 X509Certificate2 过时构造改 X509CertificateLoader、CS9191 ref→in、CS8600/CS8602）；SmartFailingNow 的 probe() 空值原本是会真炸的缺陷（.Value 直解）顺手收口成三态。新增 WarningBaselineTests：真重建主专案、去重数警告、基线 0——任何人新增一条警告，全套就红。警告没有基线就和没有守门一样：这 13 条存在了数十个版本而没人看见，因为没有东西在数。测试 +1（3657 绿）。
- **v2.49 Olympus**（2026-10-10）：三方对账收口——新增执行期对账：用 App 启动与 CLI 同一条唯读路径跑完三个入口（驱动组／usermode 组／平台组），把「真正产生的键集合」与「申报的键集合」逐键比对。第一次上线就抓出两批共 214 把「天天在生产、覆盖申报完全看不见」的键：pci.dev.／reconcile.／sio.hwm. 等 137 把（驱动组）＋ asset.field.／role.installed.／monitor. 等 77 把（平台组）。其中 106 把其实静态可枚举——补进全键目录（153 → 170，扫描器加六个定向样式追上 helper 传键的写法）；13 个真动态家族（成员由机器决定）进新设的 FactKeyDynamicCatalog 登记——每个前缀要有「为什么动态＋成员由什么决定」的理由，且前缀本身必须在源码扫得到（僵尸前缀红灯）。对账类跑真 WMI／MMDevice／驱动会话，归入禁并行集合。测试 +4（3656 绿）。
- **v2.48 Olympus**（2026-10-10）：审计链的 App 外验证入口——CLI 新增 --verify-audit：逐笔重算审计日志杂湊链、输出 fileExists／chainValid／checkedEntries 与断点原因，退出码 0＝链完整（日志不存在如实标「还没有审计事件」，不假称通过）、2＝链断或损毁、1＝致命。能产日志却不能在 App 外证明日志没被改，『可证明』的主张就打折扣——本机现况实测 687 笔链完整。LocalApiHandler 写入判定登记表（HTTP 壳刻意不启动是决定不是遗漏：开监听＝本机攻击面；headless 出口由 CLI 承担），登记表验证同步支持非 Service 结尾的能力类别。测试 +4（3652 绿）。
- **v2.47 Olympus**（2026-10-10）：Uncore 事实接线＋孤儿服务完整性网——UncorePmuService 接进证据实验室（pmu.uncore.platform／ratio_limit／perf_status 三键，目录 150 → 153；平台白名单判定先行，未收录平台一个 MSR 都不碰，三态各附原因）；新增 WiringDecisions 判定登记表（刻意不接线要写名字与理由：SMN 写入面、WASAPI 主动捕获、上传通路刻意不实作、假容量写操作）与 ServiceOrphanGateTests：每个公开服务要么被生产引用、要么在登记表有判定，登记表过时也红灯。扫描踩坑：同档宣告＋同档真实引用（ScmDriverService）不能整档排除，改行级判断。测试 +10（3648 绿）。
- **v2.46 Olympus**（2026-10-10）：发布治理——新增 Tools/verify-release.ps1 发布后验证脚本（README 下载链接逐一查 Release 存在、资产大小与表列字节数对账、版号六处一致；匿名 API 限流自动改向 Git Credential Manager 取 token；任一失败非零退出）与 ReleaseIntegrityTests 三条（「字节数为本版实际发布」宣称与下载链接同版本——防「文字跟着跳、数字没跳」再现；宣称版本等于项目版本号；验证脚本存在且带 UTF-8 BOM——PS 5.1 读无 BOM 的 UTF-8 会把中文当 ANSI 炸）。本版不加功能：先把发布管线修好再走。测试 +3（3638 绿）。
- **v2.45 Olympus**（2026-10-10）：环境假设守门——清掉生产代码两处写死开发机路径（PaddleOCR-VL 目录、CPU-Z 搜索变体），改环境推导、找不到如实回 null；新增守门测试扫「驱动器:\Users\<用户名>」与「:\Desktop\」字面值，角色与 ACL 名（WindowsBuiltInRole.Administrator、BUILTIN\Administrators）不误报、SpecialFolder 推导写法不误报，白名单每笔要理由、上限 5（目前为空）。与 v2.43 的 WinRing0 同族缺陷：用户端的静默失灵长得像「没有这个功能」，不像「路径假设错了」。测试 +3（3635 绿）。
- **v2.44 Olympus**（2026-10-10）：时间炸弹守门——测试方法不得在同一个方法体里同时写绝对日期与 UtcNow 查询窗（已炸过一次：TrendSentinel 的数据基准写死 2026-10-08 配相对窗，隔天 8 条断言同时红，2.43 已修）。守门做在方法级而不是文件级：文件级扫描的「高危」三档，方法级逐一核对后两条是误报（写入读回的往返与合成序列不与查询窗相遇），正对照测试钉住扫描器真抓得到炸弹。测试 +2（3632 绿）。
- **v2.43 Olympus**（2026-10-10）：核心热区图重做——格数改由拓扑的实体核心数决定（新增 PhysicalCores），传感器少报几颗时缺的位置照样占一格并如实留白（灰色格与「—」代表拿不到读值，不是 0 °C、也不是凉）；两种逐核来源（传感器列／逐实体核心摘要）收敛到同一份格子契约与同一支控件，色阶收敛成单一来源 HeatScale（核心热区图、逐核液柱与图例同一个温度就是同一个颜色，不随主题变），图例与统计列（最热／最冷／平均／覆盖率）由锚点与数据推导；修掉逐实体核心编号从 0 起算、与全站 1 起算差一号的缺陷。另新增 ESP 文件层扫描（枚举 .efi 文件 SHA-256 并与 dbx 交叉引用，命中是攻击面事实不是中毒判决）与 WinRing0 来源内嵌（先前单文件发布在用户端找不到驱动，MSR／PCI／I/O／MMIO 全数读不到；现在 0.9.4 跟着可执行文件走）。测试 +40（全套 3630 绿）。接线守门：三支事实服务（音频端点／开机计时／网络卸载）接进共用入口，并加「事实服务必须被生产代码引用」的守门测试。
- **v2.42 Olympus**（2026-10-09）：.etl 內容讀回——本專案寫得出（EtwTraceService）、認得出（IsValidEtl）、這一版起讀得回來：TraceEvent 檔案模式把落地軌跡解析回逐提供者事件數、未解事件數（如實計數不以 0 補）與時間範圍。測試過程抓到會炸行程的真缺陷：垃圾位元組餵給 ETWTraceEventSource 會踩 TraceEvent finalizer 缺陷炸掉測試主機——修法是建構前先驗檔頭。接進 LoadUsermodeFacts 共用入口。真機以系統 .etl 實測閉環。測試 +5（全套 3591 綠）。
- **v2.41 Olympus**（2026-10-09）：裝置安裝時間線——唯讀解析 setupapi.dev.log（peripheral-forensic 能力的唯讀版）：區段標記、裝置實例 ID、起訖時間戳、結束狀態、錯誤行、開機段歸屬。解析只取語言中立欄位（標記符號／裝置 ID 語彙／時間戳格式），在地化標籤混入照樣解出（測試釘住）；接進 LoadUsermodeFacts 共用入口。真機實測 376 區段、36 錯誤行。測試 +9（全套 3586 綠）。
- **v2.40 Olympus**（2026-10-09）：驱动档静态检视（DrvEye／DriverSight 能力的唯读版）——对非系统目录的加载模块逐颗读回 .sys、解析 PE 结构（机器／子系统／区段／汇入表）、扫内嵌装置字符串与 CTL_CODE 编码候选（Function 落自订范围才收，并明说「候选不是确认」），与既有 BYOVD 封锁清单双道交叉（SHA-256 主、档名辅）。全程不加载驱动、不呼叫 IOCTL。接进 LoadUsermodeFacts 共用入口（UI 与 CLI 同源）。真机实测：235 颗非系统模块、逐颗解出汇入与装置字符串。测试 +14（全套 3577 绿）。
- **v2.39 Olympus**（2026-10-09）：UEFI 固件磁碟区结构解析——唯读读回 BIOS 区、依 UEFI PI 规格解出 FV → FFS 档案 → 区段顶层树（GUID／型别／大小／使用者接口名），压缩区段只列出不解压、表头校验和不符合的档案如实计数、找不到 _FVH 如实回 0。新纯解码器纳入 SpecRef 覆盖检查；固件安全页新增卡片。金标向量按规格逐栏编码，测试 +15（全套 3564 绿）；MMIO 后端不可用时三态如实显示原因，驱动加载后真值翻转。
- **v2.38 Olympus**（2026-10-09）：OS 内建查询接缝化（INativeToolSource 接缝，powercfg 原样输出诚实带进画面，宣告与执行分离）＋存储可靠性计数器 19 栏（storage.reliability.*，值为 0 就是 0、提供者没给的栏位数出并具名列出）＋三个真缺陷修复：整合没接线（LoadUsermodeFacts 具名入口＋守门测试）、三态被写错（先 Read 再判可用性，例外一律 ReadError）、CLI 进入点在 .NET 10 上 StartupUri 崩溃（改 Shutdown 机制，端到端实测通过）＋虚拟化卡片绑定路径修正与「有资料时」样板渲染检查。README 测试徽章改由 TestSuiteBaseline 单一来源保管。测试 3548 绿。
- **v2.37 Olympus**（2026-10-08）：修 v2.36 的覆盖申报缺陷——发布版扫不到源码时会把「0 个事实键」读成「全部都覆盖了」。事实键目录改为编译期固定（134 键）＋规则改用运行期那一份（26 条），并加三条回归测试。另修正说明文档对 Rules/builtin.json 的承诺（该文件没有随程序出货）。测试 +11：项目测试 3524 绿（全套是 3525，差的那 1 条是本机未追踪的探针）。
- **v2.36 Olympus**（2026-10-08）：地基——可用性由五态扩为六态（新增 Unknown＝有值但未确认）并定义偏序格与合取传播；覆盖申报（设置页）申报事实键的对账覆盖与知识表收录率；版号守门扩大到三份 README 的下载链接与版本沿革。测试 +59，全套 3514 绿。
- **v2.35 Olympus**（2026-10-08）：第四梯收官——音频端点缓冲区（独占模式延迟下限，四级用途判读）、识别与资产（SMBIOS 识别字段与固件未填标注、机箱类型）、已安装角色与功能的攻击面（装了但没在用，服务没在跑不等于不会跑）；IPMI／MegaRAID／Redfish 三个既有解码器纳入 SpecRef 覆盖检查。测试 +76，全套 3455 绿。
- **v2.34 Olympus**（2026-10-08）：IOCP 队列深度曲线判读（还有余裕／已饱和／加深反而下降三种形状，并说明延迟随 QD 上升是排队的必然结果而非故障）、NUMA 跨节点标为「不适用」并明说未验证不代表可用。新增纯解码器 StorageQdJudge，测试 +16，全套 3379 绿。
- **v2.33 Olympus**（2026-10-08）：内存通道配置（插槽命名推断的通道数 vs 每通道模组数，并明说理论上限的「每支各占一通道」假设站不站得住）、屏幕组成（真实屏幕／软件虚拟屏幕／操作系统默认对象三分）。新增纯解码器两个，测试 +26，全套 3363 绿。
- **v2.32 Olympus**（2026-10-08）：服务器与工作站视角——虚拟化平台三态（组件／服务／虚拟层分离，「装了但开机未载入」不再被讲成「已启用」）、网卡两条链路落差（PCIe 供给 vs 线路速率，供给不足时明说「跑不满」）、显示适配器真伪（真实 vs 软件 vs 基本显示驱动）、SMBIOS 补完（Type 1 UUID 小端序、Type 3 机箱类型、Type 28／29 传感器的「值未知」位元）、内存错误更正三层分离（ECC／Registered／平台能力不互相推论）。新增纯解码器四个，测试 +105，全套 3337 绿。
- **v2.31 Olympus**（2026-10-08）：PCIe 落差判读——PCIe 链路页新增「落差」栏，把「装置宣告的能力」与「实际跑到的链路」之间的差距依成因分成相符／宽度受限／速度待确认／省电设计／上游上限／未判定，并附上实际字段值当依据。会往上游端口读它的链路能力，上限在上游就明说「这张卡不是瓶颈」；读 Link Control 2 的 Target Link Speed，被 BIOS 或驱动压低的协商上限直接指出来；上游读不到一律标未判定，不当成「上游没有限制」。**纯只读，不写任何寄存器**——PCIe 主动重协商会动到运作中的链路，本项目不跨这条线。测试 3218 绿。
- **v2.30 Olympus**（2026-10-08）：SPI 快闪熵图——只读读回整颗快闪、逐 4 KiB 块算 Shannon 熵、依 FREG 区域切分，看出内容组成（高熵＝压缩／加密、低熵＝空白或规律、全 F＝抹除）；只描述分布不判断好坏或是否原厂，PRx 读保护拦截范围会被算成抹除区且报告会标明。测试 3195 绿。
- **v2.29 Olympus**（2026-10-08）：历史仓扩充——长期追踪指标由 7 项增至 13 项（新增处理器功耗／电压、VRM 温度、显卡功耗、内存用量、存储温度）；磁盘格式升为 v2 并改为硬性版本检查（字段数不同时整份忽略而非错位读成假读值）；既有字段顺序未动并加测试钉住。测试 3174 绿。
- **v2.28 Olympus**（2026-10-08）：统计哨兵——历史回放页新增时序统计判读：Theil–Sen 稳健斜率（趋势，附 95% 置信区间）、CUSUM 累积和（变化点）、Pearson 前后半相关性（关系变化）；只陈述数列本身的变化，不对硬件健康下因果结论；数据不足／无传感器一律如实回报；斜率置信区间改用中位绝对差（MAD）常态近似（Sen 无母数区间在配对数量大时会宽到失去判别力）；新增纯统计核心 TrendSentinel 与服务层，测试 +30。测试 3170 绿。
- **v2.27 Olympus**（2026-10-08）：语言模式收尾——自绘控件（FieldRow／RadialGauge／HistoryGraph／Oscilloscope／AnalogVoltMeter／DonutChart／SectionHead）的文字是自定义依赖属性、不在视觉树上，过去三种语言模式都碰不到，现在逐一转换；状态栏与时钟的复合字符串改为逐段翻译；修正有绑定的 TextBlock 被当行内文字写入（1015 处绑定的内容从此不再被写死，时钟与实时读值恢复跟着来源更新）；修正简体模式夹英文（英语模式产生的字符串存槽前先反查回繁中）；翻译表补 308 条；已知未收录：安装精灵与彩蛋页的长篇文案。测试 3140 绿。
- **v2.26 Olympus**（2026-10-08）：语言模式修复——设置页 English 正式接线、英语模式真的可用，简体模式与语言切换卡住的问题一并修掉；绑定字符串 StringFormat 缺口补完（48 处）；一键部署器下载文件名错字修正（XinSect.exe → XinSpect.exe）；README 下载链接与版本沿革修正；新增落差分析与扩展总蓝图文档；测试 3134 绿。
- **v2.2.0 Olympus**（2026-10-04，FileVersion 2.2.0.1）：系列更名 Everest→Olympus；WinRing0 回归主力、XsRegProbe 转白名单备援；交叉对账 26 条外部化＋判决卡；验机杀手级（SMART failing-now、机箱开启、HPA、假容量验证、WCTEMP、TDR、退休页、NPU）；安全鉴识（BYOVD、Defender 排除、1102、信任根、USBSTOR）；处理器深化（TME/SGX、C-state、PMU 能力＋编程验证、die 拓扑、SLIT）；系统软件层（Update 历史、服务盘点、事件记录摘要、稽核政策、选用功能、核心模块 Authenticode、开机参数、开机计时、USB 拓扑、摄影机、企业存储、网卡健康＋OUI、Wi-Fi 频道）；蓝色中队内置本体（可开关）；深测中心 38 项；公开规格与文档十份；审计日志、时间胶囊生命周期事件、corpus 骨架、本机 API、查询语言；DeepBench 启动崩溃修复；突变测试解锁 82%；3094 测试。
- **v2.1.0 Everest**（2026-10-02）：固件安全页、深层寄存器三态、XsRegProbe 驱动源码、深层存取豁免开关、Deep Bench 20（10→20 项）、守护进程三版、蓝色中队态势评估、一键装机、硬件检测、工具箱。

## 十八、常见问题

**为什么有些项目显示「权限不足」？** 该项事实需要 ring0（MSR／PCI 配置空间读取）或管理员权限（Security log）。以系统管理员重新执行即可翻成真值；程序不会静默降级或以默认值冒充。

**为什么 SPI／MCHBAR 相关事实全为三态？** 这些寄存器位于 SPIBAR，需要 MMIO 读取通路。WinRing0 载入后由 MmioBackendSelector 裁决（WinRing0 主力→XsRegProbe 备援→三态）；企业环境若拦截 WinRing0，可改走深层存取开关载入 XsRegProbe 白名单驱动。

**交叉对账显示「矛盾」是不是中毒了？** 不一定。矛盾的意思是「两个来源对同一件事说了不同的话」，可能是固件实现差异、量测时机差异，也可能是窜改。判决卡会列出双方的数值与来源，判读留给你。

**突变分数 82% 够吗？** 知识表数据档刻意排除（逐条字符串断言＝快照重复）；主程序无法突变测（WPF＋特权依赖），由注入式测试与金标向量承担。82% 是「已测范围内」的分数，账本有逐批记录。

**会收集我的数据吗？** 不会。事实搜集层零网络 API（机器检查扫描源码钉死）。四个会上网的功能（AI 评价、回馈、测速、网络延迟量测）全部需要你主动触发。

**Rowhammer 探测安全吗？** 它在自拥有的缓冲区内操作，但 usermode 无法完全隔离相邻物理列——程序以「危险」标注并建议使用专用测试机。它也不是正规的 Rowhammer 施测（无 clflush），结果标「未经过校验」。

**蓝色中队是防毒软件吗？** 不是。它是六防线的安全态势评估（只读）加 ETW 即时威胁侦测时间轴（观察）。它不隔离、不删档、不挡程序——它把看到的事实摊开。

## 十九、授权

本专案以 [MIT License](LICENSE) 释出。内置第三方组件（Intel XTU SDK、LibreHardwareMonitor、TraceEvent、NAudio 等）受其各自授权条款约束，不在本专案 MIT 授权范围内。WinRing0 为双用途驱动，本程序的 MSR 读取亦依赖它；使用者应仅在自有或获授权的机器上使用本工具。

## 二十、作者

By：Xinglanclever
