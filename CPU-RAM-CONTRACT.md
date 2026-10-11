# CPU/RAM 升级的协作接口

本轮实现目标：8 位 ACC 与 X，256 字节 RAM，512×12 位可装载程序，实际整数 π 计算。RESET 保留 RAM 并禁止复位拍写入；加载/RESTART 初始化 RAM。代码、RAM 与电路状态均在 world 本地 GPU 使用。

用户最新约束：后续子代理全部使用 `gpt-6-luna`、`reasoning_effort=max`。`~/tmp/simple-cpu-pi` 作为只读参考，不改该目录。参考的 355/113 程序执行真实十进制长除法，前六位小数与π一致，第七位起不一致，必须在面板/描述标明近似法和精度；优先移植这一小型、快速演示，尽可能让整数商和初始余数也由CPU从355/113求出。默认输出六位小数。参考 Machin 程序的级数项为汇编期常量，只有16位求和在CPU运行，不能将其描述成CPU运行完整反正切级数；可作为带明确名称的多字算术演示，完整Machin生成算法为可选增强，不阻碍本轮真实长除法π演示验收。

## HDL 与导出

- 核心仍为 `teaching_cpu`，单上升沿 `clk`、同步 `reset`。PC 9 位、指令 12 位、ACC/X/OUT 8 位。保留旧 opcode 0–7/F；8=LD、9=ST、A=ADD RAM、B=SUB RAM、C=ADC RAM、D=SBC RAM、E=扩展指令组。扩展组容纳移位、X 访存/传送/计数、进位条件跳转与长地址 CALL/RET，具体编码由 HDL 负责人确定并导出 ISA 元数据。
- 原有指令语义保持；直接 JMP/JZ 使用低8位。LOAD/STORE 保留 C，SUB/SBC 的 C=无借位。扩展长地址使用第二个12位字，低9位为程序地址。
- ROM/RAM 在综合时保留为边界明确的存储器原语（可用综合 blackbox + 单独的等价 RTL 行为模型）；CPU 控制、寄存器、ALU 仍映射基础门。综合网表测试必须连接同语义存储器模型，不能替换 CPU 执行结果。
- schema 升为 3。沿用 nodes/modules/signals/ports/dffSignals/inputMasks/expectedStates 等原有字段。INPUT offset 由真实顶层输入决定，clk=0/reset=1；程序选择属于宿主加载状态，不要求硬件有 program_select。
- 新节点 op=15 ROM_READ、op=16 RAM_READ：a=输出位下标，b=c=0。组合依赖是对应读地址总线，不是 a/b/c 信号。
- `memoryPorts` 字段：`romAddress`（9个信号ID）、`romData`（12个）、`ramReadAddress`（8个）、`ramReadData`（8个）、`ramWriteAddress`（8个）、`ramWriteData`（8个）、`ramWriteEnable`（一个ID）。metadata 中信号位序均低位在先。
- `programs` 是对象数组：id/name/description/words（512个12位整数）/initialRam（256个字节）/expectedText/expectedCycles（适用时）。ISA 元数据也导出到 `isa`。
- fixture 需支持 `fixturePrograms`（每vector加载的program id）、`fixtureRamReset`（本vector之前是否恢复该程序的initialRam）。普通reset mask不初始化RAM。限制 fixture 大小，完整π由RTL/综合网表完整执行验证，GPU完整执行以输出/末态独立验收。
- 顶层 `ports` 保留 pc/instruction/phase/acc/out/zero/carry/halt/terminal_*；新增 x/sp/微步等可用信号据实导出。phase 可为多位：保留 `phaseSignal` 兼容字段作为首位，增加 phaseSignals 供精确显示。

## Unity GPU 公共接口

在 CircuitLabSimulator 增加：

- `Material ramMaterial`；`Texture2D programTexture/ramInitialTexture/restoreStateTexture` 与 `RenderTexture ramA/ramB/currentRam`。programTexture=512×1 RGBA32 linear Point，R=word低8位、G=高4位；ramInitialTexture=16×16 RGBA32 linear Point，R=byte；restoreStateTexture=textureWidth×1 RGBA32 linear Point，按node ID的R=恢复DFF位0/255，其余为0。地址ID用向量uniform，不依赖memoryAddressTexture。
- `int[] romAddressSignals,ramReadAddressSignals,ramWriteAddressSignals,ramWriteDataSignals`；`int ramWriteEnableSignal`；`int[] xSignals,spSignals,phaseSignals`。
- 程序数据：`int[] programWords`（512）、`byte[] initialRam`（256）、`int[] presetProgramWords`（count×512）、`byte[] presetInitialRam`（count×256）、`string[] programNames/programDescriptions`、`int programCount`、`string activeProgramName`、`int programRevision`。`programSelect` 是已加载预置索引，自定义使用-1。允许额外字段，通知其他负责人。
- `bool LoadProgram(int[] words, byte[] ram, string name, int presetId)`：只在控制者处接受，排队到稳定边界原子加载，复位CPU和历史/终端，并初始化RAM；programRevision增长。
- `void LoadPreset(int id)` 同上；已有 SelectProgram 依次切换预置。
- 纹理里的RAM一个像素一个字节、16×16、线性ARGB32且Point采样。CPU和RAM更新均读取同一份上升沿前稳定状态；两者提交后才稳定/采样。显式禁止reset拍RAM写入。
- 普通 Run/Pause/Step/Freeze/RESET 接口保留。目标速度 .25–256 Hz，主控制者和跟随者均按每帧预算执行，避免丢周期；公开 actualCyclesPerSecond、catchupLag 等可见遥测。
- 平板遥测：GetObservedSignal(int)、observedRam[256]/observedRamStable/observedRamCycle，以及 observedRamReadAddress/ReadData/WriteAddress/WriteData/WriteEnable。phaseSignals存在时observedPhase读取完整bus。
- 全部DFF检查点取消64位限制；共享程序、initialRam、RAM检查点与程序revision/name，同一周期一致捕获。现有终端、探头、冻结历史及控制权交接继续工作。
- 数值纹理不得伪造计算结果。RAM探头使用虚拟测点ID `nodeCount + address*8 + bit`，由GPU历史shader在currentRam读取；真实电路ID仍0..nodeCount-1。虚拟ID不加入综合网表nodes，需要在类型/标签中明确RAM来源。

## 所有权与操作

- HDL负责人：circuits/、tools/build_teaching_cpu.py 及新的汇编/程序生成工具；不操作Unity。
- GPU负责人：Simulator/NetworkCoordinator/NetworkClock，现有计算/历史/显示shader及新的RAM计算shader；不改Builder，不操作Unity。
- 场景负责人：BuildCircuitLab、新ProgramPanel/StateTablet及其显示shader；不改GPU或HDL负责人的文件，不操作Unity。
- root：集成、真实Unity操作、必要验证脚本、部署工具、文档和Git/GitHub同步。其他负责人均不要部署或推送。
- 所有负责人不是独自在代码库工作，不得回滚他人的修改；接口改变先发消息。
