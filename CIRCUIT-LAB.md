# CircuitLab：共享的分层 CPU 实验台

场景位于 Windows Unity 工程的 `Assets/CircuitLab/Scenes/CircuitLab.unity`。各客户端在 world 本地用 GPU 计算真实综合网表，VRChat 网络负责共享控制、物体布局及检查点。无需额外计算服务器。

## 使用

实验台未被占用时，首次点击仿真、探头或层级树按钮会自动取得全局控制权并执行操作，也可以点击逻辑分析仪顶部的 **REQUEST CONTROL**。已有控制者时，其他玩家通过该按钮申请交接，当前操作者可以交接或释放；操作者离开时自动释放。只有控制者可以操作电路和逻辑分析仪。模块按物体分别占用，同一物体禁止抢夺。

| 控件 | 行为 |
| --- | --- |
| RUN / PAUSE / STEP | 连续运行、停在稳定周期边界、推进一个周期 |
| SLOWER / FASTER | 0.25–8192 周期/秒，FASTER 每次翻倍；显示目标、实际速度和追赶差额，实际速度取决于客户端（本机实测约 3.4k 周期/秒，见[性能报告](results/circuit-lab/perf-rates.json)） |
| RESET | 同步复位 CPU，保留 RAM、终端文本和波形历史，取消复位拍的写入 |
| RESTART | 重新初始化当前程序，恢复 initialRam 并清空实验记录 |
| 程序列表 / LOAD SELECTED TO ROM | 浏览预置源码、注释、HEX/BIN；ASM 源码为 IBM Plex Mono 全量滚动，HEX/BIN 分页；明确点击 LOAD 才切换 ROM |
| HEX / BIN / LOAD DRAFT | 按行输入 12 位程序，校验后原子加载；草稿共享 |
| SIGNALS / GATES / ROM / RAM | 查看实际状态纹理、门编码、当前程序与四页 16×16 RAM |
| BIND BIT / PROBE BYTE | 将选定电路位或 RAM 位/字节绑定到选中探头 |
| EXPAND / FOLD | 展开或收起场景模块，保留信号身份和探头绑定 |
| EXTRACT / RETRIEVE | 取出子模块独立摆放，或恢复停靠关系 |
| RESET LAYOUT | 恢复初始位置、旋转、尺度、停靠和展开状态，保留电路及波形 |
| SELECT A–D / DETACH | 选择测量通道或解除绑定 |
| RETURN / RETURN ALL | 单支或全部探头回收纳架并解除绑定，保留冻结波形 |
| FREEZE / LIVE | 固定真实波形快照，或回到最新；后台采集继续 |
| ZOOM / PAN / LATEST / CURSOR | 时间窗口、历史位置和采样游标 |
| BUS / BIT VIEW / HEX / BIN / DEC | 总线、逐位和数值格式 |
| AA / EDGE WIDTH | 切换解析抗锯齿，调整交叉过渡宽度 |
| CLEAR TERMINAL | 清空共享终端 |

把手采用原生 Pickup 1.1，默认射线抓取距离 10 米，握持参考点位于把手。小模块保留最小世界命中尺寸。PC 用视线，VR 用手柄射线瞄准并抓取；模块及仪器面板使用 Any 自由握持朝向，拾取时保留当前方向；探头保留针柄的 Grip 配置。模块本体不再用 Interact 切换展开，避免桌面抓取误触。

按钮与列表使用世界空间 Canvas / VRCUiShape。层级树的目录三角只控制列表子项可见性，场景展开按钮单独控制电路模块；列表可滚动，选中、目录和滚动状态均共享。交互碰撞体使用玩家可穿透的层，保留射线命中能力。

初始顶层面板位于出生点前左方并面向玩家。展开与布局复位检查旋转后板框和把手边界，避免落入地板。玩家移动初始化设置非零跳跃强度。升级保留用户的 100×100 地板、工作台、出生点及其它环境对象；旧 smoke 不再加载进场景。

