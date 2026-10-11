# 完整 LCVEX SoC 的 GPU 电路实验室可行性评估

评估日期：2026-10-11。冻结源码 SHA：`c7e44bd2afc4198e64e705cbea7495286eac569e`。
可复现入口：[tools/evaluate_lcvex_gpu.py](tools/evaluate_lcvex_gpu.py)。

## 分阶段结论

| 目标 | 本次实际结果 | 尚需完成 |
| --- | --- | --- |
| 导入真实 RTL 层级浏览 | 完整 FP/NEON/MMU/cache SoC 的 34 个实例成功展开；板级功能模型共 38 个实例 | 把 RTL 实例清单接入浏览器；底层真实门浏览仍需完整 SV 综合和层次网表导出 |
| 裸机程序运行 | 真实控制模块门级综合成功，发现当前模拟器不支持的多时钟/异步复位单元；未做整机 GPU 运行 | 多时钟事件调度、异步复位、RAM/FIFO 功能块、板级总线模型、镜像加载、commit/UART 对照 |
| Linux 启动 | full-FP + 128 MiB 板级模型的 elaboration 成功；没有执行 Linux，更没有 GPU 启动证据 | 精确 RAM/Flash/UART/中断/定时器模型，足够长的裸机与 Linux 差分验证、运行性能实测 |

当前教学 CPU 的 schema 2 和门模拟器不能直接承载完整 SoC。可以先接入真实实例树浏览，
然后以同一冻结源码建立完整综合/功能块后端。完整 gate count 在本次报告中为 `null`，
因为当前机器的综合前端未能读完真实完整 RTL；不会把子模块或 stub 统计当成整机规模。

## 源码边界和隔离

已阅读 LCVEX 的 `AGENTS.md`、`docs/MULTI_AGENT_WORKFLOW.md`。
主仓库为 `/home/chiro/projects/mycpu/lcvex`，只读使用。
实验在独立 direct sibling worktree：

```text
task:     T-20261011-vrc-gpu-eval
branch:   verify/T-20261011-vrc-gpu-eval
worktree: /home/chiro/projects/mycpu/lcvex-wt-T-20261011-vrc-gpu-eval
base:     c7e44bd2afc4198e64e705cbea7495286eac569e
```

所有生成结果位于 `~/tmp/vrc-cpu-map/lcvex-gpu-eval/`，`TMPDIR` 指向其中的 `tmp/`。
ABC 通过独立 wrapper 在任务临时目录运行，避免在 worktree 留下 `abc.history`。
重型命令逐个取得 `/home/chiro/projects/.resource-locks/resource-lock` 的 `local` 独占锁，
准入门槛为 MemAvailable ≥ 4096 MiB，单线程、串行实验。该门槛用于保持交互余量；
实际前端内部 allocator 约 85～87 MB，未进行重型整机 C++ 编译或 Linux 长跑。
报告记录每条命令的真实起止时间、exit code、资源锁、日志哈希和全部输入文件哈希。
最后验证源码哈希不变，worktree 前后 Git 状态均为空。

## 实际前端实验

可用工具为 Yosys 0.66、Icarus 13.0，以及
`/home/chiro/miniforge3/envs/lcvex/bin/verilator` 的 Verilator 5.050。
PATH 中没有 slang、sv2v、Surelog；加载 Yosys slang 插件也实际失败。
Verilator 5.050 提供 `--json-only`，本次使用它输出参数展开、类型确定后的真实 RTL AST，
没有将 AST 冒充可执行基础门网表。

| 实验 | 结果 | 证据 |
| --- | --- | --- |
| `yosys: plugin -i slang` | FAIL：`slang.so` 不存在 | `slang-plugin-probe.log` |
| 原生 `read_verilog -sv -formal` 读取 `rtl/filelist.f` | FAIL：package 读取成功，随后 `lcvex_fp_state.sv:46` unpacked-array 端口语法失败 | `direct-full-soc.log` |
| 真实 `lcvex_calibration_gate.sv` 基础门综合 | PASS：11 cell，含 6 个异步复位 DFF | `calibration-gate.log`、`calibration-gate.json` |
| 完整 `lcvex_catapult_soc_top` JSON elaboration | PASS：34 实例、34 参数特化模块 | `full-soc.log`、`full-soc-inventory.json` |
| 板级 TB，`LINUX_BOOT_TEST=1`、显式 `A64_FP_SIMD=1` | PASS：38 实例、37 参数特化模块，128 MiB DDR 模型 | `full-fp-board-128m.log`、`full-fp-board-128m-inventory.json` |

本次完整 SoC 的参数为 BRAM=1 MiB、L1 sets=64、L2 sets=256、ways=2、
`A64_FP_SIMD=1`，保留 core、FP state/execute/scalar/divider/sqrt、NEON integer、MMU、
I-L1、D-L1 writeback、L2 writeback、GIC、AXI/Avalon、CDC FIFO 等真实模块。
FP/NEON 浮点执行路径由 `lcvex_fp_exec` 与 `lcvex_fp_scalar` 等模块实现，未关闭。
顶层实际有 116 个端口，795 输入位、1903 输出位；当前 32 位 input mask 也需扩展。

