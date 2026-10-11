# CircuitLab：共享的分层 CPU 实验台

场景位于 Windows Unity 工程的 `Assets/CircuitLab/Scenes/CircuitLab.unity`。各客户端在 world 本地用 GPU 计算真实综合网表，VRChat 网络负责共享控制、物体布局及检查点。无需额外计算服务器。

## 使用

实验台未被占用时，首次点击仿真、探头或层级树按钮会自动取得全局控制权并执行操作，也可以点击逻辑分析仪顶部的 **REQUEST CONTROL**。已有控制者时，其他玩家通过该按钮申请交接，当前操作者可以交接或释放；操作者离开时自动释放。只有控制者可以操作电路和逻辑分析仪。模块按物体分别占用，同一物体禁止抢夺。

| 控件 | 行为 |
| --- | --- |
| RUN / PAUSE / STEP | 连续运行、停在稳定周期边界、推进一个周期 |
| SLOWER / FASTER | 0.25–32 周期/秒；实际速度取决于客户端 |
| RESET | 同步复位电路，保留终端文本和波形历史 |
| RESTART / PROGRAM | 重新初始化或选择程序，清空该实验的记录 |
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

[教学 CPU](TEACHING-CPU.md)包含 8 位数据、4 位 PC、16×12 位 ROM、FETCH / EXEC 两阶段，以及 LDI / ADDI / ANDI / ORI / XORI / OUT / JMP / JZ / HALT。程序 0 循环计数，程序 1 验证逻辑与条件跳转，程序 2 实际执行指令输出 `HELLO\n`，程序 3 保留为 HALT。

综合保留 19 个 module 实例，包括 8 个全加器。当前为 524 个信号节点、45 个 DFF，状态纹理宽度 1024，最大组合深度 25，稳定传播 26 轮。一个周期先求低相位稳定值，再原子提交触发器并求高相位稳定值，仅记录两次稳定结果。模型为二值、单时钟、零延迟；传播轮次不表示物理门延迟或毛刺。

逻辑分析仪启动时默认 LIVE、跟随最新记录，启动提示在 Udon 初始化前也显示 LIVE。四通道 GPU 环形历史记录真实值、有效性、绑定段及绝对采样编号，容量为 2048 个半周期样本，每通道最多 16 位。冻结保留独立纹理及测点名、宽度和段信息。重绑从新片段开始，不补造此前未采集的数据；探头归架不改写冻结纹理。

BIT VIEW 的 0/1 字形按显示比例和行高保持宽高比。解析 AA 使用像素导数处理线条与字形，开关只改变显示；状态和历史纹理继续 Point 采样。

## 字符终端

CPU 的 `terminal_data[7:0]`、真实 `terminal_write` 脉冲和事务序号来自综合后的实际节点。GPU 在每个上升沿稳定结果中记录事件与周期戳，Udon 批量读回有限事件环；每次 OUT 只消费一次，连续相同字节仍分别记录。支持 ASCII、换行、回车、退格、清屏和有界文本缓冲。

同步复位归零事务序号，首个新 OUT 仍需保留。共享检查点同时保存文本、事件计数及回车后的字符光标，恢复后可以继续输出。

## 多人共享

共享范围包括电路控制与周期，模块及工具面板的位姿、停靠和展开，探头位置与绑定，逻辑分析仪冻结、窗口、游标、格式及 AA，层级列表，以及终端内容。

物体位姿由 VRCObjectSync 管理。可靠的模块和探头状态使用始终活动的独立 Manual Udon 子对象；SDK 禁止 VRCObjectSync 与 Manual Udon 放在同一对象上。折叠只隐藏视觉和交互部分，保留网络对象活动。

控制者发布精简的周期与视图信息；其它客户端在自己的 GPU 上计算并追到权威周期。程序切换、复位、换绑和冻结通过稳定边界的检查点同步。检查点包含真实 DFF 位、当前输入、通道元数据、有效历史及冻结数据，适用于晚加入和失步恢复。控制权交接等待对应 revision / epoch 的序列化确认，旧回执不能解除新检查点的屏障。

当前恢复协议支持最多 64 个 DFF、四通道各 16 位。2048 样本的 live 数据约 18 KiB，live 加 frozen 约 36 KiB，另有标签和终端元数据。周期检查点间隔为配置值与 `payloadBytes / 2500` 的较大者；普通单步、运行和视图变化使用小型连续状态。恢复每帧重建 32 个历史槽，再每帧追赶最多 4 个电路周期。网络延迟、晚加入耗时和所有权竞争仍需真实多人客户端验收；owner 退出时未发布的私有尾部周期不保证保留。

## 验证与边界

分支圆点已修复为与实际走线交点重合，148 个交点审计均无悬空偏移，并补齐 117 条实际门输出至模块端口的显示连线。卡片的固定握持朝向已取消；最内层全加器实际 Udon 拾取回调验证方向变化为 0 度。真实客户端鼠标/手柄的最终抓取行为仍需设备验证。