## 电路与波形

[教学 CPU](TEACHING-CPU.md)保留 8 位 ACC，新增 X、SP 和 256×8 位 RAM；PC 扩为 9 位、ROM 为 512×12 位。普通指令 FETCH/EXEC 两拍，长跳转和 CALL/RET 有额外微步。13 个预置程序涵盖 π 长除法、计数、逻辑、HELLO、流水灯、倒计时、进借位与长地址栈操作。

默认 π 程序从 355 和 113 计算整数商、余数，再用十进制长除法输出 `3.141592\n`。前六位小数与 π 一致，第七位起偏离；程序说明显示这一边界。数字由实际 CPU 算法生成，Udon 只加载程序、调度 GPU 并读取 OUT 事务。

综合保留 20 个 module 实例，包括 8 个全加器，以及明确的 ROM/RAM 原语。当前为 1172 个节点、79 个 DFF，状态纹理宽 2048，最大组合深度 42，稳定传播 43 轮。RAM 是独立的 32×32 字节纹理。一个周期先求低相位稳定值，CPU 寄存器与 RAM 写入从同一份旧状态提交，再求高相位稳定值；只记录两次稳定结果。二值、单时钟、零延迟模型不包含物理门延迟或毛刺。

逻辑分析仪启动时默认 LIVE、跟随最新记录，启动提示在 Udon 初始化前也显示 LIVE。四通道 GPU 环形历史记录真实值、有效性、绑定段及绝对采样编号，容量为 2048 个半周期样本，每通道最多 16 位。冻结保留独立纹理及测点名、宽度和段信息。重绑从新片段开始，不补造此前未采集的数据；探头归架不改写冻结纹理。

BIT VIEW 的 0/1 字形按显示比例和行高保持宽高比。解析 AA 使用像素导数处理线条与字形，开关只改变显示；状态和历史纹理继续 Point 采样。

## 程序与纹理平板

程序板、状态平板与其余仪器面板的初始位姿以场景内作者布局为准，均可移动并参加全局布局复位（RESET LAYOUT）。2026-10-12 的手工布局调整已通过 `BuildCircuitLab.AdoptPoses` 写回为默认值，并用 PlayMode 验证 20 个模块与 11 个工具全部精确复位。程序编辑板显示当前实际加载的程序、revision 与运行状态，以及共享 HEX/BIN 草稿。右栏显示实际 ROM 的 HEX、12 位 BIN 和反汇编，支持翻页及跳到当前 PC。预置程序选择已拆到独立浏览面板，原分析仪单个切换按钮和编辑板的前后选择按钮已隐藏。

每行输入一个 12 位字，支持 HEX/BIN、`0x`/`0b` 前缀、空格/Tab/下划线分组，以及 `#`、`;`、`//` 注释。空白行忽略，未用 ROM 填 HALT；超过位宽/容量或非法字符会按行报错并保留当前程序。VR 可通过两排位键盘构造并追加指令。加载动作停在稳定边界，复位 CPU、应用 initialRam 并清空该实验的历史和终端。

独立程序面板的可滚动列表包含全部 24 个预置。点击条目只更新浏览内容，展示原始汇编源码、注释、`.equ` 定义与对应 ROM 编码。ASM 源码一次呈现完整列表：使用 IBM Plex Mono 等宽字体，可通过 SCROLL UP / SCROLL DOWN 按钮或鼠标滚轮上下滚动（滚动位置只在本机，不占用共享状态）；HEX/BIN 仍按页查看。顶部的 CURRENT LOADED 与 BROWSING 分别表示实际运行程序和预览程序。只有 LOAD SELECTED TO ROM 才会原子切换程序并初始化实验。浏览选择、模式和 HEX/BIN 页码共享，保持一人全局操作。

