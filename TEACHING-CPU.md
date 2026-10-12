# 8 位教学 CPU、RAM 与 π 演示

CPU RTL 位于 [circuits/teaching_cpu.v](circuits/teaching_cpu.v)，汇编与指令参考模型位于 [tools/teaching_cpu_assembler.py](tools/teaching_cpu_assembler.py)。[tools/build_teaching_cpu.py](tools/build_teaching_cpu.py) 执行 RTL、综合网表和参考模型比较，并导出 schema 3 GPU 数据。

## 硬件与执行

ACC、X、SP、OUT 均为 8 位，PC 为 9 位，程序为 512×12 位，数据 RAM 为 1024×8 位（四个 256 字节页）。CPU 为单时钟、同步复位、二值零延迟模型。普通指令 FETCH/EXEC 两拍；长跳转、CALL/RET 使用额外微步，真实 phase 是三位总线。

RESET 清零 CPU 寄存器、标志和执行状态（Z 置一），保留全部 RAM；复位拍写使能被关闭，不能提交此前待执行的 STORE。冷启动、RESTART 和加载程序才应用该程序的 initialRam。BANK 选择当前 RAM 页；RESET 将 BANK 复位为零。CALL/RET 固定在第 0 页保存和恢复九位返回地址；X 支持间接访存。

指令第一字仍为 opcode[11:8] + operand[7:0]，每字三位 HEX。跳转第二字低九位给出长地址。

| Opcode | 指令 | 说明 |
| --- | --- | --- |
| 0 | LDI imm | ACC←imm，更新Z，清C |
| 1 | ADDI imm | 立即数加法，更新Z/C |
| 2/3/4 | ANDI/ORI/XORI imm | 逻辑操作，更新Z，清C |
| 5 | OUT | 输出ACC，产生独立终端写事务 |
| 6/7 | JMP/JZ addr8 | 低八位绝对跳转/零条件跳转 |
| 8/9 | LD/ST addr8 | 读写RAM；LD更新Z并保留C，ST保留标志 |
| A/B | ADD/SUB addr8 | ACC与RAM运算；SUB的C=1表示无借位 |
| C/D | ADC/SBC addr8 | 加入进位，或减去(1-C)，支持多字运算 |
| E | EXT | 子操作见下表 |
| F | HALT | 停止CPU提交，保持状态 |

EXT 使用 operand 高四位选择子操作：

| 子操作 | 指令 |
| --- | --- |
| 0/1 | SHL/SHR，C为移出位 |
| 2/3 | E20=TAX、E30=TXA；E21..E24=BANK 0..3，保留标志 |
| 4/5 | LDAX/STAX，访问RAM[BANK×256+X] |
| 6/7 | INX/DEX，更新Z，保留C |
| 8/9 | ADDX/SUBX |
| A/B | JC/JNC，第二字为长地址 |
| C/D | JMP9/JZ9，第二字为长地址 |
| E/F | CALL（第二字地址）/RET |

## 计算 π

默认保留快速的 [355/113 长除法](circuits/programs/pi_355_113.asm) 演示，输出 `3.141592`；前六位小数正确，第七位起不同。另有 [64 位](circuits/programs/pi_spigot_64.asm) 与 [100 位](circuits/programs/pi_spigot_100.asm) Rabinowitz–Wagon 整数 spigot 程序。所有数位由 CPU 执行软件整数乘除与余数更新产生；初始 RAM 仅包含工作数组的初值 2，不存储 π 数字串。连续 9 暂存等待进位判定，保护位确保目标小数位准确。数字完成后停机。

| 程序 | ROM 占用 | CPU 运行周期 | 256 Hz 理想耗时 | 8192 Hz 理想耗时 |
| --- | ---: | ---: | ---: | ---: |
| π 64 位小数 | 301 字 | 10,421,948 | 11.31 小时 | 21.2 分钟 |
| π 100 位小数 | 345 字 | 25,244,764 | 27.39 小时 | 51.3 分钟 |

