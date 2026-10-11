# 8 位教学 CPU、RAM 与 π 演示

CPU RTL 位于 [circuits/teaching_cpu.v](circuits/teaching_cpu.v)，汇编与指令参考模型位于 [tools/teaching_cpu_assembler.py](tools/teaching_cpu_assembler.py)。[tools/build_teaching_cpu.py](tools/build_teaching_cpu.py) 执行 RTL、综合网表和参考模型比较，并导出 schema 3 GPU 数据。

## 硬件与执行

ACC、X、SP、OUT 均为 8 位，PC 为 9 位，程序为 512×12 位，数据 RAM 为 256×8 位。CPU 为单时钟、同步复位、二值零延迟模型。普通指令 FETCH/EXEC 两拍；长跳转、CALL/RET 使用额外微步，真实 phase 是三位总线。

RESET 清零 CPU 寄存器、标志和执行状态（Z 置一），保留全部 RAM；复位拍写使能被关闭，不能提交此前待执行的 STORE。冷启动、RESTART 和加载程序才应用该程序的 initialRam。CALL/RET 在 RAM 中保存和恢复九位返回地址；X 支持间接访存。

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
| 2/3 | TAX/TXA |
| 4/5 | LDAX/STAX，访问RAM[X] |
| 6/7 | INX/DEX，更新Z，保留C |
| 8/9 | ADDX/SUBX |
| A/B | JC/JNC，第二字为长地址 |
| C/D | JMP9/JZ9，第二字为长地址 |
| E/F | CALL（第二字地址）/RET |

## 计算 π

默认程序 [pi_355_113.asm](circuits/programs/pi_355_113.asm) 使用 π≈355/113，通过CPU求整数商3和余数16，再运行十进制长除法逐位输出 `3.141592\n`。每位用十次有界8位累加、比较和减法产生，数字来自执行过程；程序未预置π数字串，也未由Udon代算。

该近似的前六位小数与π一致，第七位起不一致，UI程序说明明确这一界限。`~/tmp/simple-cpu-pi` 的长除法实现是只读参考，本工程适配了不同的C/LDI语义；其Machin示例的级数项由汇编期预算，本轮没有把它称为CPU执行完整反正切级数。

RTL与综合网表均执行2020个运行周期（另有1个初始化RESET周期）输出九个字节 `[51,46,49,52,49,53,57,50,10]`，并比较全部256字节末RAM。RAM[0:9]为 `[113,104,104,2,0,0,0,16,3]`。

## 预置程序

目录 [circuits/programs](circuits/programs) 提供π长除法、循环计数、逻辑运算、HELLO、HALT、流水灯、55/AA交替、FF+1、倒计时、ASCII数字、步长计数、九位CALL/RET和CPU/RAM验证程序，共13项。程序元数据同时保存名称、说明、512字程序映像、256字节初始化映像及适用时的独立验证结果。

## GPU 边界与层次

CPU控制、PC、寄存器、输出单元、ALU及八个full-adder保留真实RTL实例，合成成基础门和DFF。ROM/RAM作为明确的存储器原语保留，综合网表测试连接同语义的RTL行为模型。GPU状态纹理保存实际电路节点，独立RAM纹理保存字节，RAM读写由真实CPU端口驱动。

schema 3保留nodes/modules/signals/ports，新增memoryPorts、programs、isa、fixturePrograms、fixtureRamReset。op15=ROM_READ、op16=RAM_READ，a是输出位下标，组合依赖是对应读地址总线。所有ID位序低位先；nodes.level和modules.ports来自真实综合拓扑。RAM测点虚拟ID为nodeCount+address×8+bit，直接由GPU历史采样RAM纹理，未伪装成网表门节点。

当前规模：1127节点、77个DFF、20个模块，状态纹理宽2048，组合深度42、稳定迭代43。独立RAM为16×16 ARGB32线性Point采样，每像素R通道保存一个字节。ROM的RGBA32纹理以R保存低八位、G保存高四位。

## 构建与验证

需要Python 3、Yosys、Icarus Verilog。临时文件使用项目专属目录：

```sh
python tools/build_teaching_cpu.py
python tools/deploy_circuit_lab.py
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildCircuitLab.cs --entry BuildCircuitLab.CompileUdon
python tools/unity_remote.py --check-result run_script --file AgentScripts/BuildCircuitLab.cs --entry BuildCircuitLab.UpgradeScene
```

默认产物为 `~/tmp/vrc-cpu-map/circuit-lab/hdl/`。本轮集成产物使用 `~/tmp/vrc-cpu-map/cpu-hdl-build/`，部署时显式传入 `--data`。UpgradeScene保留环境、原模块和工具摆放，标记修改但不自动保存。

RTL、综合网表和指令参考模型比较真实端口、终端事件和RAM；短GPU fixture有128个向量，覆盖访存、进借位、间接地址、移位、长跳转、CALL/RET和reset取消写入。完整π单独验证，避免把全运行轨迹乘全部节点生成巨量fixture。Unity 验证入口为 `ValidateCircuitLabRam` 与 `ValidateCircuitLabRamReplica`。已通过 128 周期 × 1127 节点的 144,256 位 GPU 对照，以及 256 个低/高相位样本、1020 个有效端口值的独立 RTL 波形核对。实际 GPU π 输出 `3.141592\n`，九次 OUT、全部 256 字节末 RAM 与参考一致；256 Hz 下约 8–9 秒完成，终端读回错误和漏采均为 0。

HEX/BIN 及注释/前缀加载、非法输入不改变程序、RESET 保留 RAM/取消待写、RESTART 初始化 RAM、RAM 探头与冻结均经实际 Udon/GPU 验证。独立副本恢复 1127 节点、77 DFF、512 字程序与 256 字节 RAM，随后本地计算追赶 16 周期并验证实际 RAM 写入。报告位于 [results/circuit-lab](results/circuit-lab)；真实多人传输和原生 PC/VR 输入尚需客户端验收。

```sh
# 新一轮 PlayMode（默认暂停/LIVE）中执行
python tools/run_circuit_lab_checks.py --suite ram
python tools/run_circuit_lab_checks.py --suite gpu
```