关于面板展示项目介绍、作者和完整公开 GitHub 地址 `https://github.com/chiro2001/vrc-circuit-lab`，用于在 world 中了解与访问项目。关于板与程序列表板均可抓取移动，并纳入 RESET LAYOUT；当前默认位姿见场景，最近一次基线化结果记录在 [布局审计](results/circuit-lab/boards-audit.json)。

[关于面板截图](results/circuit-lab/about-panel.png)和[程序列表截图](results/circuit-lab/program-browser-panel.png)来自实际 PlayMode。已验证 27 个既有模块/工具/地板位姿保持不变；选择程序只浏览，明确加载 HELLO 后实际 GPU 输出 `HELLO\n`。共享反序列化恢复编码第 43 页（索引42），观察者不能加载，两个新板的位置复位不改变 CPU。报告：[场景](results/circuit-lab/presentation-authoring.json)、[点击与加载](results/circuit-lab/presentation-browse-load.json)、[共享与复位](results/circuit-lab/presentation-shared-layout.json)。

状态平板按节点显示实际门输出，可查看门纹理编码与依赖信息、ROM 编码和 RAM 的地址/字节。RAW TEXTURE 展示实际 32×32 字节纹理，INIT/CURRENT 切换初始化与工作内容；在 RAM 网格选地址后可绑定单 bit 或整字节探头。RAM 测点用单独的虚拟 ID 映射 GPU 字节，不增加伪造的门节点。复位、运行和 STORE 的变化均来自模拟器的当前纹理。

## 教学展示与本地语言

独立 ISA 手册有 39 页，按概览、编码、基本指令、扩展、标志和例子浏览。BANK 切换四个 RAM 页；SUB/SBC 的 C=1 表示没有借位。页码通过面板内独立的 Manual Udon 状态对象共享，物体位置由面板根对象的 VRCObjectSync 同步。首版不在程序编辑板堆叠整份 ISA。

LED 板直接读取真实 OUT[7:0]，流水灯的每一位有独立灯格。生命游戏板读取真实 RAM 双缓冲，CPU 计算 8×8、B3/S23、封闭边界的八代；没有用 Udon 计算或预录动画。每代约 17,080 CPU 周期，256 Hz 下约一分钟。ISA、LED、Life 三块教学板同样参加 RESET LAYOUT；它们的初始位姿随作者布局一起基线化，PlayMode 复位校验为 0 误差（[复位验证](results/circuit-lab/boards-reset-verify.json)）。布局审计确认所有面板互不穿插、不穿地、不插入背景墙（[布局审计](results/circuit-lab/boards-audit.json)）。

模块的 GRAB 文字改为侧边竖直、半透明的青蓝把手，保留射线拾取命中体积。工具面板继续使用明确的面板把手。

关于板右下角内嵌 AUTO / 简体中文 / 日本語 / English，与项目说明同板并避开程序面板的视线遮挡。AUTO 使用 VRChat 当前语言并响应语言变化；每位玩家的语言覆盖仅本地有效，不改变共享的程序、页码或仿真状态。界面采用 Noto SC/JP 与 IBM Plex Mono 的静态 TMP SDF 字体；代码、HEX/BIN 和用户程序保持原样，预置有三语名称、说明及独立译注。输入框保留兼容 VRChat 的 uGUI 输入路径。

截图：[中文 ISA](results/circuit-lab/isa-manual-zh.png)、[日文 ISA](results/circuit-lab/isa-manual-ja.png)、[英文 ISA](results/circuit-lab/isa-manual.png)、[LED](results/circuit-lab/led-output.png)、[生命游戏](results/circuit-lab/life-output.png)。

64/100 位 π 使用真实 Rabinowitz–Wagon 整数 spigot，初始 RAM 只有工作数组初值，数字由 CPU 算出。全量参考模型、RTL 和综合门级结果已与独立高精度算法比较。Unity GPU 各运行约 4,100 周期并核对寄存器及全部 1 KiB RAM，尚未在 GPU 上等待完整数位输出。按 256 Hz 理想速度，64 位约 11.31 小时、100 位约 27.39 小时；默认快速 355/113 示例保留。算法、程序清单与详细界限见 [TEACHING-CPU.md](TEACHING-CPU.md)。

