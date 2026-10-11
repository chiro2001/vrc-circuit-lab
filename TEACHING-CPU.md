# 8 位教学 CPU 与层次网表

CPU 源码为 [circuits/teaching_cpu.v](circuits/teaching_cpu.v)，构建入口为
[tools/build_teaching_cpu.py](tools/build_teaching_cpu.py)。它把一个可逐拍观察的小型 CPU
综合为基础门，同时保留实例树，供电路实验室的层次浏览、模块取出和信号探针使用。

## 执行规则

顶层模块 `teaching_cpu` 的输入为 `clk`、`reset`、`program_select[1:0]`。
输出 `pc[3:0]`、`instruction[11:0]`、`phase`、`acc[7:0]`、`out[7:0]`、
`zero`、`carry`、`halt` 是真实电路信号。向量的信号 ID 列表均按低位到高位排列。

所有状态都在同一个时钟的上升沿更新，复位为同步复位，优先于写使能。
复位结果为 PC、指令、ACC、OUT、phase、carry、halt 清零，zero 置一。

每条指令分两拍：

1. `phase=0` 表示 FETCH；下一次上升沿读取 `ROM[pc]`，锁存指令，进入 EXEC。
2. `phase=1` 表示 EXEC；下一次上升沿提交运算、输出或跳转，然后返回 FETCH。

PC 在 EXEC 提交时递增或跳转，在 FETCH 时保持。HALT 提交后 PC 和所有状态保持，
phase 保持为 1；再次同步复位可恢复执行。`program_select` 选择三套 16×12 位 ROM，
FETCH 使用该拍输入的选择值；切换示例程序时通常同时复位。

指令高 4 位为 opcode，低 8 位为立即数。跳转使用立即数的低 4 位。

| Opcode | 指令 | EXEC 操作 |
| --- | --- | --- |
| 0 | LDI imm | ACC ← imm |
| 1 | ADDI imm | ACC ← ACC + imm，保留第 9 位进位 |
| 2 | ANDI imm | ACC ← ACC & imm |
| 3 | ORI imm | ACC ← ACC \| imm |
| 4 | XORI imm | ACC ← ACC ^ imm |
| 5 | OUT | OUT ← ACC |
| 6 | JMP addr | PC ← addr |
| 7 | JZ addr | zero=1 时跳转，否则 PC 递增 |
| F | HALT | 停止提交 |

LDI、ADDI、ANDI、ORI、XORI 更新 zero；只有 ADDI 可以置 carry，其他四条清 carry。
OUT、跳转和 HALT 保持两个标志。8～E 作为空操作处理，PC 正常递增。

## 两套示例程序

`program_select=0` 为计数循环：先 `LDI 0; OUT`，再循环执行
`ADDI 1; OUT; JMP 2`。每六个上升沿输出增加一，8 位溢出时变为零并置进位。

`program_select=1` 为逻辑与条件跳转示例：

| 地址 | 指令 | 结果或作用 |
| --- | --- | --- |
| 0 | LDI A5 | ACC=A5 |
| 1 | ANDI 0F | ACC=05 |
| 2 | ORI 80 | ACC=85 |
| 3 | XORI 85 | ACC=00，zero=1 |
| 4 | JZ 6 | 跳过地址 5 |
| 5 | LDI FF | 被跳过 |
| 6 | ORI 3C | ACC=3C |
| 7 | OUT | OUT=3C |
| 8 | ADDI C4 | ACC=00，carry=1，zero=1 |
| 9 | JZ B | 跳过地址 A |
| A | LDI EE | 被跳过 |
| B | OUT | OUT=00 |
| C | LDI 55 | ACC=55，carry=0 |
| D | XORI FF | ACC=AA |
| E | OUT | OUT=AA |
| F | HALT | PC=F，phase=1，halt=1 |

`program_select=2` 为文字输出程序，实际执行 LDI/OUT，输出六个 ASCII 字节 `HELLO\n`。
两个 `L` 来自地址 5、6 的独立 OUT 指令；第二次 OUT 没有改变 ACC 或 OUT 数据，仍产生事件。
选择值 3 的 ROM 全为 HALT，不产生字符写入。

## 字符写事务

`terminal_data[7:0]` 与 `out[7:0]` 共用实际信号 ID。
`terminal_write` 是真实 RTL 注册的一拍写脉冲：EXEC OUT 的上升沿提交后为 1，
下一个上升沿在没有 OUT 写入时归零。`terminal_sequence[7:0]` 每次 OUT 递增，模 256 回绕。
同步复位同时清零写脉冲、序号和数据；HALT 不产生写入。

终端在上升沿提交后的高电平稳定状态读取 `terminal_write=1` 的事件，每次追加一个字节，
不以数据是否改变作为判断条件。低电平采样可能仍看到上一拍写脉冲和序号，不能再次追加字符。
需要同时采样多个相位时，可用序号去重；复位后的序号从零重新开始。

## 保留的模块结构

```text
teaching_cpu
├── rom                  teaching_rom
├── control              teaching_control
├── pc_unit              teaching_pc
├── registers            teaching_registers
├── output_unit          teaching_output
└── alu                  teaching_alu
    ├── bit_and          teaching_and8
    ├── bit_or           teaching_or8
    ├── bit_xor          teaching_xor8
    └── adder            teaching_adder8
        └── fa0～fa7     teaching_full_adder（八个独立实例）
```

