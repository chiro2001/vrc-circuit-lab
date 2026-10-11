# 网表渲染与 GPU 仿真冒烟

本轮验收环境为 Windows 上 Unity 2022.3.22f1 的 Play Mode，使用真实 Udon 程序执行调度、`VRCGraphics.Blit` 执行 GPU 计算、`VRCAsyncGPUReadback` 核对结果。GPU 为 RTX 5080，图形 API 为 Direct3D 11。

## 验收结果

| 检查 | 结果 |
| --- | --- |
| RTL / 综合后 testbench | 各 897 个周期，通过，轨迹一致 |
| GPU 网表仿真 | 897 × 69 = 61,893 个信号值，0 差异 |
| shader | 支持，无编译诊断错误 |
| 按钮 Udon 事件链 | 复位、加一、加载、再复位，通过 |
| 同帧 16 次时钟请求 | 全部执行一次，通过 |
| RUN TEST 按钮 | 重新完成全部 897 个周期，通过 |

完整结果见 [summary.json](results/netlist-smoke/summary.json)，截图见 [Play Mode 画面](results/netlist-smoke/playmode-pass.png)。最后一轮自检约 19.96 秒，包含每周期 GPU 异步读回与逐信号比较，该时间用于本轮验收。

## 电路与综合

[smoke_accumulator.v](circuits/smoke_accumulator.v) 是一个 4 位累加器，优先级为同步复位、加载、使能加法、保持，并输出零标志和奇偶校验位。

[testbench](circuits/smoke_accumulator_tb.sv) 检查 897 个周期：全部 256 种 4 位加法组合、溢出、全部控制组合与确定性的随机序列。RTL 和综合后 Verilog 分别通过 Icarus Verilog 测试，两份输出轨迹完全一致。

综合工具为 Yosys 0.66 与 ABC。流程为 `synth -flatten -noabc`、`dffunmap`、`abc -g simple`，输出单个位门和普通上升沿触发器。

| 指标 | 结果 |
| --- | --- |
| 综合单元 | 59 |
| 触发器 | 4 |
| NOT / AND / XOR / OR | 7 / 25 / 7 / 16 |
| 信号，包括常量和输入 | 69 |
| 最长组合逻辑深度 | 11 |
| 状态纹理 | 128 × 1，ARGB32，线性、Point 采样 |

综合结果、参考轨迹和 HDL 验证记录位于 [results/netlist-smoke](results/netlist-smoke)。

## GPU 计算与画面

网表导出工具将每个信号编码成一条指令：操作类型和输入信号编号。Unity Builder 将其写入 RGBAFloat 纹理。两个 RenderTexture 交替保存信号状态，shader 根据网表指令计算下一份状态。

每个周期先注入低电平时钟及输入，执行 12 次组合逻辑更新；再用一个独立 pass 同时提交全部触发器；最后执行 12 次更新，让新的寄存器值传播到组合输出。额外的一轮用于输入注入，避免在尚未稳定的 D 输入上提交。

Udon 的 `Update` 统一执行 GPU 工作。按钮仅提交请求，手动时钟使用 16 项队列，在上一周期 GPU 读回完成后推进下一周期。这样避免交互事件直接提交 GPU 工作时出现读回旧状态的问题。

每一周期都读回全部 69 个信号，与基于综合网表生成、并已核对 Icarus 输出的参考状态比较。参考数据用于验证，门级计算由 GPU 执行。

画面使用两个合并网格呈现连线和节点，材质直接采样 GPU 状态纹理，蓝色表示 0、金色表示 1。布局按逻辑深度排列，反馈线连接 D 输入与触发器；该布局是逻辑展示。

## 场景与操作

远端工程：`D:\Projects\VRChatProjects2\vrc-cpu-world`。

场景：`Assets/GpuNetlistSmoke/Scenes/GpuNetlistSmoke.unity`。

- `CLOCK +1`：累加输入值，默认输入为 1。
- `LOAD 9`：加载 9。
- `RESET`：执行同步复位。
- `RUN TEST`：重新初始化 GPU 状态并运行全部参考向量。

进入 Play Mode 后默认自动自检。预览相机保存在场景中，默认禁用；可在 Play Mode 中通过 Pipeline 启用它，让 Game View 显示电路。其状态在退出 Play Mode 后恢复。

## 复现

Linux 端重新生成 HDL 验证结果及 GPU 数据，然后批量部署纯文本源码与数据：

```bash
python tools/build_netlist_smoke.py
python tools/deploy_netlist_smoke.py
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildNetlistSmoke.cs --entry BuildNetlistSmoke.CompileUdon
```

临时数据默认放在 `~/tmp/vrc-cpu-map/netlist-smoke/`；Windows 部署暂存位于 `~/tmp/vrc-cpu-world/netlist-smoke/`。部署保留 Unity 生成的资产和 GUID，并通过 Pipeline 刷新编译。

首次创建场景，然后运行 Builder。已有场景时先打开已有场景，再运行 Builder；Builder 只重建此冒烟的根对象，保留生成资产的 GUID。

```bash
# 首次创建
python tools/unity_remote.py create_scene --path Assets/GpuNetlistSmoke/Scenes/GpuNetlistSmoke.unity --template empty

# 生成网表纹理、状态纹理、材质、网格、VRChat Descriptor 和 Udon 按钮
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildNetlistSmoke.cs --entry BuildNetlistSmoke.BuildScene

python tools/unity_remote.py editor_play
python tools/unity_remote.py set_serialized_field --target '{"hierarchyPath":"/GpuNetlistSmokeWorld/NetlistPreviewCamera"}' --component Camera --field m_Enabled --value true

# 等待完整自检，验证按钮 Udon 事件链
python tools/unity_remote.py --check-result run_script --timeout 50 --file AgentScripts/BuildNetlistSmoke.cs --entry BuildNetlistSmoke.ManualControls --timeout_ms 45000

# 一次提交 16 次时钟，验证请求没有遗漏或重复
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildNetlistSmoke.cs --entry BuildNetlistSmoke.BurstControls

# 验证 RUN TEST 按钮，并再次核对全部 GPU 状态
python tools/unity_remote.py --check-result run_script --timeout 50 --file AgentScripts/BuildNetlistSmoke.cs --entry BuildNetlistSmoke.RunButtonSelfTest --timeout_ms 45000

# 保存实际 GPU 状态报告，再结束 Play Mode
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildNetlistSmoke.cs --entry BuildNetlistSmoke.Status
python tools/unity_remote.py editor_stop
```

`--check-result` 同时检查 Pipeline 命令体的失败状态；仅看 CLI 顶层 `success` 不能判断动态 C# 编译或执行是否成功。部署工具在域重载期间容忍短暂连接重置，并继续查询编译状态。

运行记录由 Builder 写入远端工程的 `Reports/NetlistSmoke/`。截图通过 Pipeline 保存，实际路径由命令返回；当前包会将截图路径放在 `Assets/Reports/NetlistSmoke/`。

## 当前范围

此实现验证单时钟、二值、同步复位的门级逻辑。组合传播轮数表示逻辑依赖层次，不包含器件物理延迟、毛刺或 X/Z 状态。

本地 `.vrcw` 曾通过 SDK 校验和构建；按本轮要求，以 Play Mode 作为计算与交互验收环境。多人状态同步、Quest 和完整 CPU 规模属于后续验证。