## 字符终端

CPU 的 `terminal_data[7:0]`、真实 `terminal_write` 脉冲和事务序号来自综合后的实际节点。GPU 在每个上升沿稳定结果中记录事件与周期戳，Udon 批量读回有限事件环；每次 OUT 只消费一次，连续相同字节仍分别记录。支持 ASCII、换行、回车、退格、清屏和有界文本缓冲。

同步复位归零事务序号，首个新 OUT 仍需保留。共享检查点同时保存文本、事件计数及回车后的字符光标，恢复后可以继续输出。

## 多人共享

共享范围包括电路控制与周期，模块及工具面板的位姿、停靠和展开，探头位置与绑定，逻辑分析仪冻结、窗口、游标、格式及 AA，层级列表，以及终端内容。

物体位姿由 VRCObjectSync 管理。可靠的模块和探头状态使用始终活动的独立 Manual Udon 子对象；SDK 禁止 VRCObjectSync 与 Manual Udon 放在同一对象上。折叠只隐藏视觉和交互部分，保留网络对象活动。

控制者发布精简的周期与视图信息；其它客户端在自己的 GPU 上计算并追到权威周期。程序切换、复位、换绑和冻结通过稳定边界的检查点同步。检查点包含真实 DFF 位、当前输入、通道元数据、有效历史及冻结数据，适用于晚加入和失步恢复。控制权交接等待对应 revision / epoch 的序列化确认，旧回执不能解除新检查点的屏障。

DFF 检查点按 32 位字数组打包，已取消原来的 64 位限制；当前 79 位使用 3 个字。检查点还保存全部 512 个程序字、1024 字节 initialRam、1024 字节当前 RAM、程序名称和 revision。程序草稿、输入模式、预置选择、ROM 页面、位键盘和状态平板选项也共享。

2048 样本的 live 数据约 18 KiB，live 加 frozen 约 36 KiB，程序和存储器另约 3.5 KiB，加标签和终端元数据。检查点间隔为配置值与 `payloadBytes / 2500` 的较大者；普通运行和视图变化使用小型连续状态。恢复每帧重建 32 个历史槽，再按帧预算执行本地 GPU 周期追赶。真实网络延迟、晚加入耗时和所有权竞争仍需多人客户端验收；owner 退出时未发布的私有尾部周期不保证保留。

## 验证与边界

此前交互版本中分支圆点已修复为与实际走线交点重合，148 个交点审计均无悬空偏移，并补齐 117 条实际门输出至模块端口的显示连线。卡片的固定握持朝向已取消；最内层全加器实际 Udon 拾取回调验证方向变化为 0 度。真实客户端鼠标/手柄的最终抓取行为仍需设备验证。

本轮截图：[程序与 ISA 板](results/circuit-lab/ram-program-panel.png)、[RAM 状态平板](results/circuit-lab/ram-state-tablet.png)、[原始 RAM 纹理](results/circuit-lab/ram-raw-texture.png)、[实际 π 终端](results/circuit-lab/ram-pi-terminal.png)、[分析仪顶部控制栏](results/circuit-lab/ram-analyzer-control-top.png)。面板近照通过预览相机裁掉前景工具，未移动场景对象；最终 Editor 停在 Edit Mode。

探头归架解绑回归核对 8,192 个冻结纹理 RGBA 像素完全不变，电路周期和冻结元数据保留。独立二维纹理实验在 1024、10000、100000 节点下各执行 16 周期、每周期 65 次 Blit，结果零差异；提交至读回完成耗时约 20–23 ms。短批次受固定提交及调度开销影响，不能用节点数间的微小耗时差判断扩展效率，也不能换算实际 SoC 速度。