ROM 为组合 case 逻辑，不使用存储器单元。综合不执行 flatten，ABC 在各模块内映射基础门。
`dffunmap` 将同步复位和写使能展开成 MUX + `$_DFF_P_`。
导出器拒绝锁存器、存储器、未知门型、组合环和非顶层统一时钟。

## 生成与验证

本地需要 Python 3、Yosys、Icarus Verilog：

```sh
python tools/build_teaching_cpu.py
```

默认输出到 `~/tmp/vrc-cpu-map/circuit-lab/hdl/`，子进程的 `TMPDIR` 也设在该目录。
可用 `--output <目录>` 修改路径。主要结果为：

| 文件 | 内容 |
| --- | --- |
| `gpu-netlist.json` | schema 2 网表、层次元数据、全部节点 GPU 参考状态 |
| `netlist.json` | Yosys 原始层次 JSON |
| `synthesized.v` | 可由 Icarus 再次仿真的层次综合网表 |
| `test-report.json` | 测试结果与规模统计 |
| `waveform-reference.json` | GPU fixture 每拍低、高电平的顶层 RTL 参考 |
| `rtl-*.csv`、`gate-*.csv` | 完整与 fixture 的顶层逐拍/半拍轨迹 |
| `*-compile.log`、`*-test.log`、`yosys.log` | 工具日志 |

测试包括全部 327680 组 ALU 运算、全部 131072 组带输入进位的加法、32 组控制译码
（16 个 opcode × 两种 zero）、256 组 PC 控制，以及 2234 拍 CPU 程序与复位测试。
长计数测试覆盖 ACC 从 FF 溢出到 00；逻辑程序覆盖两次条件跳转、进位、OUT、HALT 保持；
文字用例检查 `HELLO\n` 六次写入、重复字符、HALT 保持和待提交 OUT 被复位取消；
确定性随机序列覆盖运行中同步复位和全部 ROM 选择。RTL 和综合网表分别执行这些测试，
并逐字节比较输出 CSV，包括低、高电平采样。

GPU fixture 是另外连续执行的 946 拍短测试，保留 512 拍计数、完整逻辑/文字程序与随机序列。
Python 网表解释器先稳定组合逻辑，再同时提交所有 DFF，再次稳定；它逐拍检查全部顶层状态，
也检查低电平参考，然后生成每拍所有节点的二值参考。独立 RTL 半拍参考共 1892 个采样。
第一个低电平采样标为 `valid=false`，因为 GPU 的初始全零状态尚未经过同步复位；
第一个高电平及后续所有采样都有效。

2026-10-11 实际验证通过，使用 Yosys 0.66 与 Icarus 13.0：524 个节点、19 个模块实例、
45 个 DFF、纹理宽度 1024、组合最大深度 25、稳定迭代次数 26。

## schema 2 接口

保留旧仿真指令编号：0 CONST0、1 CONST1、2 INPUT、3 DFF、4 BUF、5 NOT、6 AND、
7 OR、8 XOR、9 XNOR、10 MUX、11 NAND、12 NOR、13 ANDNOT、14 ORNOT。
MUX 为 `c ? b : a`，ANDNOT 为 `a & ~b`，ORNOT 为 `a | ~b`。

- `nodes[id]` 与 signal ID 一一对应。0、1 为共享常量；节点含 `id/op/a/b/c/name/level/moduleId`。
  INPUT 的 `a` 是输入 mask 中的偏移，DFF 的 `a` 是 D 输入信号 ID。level 为全局组合深度，
  输入、常量、DFF 为 0。
- `ports` 将顶层端口名映射为信号 ID 列表。`inputOffsets` 固定为
  `{ "clk": 0, "reset": 1, "program_select": 2 }`。
  program_select 占 mask 的 bit 2、3；mask 最多四个输入位。
- `modules` 是完整实例树，ID、parentId、moduleId 都是整数。根 ID=0，parentId=-1。
  每项含 `path/type/name/ports/children/nodes`；ports 是 `{name,direction,signals}` 列表，
  nodes 只包含该实例实际驱动的节点。
- `signals` 只包含综合网表实际保留的网名，键如 `teaching_cpu/alu/adder/fa0/propagate`。
  不为已优化消失的 RTL 信号伪造探针。模块端口别名共享同一个 ID；跨模块连接不会重复状态。
- `inputMasks` 每项代表一次完整上升沿，clk 位为 1；模拟器执行低电平稳定、上升沿提交、
  高电平稳定。`expectedStates` 按 `vectorCount × nodeCount` 展平，每项为 0/1 字节整数，
  对应上升沿后的稳定状态。
- `dffSignals` 按 signal ID 排列全部 45 个 DFF，用于保存和恢复完整 CPU 状态。
  `outputWriteSignal` 是 terminal_write 的信号 ID，`outSignals` 是输出数据的低位到高位 ID 列表，
  `terminalSequenceSignals` 是事务序号。`terminalTextFixture` 给出文字程序的首末 vector index
  （从 0 开始，包含端点）和六个预期字节。
- `waveform-reference.json` 的 sample 含 `cycle/half/clk/pc/instruction/phase/acc/out/zero/carry/halt/valid`，
  并包含 `terminal_data/terminal_write/terminal_sequence`。
  cycle 从 1 开始；half=0 表示提交前低电平，half=1 表示提交后高电平。

别名归属由实际驱动实例决定：例如顶层 `acc` 和 `registers/acc` 共用 ID，节点的
moduleId 属于 registers。父模块中的端口是浏览与连线的边界，父模块不复制子模块的驱动门。