两程序均已完整执行参考模型、RTL 和综合门级网表，输出与独立 Chudnovsky 高精度结果一致，并比较最终寄存器和全部 1024 字节 RAM。Unity GPU 使用同一网表和程序映像，其冒烟检查不等待完整数十分钟执行。速度阶梯为 0.25–8192 周期/秒；理想耗时按目标速度折算，实际速度取决于客户端。每周期约 35 次 blit（低相位 2 + 提交 1 + 自适应深度的状态传播 + 2 次半周期记录 + 1 次 RAM），本机实测约 5.0k 周期/秒（[性能报告](../results/circuit-lab/perf-rates.json)）：低相位按时钟锥 settle、高相位用 GPU 探针学习实际所需深度、材质按状态纹理变化重绑、本地化只轮询运行时文本，合计把同档目标吞吐提高到约 4.4 倍（1.15k → 5.0k 周期/秒）。

## 预置程序

[JSON 目录](circuits/programs/catalog.json) 是 24 项预置的唯一元数据源：保留原 13 项，新增两份高精度 π、格雷码、LFSR、16 位 Fibonacci、数组排序、素数筛、GCD、CRC-8、跨页 RAM 自检与 8×8 Conway 生命游戏。汇编、三语名称/说明/译注、512 字程序、1024 字节初始 RAM 和验证 oracle 随程序导出。生成器可以重新生成程序与目录，长 π 的元数据更新工具会先验证程序和初始 RAM 一致。

生命游戏使用 B3/S23、封闭边界和 glider + blinker 初始图案，CPU 完成八代并提交双缓冲 RAM；每代约 17,080 个时钟周期。LED 板读取真实 OUT[7:0]，适合查看流水灯与二进制程序；ASCII 终端只显示可打印字符和控制字符效果，数值字节可在 LED、RAM 平板及逻辑分析仪观察。

## GPU 边界与层次

CPU控制、PC、寄存器、输出单元、ALU及八个full-adder保留真实RTL实例，合成成基础门和DFF。ROM/RAM作为明确的存储器原语保留，综合网表测试连接同语义的RTL行为模型。GPU状态纹理保存实际电路节点，独立RAM纹理保存字节，RAM读写由真实CPU端口驱动。

schema 3保留nodes/modules/signals/ports，新增memoryPorts、programs、isa、fixturePrograms、fixtureRamReset。op15=ROM_READ、op16=RAM_READ，a是输出位下标，组合依赖是对应读地址总线。所有ID位序低位先；nodes.level和modules.ports来自真实综合拓扑。RAM测点虚拟ID为nodeCount+address×8+bit，直接由GPU历史采样RAM纹理，未伪装成网表门节点。

当前规模：1172节点、79个DFF、20个模块，状态纹理宽2048，组合深度42、稳定迭代43。独立RAM为32×32 ARGB32线性Point采样，每像素R通道保存一个字节。ROM的RGBA32纹理以R保存低八位、G保存高四位。

## 构建与验证

需要Python 3、Yosys、Icarus Verilog。临时文件使用项目专属目录：

```sh
python tools/build_teaching_cpu.py
python tools/deploy_circuit_lab.py
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildCircuitLab.cs --entry BuildCircuitLab.CompileUdon
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildCircuitLab.cs --entry BuildCircuitLab.UpgradeScene
```

默认产物为 `~/tmp/vrc-cpu-map/circuit-lab/hdl/`。本轮集成产物使用 `~/tmp/vrc-cpu-map/cpu-hdl-stage/`，部署时显式传入 `--data`。UpgradeScene保留环境、原模块和工具摆放，标记修改但不自动保存。

RTL、综合网表和指令参考模型比较真实端口、终端事件与完整 RAM。长 π 运行关闭逐拍 CSV，流式比较终端、最终寄存器与 RAM，避免生成数十 GB 文件。短 GPU fixture 检查所有节点及独立 RTL 波形；Unity 入口为 `ValidateCircuitLabRam`、`ValidateCircuitLabRamReplica` 和 `ValidateCircuitLabTeaching`。GPU 状态报告由 `tools/verify_gpu_teaching_state.py` 对照独立指令模型，验证代码不向 world 提供计算结果。

独立 ISA 手册包含 39 页，中/日/英说明由每位玩家本地选择，页码共享。字体使用预制 TMP SDF 三语显示组，AUTO 读取 VRChat 当前语言；语言覆盖仅本次游玩、不同步。PC/VR 原生键盘、真实客户端语言热切换及多人网络传输仍需客户端验收。实际报告与截图位于 [results/circuit-lab](results/circuit-lab)。