本轮 GPU 自检覆盖 128 个测试周期、150,016 个节点位比较，全部一致；256 个低/高相位样本、1020 个有效端口值与独立 RTL 波形一致。仿真侧另做了三项优化并在同一自检下复验：低相位只按时钟锥深度 settle（该网表时钟锥只有它自己，2 遍代替 43 遍）、显示/导线材质仅在状态纹理真正变化时重绑、本地化只轮询运行时会被 Udon 改写的 462 条文本（其余每 2 秒全量扫描一次）。8192 目标下实测由约 1.15k 提升到约 3.4k 周期/秒，π 355/113 在同档目标由 2.45 s 降到 0.77 s。完整 π 程序真实输出九个字节 `3.141592\n`，末态全部 1024 字节 RAM 与独立 RTL/综合网表/指令模型一致。速度阶梯上限已放宽到 8192 周期/秒，每帧提交量按目标速率与帧时间计算并设有突发上限，因此目标高于机器吞吐时会自动退化为实际速率而不会卡死世界；RTX 5080 / D3D11 编辑器实测见 [速度阶梯](results/circuit-lab/boards-speed.json) 与 [π 加速对比](results/circuit-lab/boards-turbo-pi.json)。

独立 Udon/GPU 实例恢复比较全部 1172 节点、79 个 DFF、512 个程序字和 1024 字节 RAM，实时与冻结各 12 个有效 RAM 采样值一致；随后 16 周期追赶中的 RAM 写入与主实例相同。可修改纹理、材质和 GPU 状态缓冲互不共用；静态门数据可共享。这是 Editor 内复制共享载荷的验证，尚未测试真实多人传输。

HEX、带前缀/注释输入和分组 BIN 均经实际 UI pointerClick → backing Udon → GPU 执行验证，非法输入保持当前程序。RESET 取消待提交 STORE 且保留 RAM；RESTART 恢复程序的 initialRam。RAM 探头只在提交后的高相位看到新值；探头归架保留冻结像素。
其它未更新的历史报告对应此前 524 节点 CPU；本轮以 ram-*、gpu、waveform 和结构/布局报告为准。交付时恢复测试更改的材质/可写纹理，并核对默认 π 程序、初始 RAM、暂停/LIVE、未占用控制权和材质着色器。

Unity 环境为 2022.3.22f1、VRChat SDK 3.10.5、Direct3D 11 / RTX 5080。报告位于 `results/circuit-lab/`，远端为 `Reports/CircuitLab/`。

- [结构、碰撞与射线 UI](results/circuit-lab/optimization-structure.json)
- [倾斜展开、布局复位与层级树](results/circuit-lab/optimization-geometry-tree.json)
- [真实 GPU 字符事件](results/circuit-lab/optimization-terminal.json)
- [本轮 CPU/RAM 共享恢复](results/circuit-lab/ram-replica.json)
- [GPU π 输出与全 RAM 末态](results/circuit-lab/ram-pi-gpu.json)
- [HEX/BIN 编辑与 RESET](results/circuit-lab/ram-edit-reset.json)
- [位键盘追加与删行](results/circuit-lab/ram-keyboard.json)
- [真实 RAM 波形](results/circuit-lab/ram-probe.json)
- [顶部控制栏与键盘布局](results/circuit-lab/ram-ui-layout.json)
- [已保存场景与初始资产验收](results/circuit-lab/ram-final-authoring.json)
- [探头复位保留冻结](results/circuit-lab/optimization-probe-reset.json)
- [GPU 与 HDL 对照](results/circuit-lab/gpu.json)
- [独立 RTL 波形对照](results/circuit-lab/waveform.json)
- [独立二维纹理规模实验](results/circuit-lab/scale-benchmark.json)