板级实验读取仓库正式 `fpga/catapult_a10/tb/filelist_soc.f`，
`lcvex_catapult_soc_tb` 原有 Linux 分支默认 no-FP，本次显式覆盖为 full-FP。
TB 中 SoC 使用 BRAM=64 KiB、L1 sets=64、L2 sets=64、ways=1，
DDR_MODEL_DEPTH_WORDS=`1<<21`，每项 512 位。两套参数各自记录，不能混为一个配置。
BOOT_HEX_FILE 为空，本次只检查展开与模型容量，没有加载或运行内核镜像。

真实校准模块的门级结果为：3 OR、1 NOT、1 AND、6 `$_DFF_PN0_`。
它同时使用 CPU 和 EMIF 时钟，以及低有效异步复位。
当前教学网表只接受统一时钟的 `$_DFF_P_`，因此最小真实边界模块已经显示出语义缺口。

## 规模证据与统计口径

完整 SoC 的 AST 文件为 24,238,265 字节，SHA-256：
`dd2d1a10730792e78abf28a1ff535dc9e984cba245cfe3a41c57615285faec02`。
板级 full-FP AST 为 22,396,840 字节，SHA-256：
`d18bdb1abdecb2339fcd93746bdbe256f0418001ae934422882a722a6e9eaba6`。
前端命令约 0.8 秒完成，Verilator 内部 allocator 分别约为 86.7 MB、85.2 MB。
这些是展开前端的实际规模与耗时，不表示完整门综合、仿真速度或进程峰值 RSS。

评估脚本沿实际 CELL 引用递归实例化，解析 packed struct、packed/unpacked array 的确定类型宽度，
并识别非阻塞赋值的目标变量。自动函数/任务局部变量不计入状态清单。
结果中，完整 SoC 的非数组 NBA 目标声明合计 50,537 位，unpacked array 声明合计 8,831,620 位；
板级模型对应为 54,416 位和 1,130,899,492 位。
这些是 RTL 声明和 NBA 目标的容量清单，含观测状态与可能重复的端口别名，
未经综合优化；既不能视为 FF 数量，也不能视为 gate count。
清单逐项保留实例路径，便于后续核实真实 RAM、寄存器堆、cache tag/data 与观测数组。

最大存储对象已通过实际展开确定：

| 对象 | 实际容量 |
| --- | --- |
| `lcvex_catapult_soc_top/bram/u_impl/mem` | 8,388,608 位，即 1 MiB |
| 默认 SoC 的 `coh/l2/u_data_ram/mem` | 262,144 位，即 32 KiB |
| 板级 `emif_model/mem` | 1,073,741,824 位，即 128 MiB |
| Linux TB `flash_lines` | 48,000,000 位，即 6,000,000 字节模型空间 |
| Linux TB `jtag_chars` | 8,388,608 位，即 1 MiB 测试日志空间 |

Flash/JTAG 日志数组属于功能模型或测试观测，不能作为真实芯片 RAM 资源相加。
最关键的 128 MiB RAM 为真实 DDR 功能容量，与 FPGA 片内 RAM block 数量无关。

若将 128 MiB RAM 每一位展开成独立门状态 texel，仅双缓冲状态的下限为：

| 状态格式 | 每 bit 两份状态的成本 | RAM 部分成本 |
| --- | --- | --- |
| ARGB32，当前教学模拟器一 bit 一个 texel | 8 字节 | 8 GiB |
| RFloat，一 bit 一个 texel | 8 字节 | 8 GiB |
| RGBAFloat，一 bit 一个 texel | 32 字节 | 32 GiB |
| 紧凑 bit-packed 功能 RAM | 一 bit 一 bit | 单份 128 MiB |

以上未包含存储器译码/读选择门、门指令纹理、几何/连线、纹理 padding、历史波形或网络快照。
把存储器映射为上十亿个 DFF 再全表稳定迭代不可作为实用方案；需保留 RAM/FIFO 功能块，
按字/字节使能访问紧凑存储，只展开正在观察的门电路局部。

## 板级 IP 与功能模型

`rtl/lcvex_catapult_soc_top.sv` 到 Avalon/EMIF、JTAG-UART、EPCQ 的接口是真实逻辑，
厂商硬 IP 和板级时钟/复位属于上层平台。
正式 RTL 在 SYNTHESIS 分支中使用 `altera_syncram`，仿真分支保留仓库的行为 RAM。
浏览器可以显示 IP 边界；可执行模型必须保留接口契约，不能把输入 tie-off 为零后声称可以运行。

