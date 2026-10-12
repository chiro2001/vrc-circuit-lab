# CPU / GPU / UI 接口

本轮保持 8 位 ACC/X/SP/OUT、9 位 PC、512×12 位 ROM，RAM 升至 1 KiB。仿真与 RAM 写入全部在 world 本地 GPU 执行；Udon 负责时钟、交互、加载和观测，不计算 CPU 程序结果。

## RAM 与 BANK

- RAM 为四个 256 字节页，BANK 是两位寄存器。普通 LD/ST/ADD/SUB/ADC/SBC 和 LDAX/STAX 访问 `BANK×256+offset`。
- `BANK 0..3` 分别编码 `E21..E24`，`E20` 仍为 TAX。BANK 不改 ACC/X/SP/Z/C。
- CALL/RET 栈固定页 0，BANK 不受调用影响。
- RESET 复位 CPU 与 BANK=0，保留全部 1024 字节 RAM，复位沿禁止 RAM 写入。LOAD/RESTART 恢复所加载程序的 initialRam。
- SUB/SBC 的 C=1 表示无借位；SBC 减去 `(1−C)`。

## 导出数据与 GPU

- schema3 保留综合网表节点和模块层级。ROM_READ=15、RAM_READ=16 是真实存储器边界，其地址依赖来自 memoryPorts。
- `memoryPorts.ramReadAddress/ramWriteAddress` 各 10 个信号，低位在前：bit0..7 偏移、bit8..9 BANK。`ports.bank` 与顶层 `bankSignals` 是两个 BANK 信号。
- 程序 `words` 长 512，`initialRam` 长 1024；`presetInitialRam` stride 1024。名称/说明/注释译文数组顺序 en、zh、ja。
- 程序 ROM 纹理 512×1：R=低 8 位，G=高 4 位。RAM 纹理 32×32：每像素 R=一个字节，地址=`y×32+x`，Point、linear RGBA32/ARGB32。
- DFF 状态、RAM 状态和 ROM 数据纹理分开。CPU 与 RAM 同时读取上升沿前状态，提交后再组合稳定和采样。
- 检查点保存全部 DFF、1024 字节 RAM、完整程序与初始 RAM、周期及修订；恢复后由 GPU 追赶。
- RAM 虚拟探针 ID=`nodeCount+address×8+bit`，覆盖 0..1023，历史 shader 从 RAM 纹理取值。
- Simulator 提供 `bankSignals/observedBank`，`observedRam` 为 1024 字节；`GetRamByte(address)` 仅返回稳定 GPU 观测，未稳定或越界为 -1。
- 速度范围 .25–256 Hz。高精度 π 使用软件整数算术，RAM 扩大不会缩短执行周期。

## 显示与本地语言

- RAM 平板每页显示 256 字节；page=0..3，地址、探针选中与共享状态均为绝对地址。Raw 显示完整 32×32 纹理。
- LED shader 直接读取 OUT 信号；生命游戏 shader 直接读取 CPU 双缓冲 RAM。显示脚本不执行 Conway 规则。
- Life buffer A=0x100..0x13F，B=0x140..0x17F；RAM[0xE0] 是前台选择 0/1，RAM[0xE1] 是代数 mod256。CPU 写完后台，再翻转前台，再递增代数。规则 B3/S23、8×8、封闭边界。
- ISA 页码、程序、电路、探针、波形、布局继续共享；语言 AUTO/中/日/英仅本地。`VRCPlayerApi.GetCurrentLanguage()` 为 AUTO 来源，`OnLanguageChanged` 更新；ClientSim 不完整模拟热切换回调。
- `CircuitLabLocalization.languageIndex` 为 0 英 / 1 中 / 2 日，override=-1 AUTO。手动选择不需全局控制。
- 原 Text 保留为隐藏数据源，TMP SDF 负责显示；输入框保留 uGUI InputField。源码与机器码、助记符、信号路径、终端字节不改写。

## 协作与验收

临时目录使用 `~/tmp/vrc-cpu-map/` 与远端 `~/tmp/vrc-cpu-world/`。Unity 管理的场景、纹理、字体资产和材质必须通过 Pipeline 制作。升级保留地板和已有位姿。各负责人不能回滚其他人的修改，不自行部署或提交。root 负责真实 Unity 集成、适当检查、作者状态恢复、资产采集和两端 GitHub 同步。