Editor 中的 `CircuitLabClientSimSerialization` 使用 SDK 自带编码器及 Pre/PostSerialization 回调，使没有一般世界网络传输的 ClientSim 可以运行确认流程。它只存在于 Editor 程序集，不进入 world，不提供电路计算值，也不模拟真实网络带宽或所有权竞争。双 Udon/GPU 实例恢复验证检查实际 GPU 结果；复制检查点数据的测试传输不能替代真实多人验收。原生 PC/VR 抓取及头显输入的最终手感同样需要客户端确认。

[完整 LCVEX 评估](LCVEX-GPU-EVALUATION.md)已经使用真实完整 RTL 及 128 MiB 内存模型做前端实验。完整门综合被当前 SV 前端阻断，整机裸机和 Linux GPU 运行尚未实现。二维纹理规模实验是合成工作负载，不能当作实际 LCVEX 性能。

本轮 24 项均完成参考模型、RTL、综合门级及独立程序 oracle 验证。Unity GPU 的八项新增短程序运行到 HALT，并逐项比较全部寄存器和 1024 字节 RAM；Life 第 1 代与独立 Conway 规则匹配，长 π 只做状态冒烟。三语实际 pointerClick、39 页 ISA、RAM 最后地址 3FF、RAW、页面和面板复位通过。真实多人传输、客户端 AUTO 语言热切换和 PC/VR 原生输入仍需设备验收。

- [完整 HDL 与程序验证](results/circuit-lab/teaching-hdl.json)
- [1 KiB / 手册 / 把手结构](results/circuit-lab/teaching-structure.json)
- [三语交互与复位](results/circuit-lab/teaching-manual-locale.json)
- [生命游戏规则](results/circuit-lab/teaching-gpu-life.json)
- [Life 连续运行显示](results/circuit-lab/teaching-life-running-display.json)

## 复现与升级

```sh
python tools/build_teaching_cpu.py
python tools/deploy_circuit_lab.py
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildCircuitLab.cs --entry BuildCircuitLab.CompileUdon
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildCircuitLab.cs --entry BuildCircuitLab.UpgradeScene
```

新增关于/程序列表 UI 的既有场景使用 `BuildCircuitLab.AddPresentationPanels` 增量入口，不重建电路。

`UpgradeScene` 只重新生成实验台拥有的对象，保留环境；它标记场景修改但不自动保存，先复核环境及结构再通过 Pipeline 保存。已有世界必须使用升级入口，`BuildScene` 会拒绝覆盖。

进入 Play Mode 并等待 ClientSim 初始化后：

```sh
python tools/run_circuit_lab_checks.py --suite ram
python tools/run_circuit_lab_checks.py --suite gpu
# 新一轮默认程序的 PlayMode 中检查程序浏览与加载
python tools/run_circuit_lab_checks.py --suite presentation
```

旧 `--suite programs` / `--suite all` 中的程序回归面向此前固定 ROM 示例；当前 CPU/RAM 使用 `--suite ram`，其余独立交互套件可按需运行。

独立规模实验先在 Edit Mode 调用 `BenchmarkCircuitLab.Prepare`，再在 Play Mode 执行 `--suite scale`。它使用独立资产，不改变生产模拟器的一维纹理架构。

临时目录为 Linux `~/tmp/vrc-cpu-map/` 与 Windows `~/tmp/vrc-cpu-world/`。场景、材质、网格、纹理和 Udon 资产均经 Pipeline 制作；部署只复制文本源和数据。预览相机默认关闭，测试可临时开启，退出 Play Mode 恢复。

本次控制按钮修复通过 `RepairCircuitLabControls.Verify` 验证：95 个初始控制按钮可用；首次 STEP 自动申请并完成一个周期；目录展开、探头 B 选择和 FREEZE 均经 Unity `pointerClick -> Button.onClick -> Udon Press` 执行；无控制权时仍锁定，REQUEST 始终可用。测试检查四类 UI 碰撞面的射线命中，结果位于 `results/circuit-lab/control-clicks.json`。尚未模拟真实操作系统鼠标输入或真实多人交接。