| 边界 | 运行所需模型 |
| --- | --- |
| BRAM/cache RAM | 已有行为模型的读延迟、写字节使能、初始化、未对齐及故障语义；综合 IP 的碰撞语义另行核对 |
| EMIF/Avalon | 512 位 DDR RAM、读返回/等待/校准握手、byteenable、地址窗口，明确 CPU/EMIF 时钟事件 |
| CDC FIFO | 各域上升沿、同步器和复位释放时机，不能只用一次单时钟 DFF 提交代替 |
| JTAG-UART | 真正的读/写接受、TX/RX FIFO、等待、IRQ 和写事务；同字节重复发送必须保留 |
| EPCQ Flash | 初始化镜像、地址 aperture、等待/返回、超时/故障；仅读取或缓存初始化文件不足以模拟总线 |
| GIC/Generic Timer | 真实寄存器、IRQ 路由、时钟或计数源及中断处理路径 |

SoC 顶层至少存在 `clk` 与 `emif_clk` 两个实际时钟域；`cpu_clk/wr_clk/rd_clk`
是子模块局部命名，清单列出的名字不能直接当成五个独立物理域。
调度器需要解析时钟别名、按时间事件分别触发各域，并对同一时点的寄存器更新保持原子提交。
异步复位须在对应边沿即时生效，不能用同步 reset MUX 冒充。

## 历史证据的使用限制

`fpga/opensynth` A1 只统计选定子模块，A2 为 0-cell boundary stub。
它们证明部分工具链/接口可处理，不能给出完整 SoC 门数或 Linux 执行结论。
当前 Yosys 0.66 的实际失败点是 unpacked-array 端口；不会照抄旧版本“所有 package 都不支持”的结论。

仓库 `docs/handoffs/T-20260902-010-a10-full-fp-synth-merge.md` 记录了历史 full-FP 综合：
旧 SHA `87777425…`，L2 64×1，估计 482,334 ALM、720,722 ALUT、80,925 registers、
247,276 block-memory bits、186 DSP。它说明 full-FP 的 FPGA 资源很大，
不代表本次冻结配置，更不能线性换算 GPU 基础门数。

`docs/handoffs/T-20260928-002-a10-linux-boot.md` 记录的板级 Linux 已进入 `/init`，
并通过 JTAG-UART help/echo/sleep，资源为 78,190 ALM、82,961 registers、121 RAM blocks、21 DSP。
该候选明确是 `A64_FP_SIMD=0` 的 no-FP profile，不能作为当前 full-FP 配置或 GPU Linux 的验证。
该硬件证据仍可用于未来逐阶段对照 Linux/板级接口行为。

## 后续验收路径

1. 用真实 AST 实例路径做模块浏览与总线探针；保留“RTL 层级”与“综合门电路”类型信息。
2. 在同一冻结源码安装并实测支持完整 SV 的综合前端，保留模块边界，先输出 RTLIL/层次统计。
   把 RAM/FIFO/IP 边界分离为有版本与契约的可执行功能块，再确定可支持的门型与真实 gate count。
3. 对多时钟、异步复位、RAM byte-enable/背压进行定向对照，随后跑真实完整 full-FP 裸机程序，
   按 commit PC/寄存器/异常/内存副作用和 UART 事务检查。
4. 最后使用同源 kernel/DTB/loader、完整 128 MiB RAM 与 IRQ/Timer 模型推进 Linux，
   记录实际每秒周期数、显存、UI 帧率、程序里程碑；GPU 启动结果通过后再讨论可用速度。

当前上限为 64 个 FF 状态位的教学 CPU 网络快照也无法表示整机状态。完整 SoC 需要更大的寄存器状态、
RAM 初始化/脏页或 checkpoint 机制、时钟事件序号与终端事务序号；网络同步方案须在整机运行后单独验收。

本次 Unity 实测 `SystemInfo.maxTextureSize=16384`。生产模拟器仍使用一行状态纹理，
其信号数量还受这个维度上限限制。独立 `BenchmarkCircuitLab` 用二维纹理测试
1024 / 10000 / 100000 个合成节点，均完成 16 周期、每周期 65 次 Blit，零差异。
RTX 5080 / D3D11 的短批次提交至 GPU 读回完成约 20–23 ms，固定提交开销占比高，
不据此判断门数增加时更快，也不能作为实际 LCVEX 运行速度。
[独立规模报告](results/circuit-lab/scale-benchmark.json)保留测量口径和限制。

## 复现

若独立 worktree 尚未创建：

```sh
git -C /home/chiro/projects/mycpu/lcvex worktree add \
  -b verify/T-20261011-vrc-gpu-eval \
  /home/chiro/projects/mycpu/lcvex-wt-T-20261011-vrc-gpu-eval \
  c7e44bd2afc4198e64e705cbea7495286eac569e
```

在 vrc-cpu-map 中执行：

```sh
python tools/evaluate_lcvex_gpu.py
```

脚本自动使用 local 锁，每条命令最多 300 秒；不运行仓库已有会改写 build/IP 的生成入口。
结果为 `~/tmp/vrc-cpu-map/lcvex-gpu-eval/evaluation.json`、两个 `*-inventory.json`、
AST/类型元数据、真实叶模块门网表和日志。保存源 SHA、工具版本与结果哈希即可复查此次判断。