[最终出生视角](results/circuit-lab/optimized-spawn-view.png)显示完整顶层面板和无遮挡的运行控制；[实际波形截图](results/circuit-lab/optimized-waveform.png)来自 Play Mode。最终场景已保存，Editor 停在 Edit Mode。

探头归架解绑回归核对 8,192 个冻结纹理 RGBA 像素完全不变，电路周期和冻结元数据保留。独立二维纹理实验在 1024、10000、100000 节点下各执行 16 周期、每周期 65 次 Blit，结果零差异；提交至读回完成耗时约 20–23 ms。短批次受固定提交及调度开销影响，不能用节点数间的微小耗时差判断扩展效率，也不能换算实际 SoC 速度。

完整 GPU 自检通过 946 个测试周期、495,704 个节点位比较，零差异；1,892 个低/高相位采样的独立 RTL 波形对照通过。

已通过独立 Udon/GPU 实例的 524 节点状态恢复、192 个实时和 128 个冻结有效采样值对照，以及恢复后 16 周期的本地 GPU 追赶。实例状态纹理引用互不相同；测试没有直接修改副本周期或 epoch。非控制者单步被拒绝，终端快照一致。

Unity 环境为 2022.3.22f1、VRChat SDK 3.10.5、Direct3D 11 / RTX 5080。报告位于 `results/circuit-lab/`，远端为 `Reports/CircuitLab/`。

- [结构、碰撞与射线 UI](results/circuit-lab/optimization-structure.json)
- [倾斜展开、布局复位与层级树](results/circuit-lab/optimization-geometry-tree.json)
- [真实 GPU 字符事件](results/circuit-lab/optimization-terminal.json)
- [独立 GPU 实例的共享恢复](results/circuit-lab/optimization-shared-gpu.json)
- [探头复位保留冻结](results/circuit-lab/optimization-probe-reset.json)
- [GPU 与 HDL 对照](results/circuit-lab/gpu.json)
- [独立 RTL 波形对照](results/circuit-lab/waveform.json)
- [独立二维纹理规模实验](results/circuit-lab/scale-benchmark.json)

Editor 中的 `CircuitLabClientSimSerialization` 使用 SDK 自带编码器及 Pre/PostSerialization 回调，使没有一般世界网络传输的 ClientSim 可以运行确认流程。它只存在于 Editor 程序集，不进入 world，不提供电路计算值，也不模拟真实网络带宽或所有权竞争。双 Udon/GPU 实例恢复验证检查实际 GPU 结果；复制检查点数据的测试传输不能替代真实多人验收。原生 PC/VR 抓取及头显输入的最终手感同样需要客户端确认。

[完整 LCVEX 评估](LCVEX-GPU-EVALUATION.md)已经使用真实完整 RTL 及 128 MiB 内存模型做前端实验。完整门综合被当前 SV 前端阻断，整机裸机和 Linux GPU 运行尚未实现。二维纹理规模实验是合成工作负载，不能当作实际 LCVEX 性能。

## 复现与升级

```sh
python tools/build_teaching_cpu.py
python tools/deploy_circuit_lab.py
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildCircuitLab.cs --entry BuildCircuitLab.CompileUdon
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildCircuitLab.cs --entry BuildCircuitLab.UpgradeScene
```

`UpgradeScene` 只重新生成实验台拥有的对象，保留环境；它标记场景修改但不自动保存，先复核环境及结构再通过 Pipeline 保存。已有世界必须使用升级入口，`BuildScene` 会拒绝覆盖。

进入 Play Mode 并等待 ClientSim 初始化后：

```sh
python tools/run_circuit_lab_checks.py --suite all
```

独立规模实验先在 Edit Mode 调用 `BenchmarkCircuitLab.Prepare`，再在 Play Mode 执行 `--suite scale`。它使用独立资产，不改变生产模拟器的一维纹理架构。

临时目录为 Linux `~/tmp/vrc-cpu-map/` 与 Windows `~/tmp/vrc-cpu-world/`。场景、材质、网格、纹理和 Udon 资产均经 Pipeline 制作；部署只复制文本源和数据。预览相机默认关闭，测试可临时开启，退出 Play Mode 恢复。

本次控制按钮修复通过 `RepairCircuitLabControls.Verify` 验证：95 个初始控制按钮可用；首次 STEP 自动申请并完成一个周期；目录展开、探头 B 选择和 FREEZE 均经 Unity `pointerClick -> Button.onClick -> Udon Press` 执行；无控制权时仍锁定，REQUEST 始终可用。测试检查四类 UI 碰撞面的射线命中，结果位于 `results/circuit-lab/control-clicks.json`。尚未模拟真实操作系统鼠标输入或真实多人交接。
