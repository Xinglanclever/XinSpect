繁體中文 · [简体中文](README.zh-CN.md)

# 曦览 XinSpect

> 一款免费开源、专为 Windows 打造的原生硬件验机／监控／安全工具——单一执行文件、免安装、全程只读读取、不收集任何数据。

![版本](https://img.shields.io/badge/version-2.5.0-4C8DFF)
![平台](https://img.shields.io/badge/platform-Windows%20x64-0A7EA4)
![框架](https://img.shields.io/badge/.NET-10.0--windows%20(WPF)-512BD4)
![测试](https://img.shields.io/badge/tests-3094%20passed-3FB950)
![突变分数](https://img.shields.io/badge/Stryker-82%25-8B5CF6)
![授权](https://img.shields.io/badge/license-MIT-green)

![总览](gallery2.png)

曦览（XinSpect）以 WPF（.NET 10）编写、MVVM 架构，整合 LibreHardwareMonitor 传感、Intel XTU 桥接、NVML／NVAPI 显卡控制、WebView2 内置浏览器，以及本专案自写的 WinRing0 事实读取层与 XsRegProbe 白名单只读驱动。名称里的「Spect」是检视者——它不替你的硬件下结论，它把硬件自己说的话、固件说的话、操作系统说的话并列摊开，读不到的如实标注，让验机的人自己下判决。

**[下载 v2.5.0 Olympus](https://github.com/Xinglanclever/XinSpect/releases/tag/v2.5.0)** — 单一执行文件免安装，蓝色中队守护进程已内置。

## 核心设计哲学：诚实契约

这不是营销词，是**有机器检查守着的工程约束**：

- **三态标注**——每一条事实都带 `availability`：读到（Present）、平台不支持（NotSupported）、权限不足（InsufficientPrivilege）、读取失败（ReadError）、本机无此硬件（NotApplicable）。**读不到就是读不到，绝不以 0、典型值或旧值填补。**
- **来源可稽核**——每条事实标明出自哪颗寄存器、哪个 WMI 类、哪支 API；寄存器解码器必须附规格引用（SpecRef），由反射机器检查，漏写直接红灯。
- **非自造验证**——测试结果附自我检核：象棋 perft 是数学常数，算出别的数字是这台机器算错了而不是慢；量测前用已知大小负载自我验证；能量计对不上就不换算成瓦。
- **突变测试**——纯解码器抽成独立类库，Stryker 突变分数 82%，抓到过「文档说有 extended family 进位、实现却漏做」的真 bug。
- **查不到≠没有**——查询语言对「存在但读不到」的条目照样匹配并附原因，不把查错伪装成空结果。

## 下载

| 文件 | 用途 |
|---|---|
| `XinSpect.exe`（约 29.7 MB） | **主程序**——单一执行文件免安装，蓝色中队守护进程已内置（运行期自动解压，SHA-256 验证） |
| `BlueSquadronBridge.exe`（约 6.5 MB） | 独立守护进程——只想单独跑安全防护的人才需要 |

需求：Windows 10 1903+／Windows 11 x64，.NET 10 Desktop Runtime。部分功能（MSR 读取、SMART、事件日志 Security log）建议以系统管理员执行——没有权限时相关条目会如实标「权限不足」，不会假装成功。

## 功能总览

### 总览分组

- **我的电脑／总览** — CPU／主板／内存／显卡／存储的完整规格摘要，主板厂商徽章与 CPU 官方 logo、内存插槽配置图（实心＝有模块、虚线＝空槽；看不出通道就直说，不硬凑 A/B——猜错通道会害人把内存插到错的槽）。
- **瓶颈诊断** — 把散在各页的读值合起来回答「现在是什么在拖住这台机器」：温度墙、功耗墙、单线程、内存、存储、显卡、驱动 DPC、电源策略、MCA 平台事件，依「该先看哪一条」排序；**没量到的数据列进「还没纳入判断的部分」，不当成没问题**。全程只读。
- **AI 评价** — 接 Ollama 或任意 OpenAI 兼容端点对本机硬件给出评语（提示词可自定义）。

### 处理器

- 完整 CPUID 解读：世代判定（含 extended family 进位——突变测试抓出并修正过的真 bug）、指令集、缓存拓扑、die 拓扑（CPUID 0x1F）。
- 微码修订双来源交叉对账：CPU 自己说的（MSR 0x8B 逐核读取，逐核不一致如实标）vs Windows 记录（注册表 Update Revision，8/4 字节两种实测布局都解、歧义不解码）。
- TjMax、温度、频率真相（MPERF/APERF 与 Turbo 阶梯）、电压、每核心负载。
- **TME／SGX 内存加密状态**（MSR 0x982＋CPUID leaf 0x12）、Package C-state 驻留（µs）。
- **PMU**：能力探索（CPUID 0xA）＋固定计数器只读观察；编程验证（最小写入→读回→工作量→还原，3 轮聚合）——**多轮测试・不保证可用**，同意闸门，绝不碰 PMI 位，每轮还原原值。
- **性能天花板** — 回答「这颗 CPU 为什么跑不到该有的频率」：TCC 节流温度、PL1/PL2 与时间窗、电流墙、Turbo 倍频表全部直接读 MSR（不是规格书数字），加上限制原因寄存器的黏滞纪录与使用者亲自触发的逐窗撞墙量测（基线／整数／AVX2／AVX-512），归因成一句判决：温度墙、功耗墙、电流墙、供电模块过热、自主 P-state、多核涡轮上限，或「缺口不在硬件」。全程只读，不清任何黏滞位。

### 内存

- SPD 直读：DIMM 完整数据（制造商、序列号、时序），标明读取总线；TSOD 温度传感器。
- 通道／容量／速度（标称 vs 实际）、ECC 现况。
- Rowhammer：风险声明（为什么工具不测）＋**压力探测**（自拥有内存内锤击、同意闸门、危险标注、**未经过校验**——usermode 无 clflush，非保证触发）＋多轮模式。
- NUMA：拓扑、节点距离（ACPI SLIT）、跨 NUMA 对照、TLB 与大分页成本。

### 主板

- 型号／BIOS 版本／序列号、芯片组、SMBIOS 全解。
- **机箱开启侦测**（SMBIOS Type 3 Security Status——「入侵侦测」＝拆机的固件级证据，二手验机一翻两瞪眼）。
- Super I/O 芯片识别（coreboot 出处对照）＋ HWM 传感器（风扇 RPM、温度、电压——电压标「未经主板校准」）。
- PCI Bus 0 设备盘点（PCI-SIG 类别码知识库）、PCI BAR 资源、ReBAR 实况（问 Windows 实际指派的内存窗口，不是能力宣称值）。
- SPI 闪存稽核三层：寄存器旗标（FLOCKDN/WRSDIS/PR）→ FREG 地图 → BIOS 区 SHA-256 → 与参考映像逐块比对。
- ACPI 表列全解：MCFG、HEST、BERT、SLIT 节点距离、CEDT（CXL）、HPET、FADT PM timer。
- CMOS/RTC（电池电压、时钟、校验和）、UEFI 开机设定四态、POST 代码。

### 存储设备

- **SMART 全属性＋门槛 failing-now**：SMART READ THRESHOLDS（0xD1）对照 READ DATA——**现值 ≤ 门槛＝现正低于门槛**，逐项摊开（门槛 0＝无门槛不评比）。
- **NVMe**：健康记录全解、WCTEMP 温度警告（Identify Controller 0x14A 对照合成温度）、错误记录、电源状态表（各阶功耗与进出延迟）对实测闲置唤醒延迟。
- **HPA 隐藏容量**：ATA IDENTIFY 最大 LBA 对照 OS 可见——不一致即 HPA 作用中（翻新机／窜改证据）；DCO 需厂商私有命令，诚实声明不实现。
- **磁盘表面扫描**：顺序读取逐块量延迟，标记慢区／读取错误——SMART 是固件说的，这是自己读的。
- **假容量写入验证**（H2testw 式）：可重现样本写满→冲刷→读回逐字节验证→删档；**同意闸门＋危险标注**（加剧濒死媒体损耗）。
- 通电时数与机龄推估、磁盘 QD 性能曲线、容量／固件／序列号。

### 显卡

- NVIDIA NVML：温度／频率／功耗／风扇／温度阈值／**退休页**（NAND 瑕疵退休计数）；AMD／Intel 基本信息。
- GPU 深测：光栅填充率、纹理取样、H.264 编码、计算管线、PCIe 频宽。
- HDR 能力（EDID CTA-861）、**显示链路真相**（像素时钟／色彩编码／位元深度——频宽不够时驱动偷偷降 4:2:2，设置里照样写 4K144）。
- TDR 逾时设置（未设置＝Windows 默认并明说）。
- 显卡超频：NVML 功耗／风扇／温度监控＋NVAPI 时脉调整。

### 网络

- 接口清单与速率、**错误／丢弃计数**（非零逐条摊开——驱动劣化／线材的第一指纹）、MAC OUI 厂商对照。
- Wi-Fi RSSI／频道／认证（BSS list 中心频率查表，换不出不猜）、网络卸载状态、网卡高级属性、网速测试、网络延迟。

### 安全（固件安全页＋防护页）

- **固件安全寄存器**：BIOS_CNTL、SMRAMC、ME 状态（HFSTS1）、IA32_FEATURE_CONTROL、IA32_DEBUG_INTERFACE、SPI 锁定旗标与保护范围——每项都下裁决，不利裁决警示色。
- **交叉对账判决卡**：26 条规则对同一批事实做语义与管线一致性检查（规则外部化于 Rules/builtin.json），矛盾整列红字——两个来源对同一件事说不同的话。
- **平台可信度**：hypervisor 存在位／签名、VBS／HVCI 状态、Invariant TSC——MSR 类卡片在此情况下「只能当参考」明说。
- **BYOVD 逐驱动比对**：加载中核心模块对微软「建议的驱动程序封锁规则」做文件名＋SHA-256/SHA-1 双道比对（使用者提供清单 XML，零出网）；命中＝攻击面事实而非中毒判决。
- **安全鉴识**：Defender 排除清单逐条摊开、事件记录清除（1102）侦测、非微软本机信任根（MITM 证书风险面）、USBSTOR 使用痕迹、驱动签名稽核（未签名逐条）。
- **蓝色中队（防护页）**：六防线即时安全态势评估（DMA／固件／CPU／存储／驱动／攻击面）＋ETW 即时威胁侦测时间轴。**守护进程已内置本体**（SHA-256 验证解压＋ACL 锁定），可开关——关闭＝只做只读态势评估。
- **深层存取豁免开关**：产生自签 CA（只放行这一张，不开全机 test-signing）→ 载入 XsRegProbe 白名单只读驱动；关闭＝完全移除不留痕迹。

### 系统与软件层（事实实验室／软件面）

Windows Update 历史、服务盘点（含非系统目录服务）、事件记录摘要（7 天严重＋错误）、稽核政策（LSA 九类别）、机器原则档指纹、选用功能（Hyper-V／VM 平台／WSL／容器）、核心模块加载清单＋非系统模块 Authenticode、开机参数（核心调试／测试签名）、开机计时（Diagnostics-Performance Event 100）、USB 拓扑、摄影机列举、企业存储（iSCSI/MPIO/FC 三态分离）。

### 深测中心（38 项 Run Session）

CPU AES/SHA、Load-to-use/ILP/branch 延迟、分支式矩阵、RDRAND/RDSEED、Intel PMU Top-down、核心延迟、核心到核心搬运频宽、SMT sibling 竞争、cache bandwidth/latency 阶梯、lock-scaling、NUMA/TLB/大分页、DRAM 映射推论、内存频宽、合成 JSON 往返、3D 渲染 D3D11 光栅/纹理、GPU 光栅/纹理/编码、存储复合、QD 梯度、耦合 loopback、WASAPI 信号级、多域 gauntlet、UX 合成负载、统计引擎自我稽核——**原 38 项全量登记，可执行与延后项目都会摊开，不把未跑的说成量测**。只并列原始样本、可信度与限制，不加权合成单一总分；跨域排名不成立。

### 监控与量测工具

- **传感器**：温度／时脉／电压／风扇／负载即时仪表，迷你悬浮窗口与系统匣；超标警示；CSV 导出；历史回放。
- **性能**：棋类跑分（perft 数学常数当检核码）、算力图（离线天梯）、帧时间监测、DPC 延迟、线程迁移（ETW 四层归因＋扣掉自己）、L3 未命中与 DRAM 实际流量（已知负载自我验证）、大页与地址转换成本、NPU 检测。
- **硬核只读量测**：SMI 次数与隐形停顿、机器检查（MCA/WHEA）、核心间延迟矩阵、RDT 缓存占用、电源政策、BIOS 与 ME 微码、AVX-512 撞墙量测——全部只读，取样前后完整还原计数器，**量不到的项目拒绝出数字**。

### 证据实验室

- 时间胶囊：全机事实快照（SHA-256 完整性信封、匿名机器识别、敏感值遮蔽）、跨快照逐栏差分、**资产生命周期事件自动分类**（内存／处理器／显卡／存储的新增移除变更）。
- 原始寄存器快照（.xinraw）：PCI 安全寄存器／ACPI 整表／MSR／SPIBAR／MCHBAR 的原始字节，篡改拒载。
- HTML 单档报告（SHA-256 离线核验）、corpus 贡献包格式 v1 骨架（只收遮蔽版、身份键排除，上传通路刻意未实现）。

### 工具箱与实用工具

- 工具箱：Windows 内置工具一键开启＋八十余款第三方硬件工具的**官方**下载捷径（危险项挂徽章写明最坏情况；无官方发布站的不收）。
- 硬件检测：屏幕坏点、鼠标按键/滚轮/回报率、键盘逐键/防鬼键、喇叭声道扫频、动态拖影——纯原生输入事件。
- 一键装机（winget 整合）、系统风扇控制（真实写入硬件、一键还原自动）、CPU 超频（XTU 桥接：倍频与电压同一张目标时脉规划卡）、垃圾清理、大档扫描、连接埠占用、Hosts 编辑器、右键菜单管理、Windows 授权、睡眠与唤醒、DNS 切换、内存整理、开机启动项、运算稳定性压测、蓝屏分析、系统引导修复、屏幕色域、USB 链路、PCIe 链路、高级驱动分析、操作系统分析。
- 内置浏览器（WebView2）与真实终端机（cmd.exe）。

### CLI 与自动化

```
XinSpect.exe --json evidence [--query <查询>] [--out <文件>]
```

- 退出码：0＝全部事实可读／2＝部分三态／1＝致命。
- `--query` 支持完整查询语言（七字段、三运算子，规格见 `docs/spec/query-language.v1.md`）；PowerShell 模块 `XinSpect.psm1`（Get-XinSpectEvidence）。
- 本机 API handler：`GET /api/facts`、`POST /api/query`（loopback 只读，与 CLI 同一语法）。

## 公开规格与测试品质

- `docs/spec/snapshot.schema.v1.json` — 时间胶囊 JSON Schema（与实际序列化逐键机器对账）。
- `docs/spec/query-language.v1.md` — 查询语言规格（解析器新增键而文档未更新会红灯）。
- `docs/spec/METHODOLOGY.md`、`docs/MEASUREMENT-METHODOLOGY.md` — 事实来源契约与量测方法学。
- `docs/spec/LIMITATIONS.md` — 公开限制（设计裁决与能力边界，全部指向账本内既有裁决）。
- `docs/EC-RISK-ASSESSMENT.md` — EC 埠存取风险评估（结案：不实现）。
- `docs/PMU-SANDBOX-PLAN.md` — PMU 编程沙箱验证方案（S1–S5）。
- `docs/DATA-SOVEREIGNTY.md` — 数据主权声明（事实搜集零网络 API，机器检查扫描钉死）。
- `docs/DEPLOYMENT.md` — 部署与企业维运。
- 测试：**3094 个单元测试全绿**；Stryker 突变分数 82%（纯解码器类库）；FsCheck property 测试；SMBIOS↔WMI 差分测试；完整迭代账本 `docs/ITERATIONS.md`（60+ 批次逐轮记录）。

## 隐私与数据主权

- **事实搜集零网络呼叫**——机器检查扫描源码钉死；全程序仅有的网络功能（AI 评价、回馈、测速、网络延迟量测）都是使用者主动触发。
- 数据全部在本机：审计日志、时间胶囊、原始快照都在使用者自己的路径；**删除文件就是删除数据**，没有云端副本。
- 机器识别是单向哈希派生；默认遮蔽敏感值，保留必须使用者主动指定。
- 自我遥测默认关闭、匿名、本机存取。

## 从源码建置

```
git clone https://github.com/Xinglanclever/XinSpect.git
cd XinSpect
dotnet build XinSpect.csproj -c Release
dotnet test Tests/XinSpect.Tests.csproj
dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

可选组件：XsRegProbe 白名单只读驱动（`XsRegProbe/`，需自行编译签名，见 `XsRegProbe/BUILD-给使用者.md`）；XTU 超频桥接（`Bridge/`，net48，Release 建置自动内置）；蓝色中队守护进程（`BlueSquadron/`，Release 建置自动内置）。

## 授权

本专案以 [MIT License](LICENSE) 释出。第三方组件（Intel XTU SDK 等）受其各自授权条款约束，不在本专案 MIT 授权范围内。

## 作者

By：Xinglanclever
