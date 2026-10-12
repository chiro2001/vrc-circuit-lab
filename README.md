# VRChat CircuitLab

公开仓库：https://github.com/chiro2001/vrc-circuit-lab

在 VRChat world 内使用 GPU 仿真综合后的门级网表，并通过可移动的模块、实体探头和逻辑分析仪观察电路。当前示例是带 1 KiB 分页 RAM 的 8 位教学 CPU，支持层级展开、子模块取出、连续记录、手动冻结、字符终端和共享 HEX/BIN 程序编辑。默认演示由 CPU 执行 355/113 长除法输出 `3.141592`；另有真实计算 64/100 位小数的 π 程序。仿真只记录逻辑状态与稳定时钟相位。

仿真在各玩家本地 GPU 执行，共享控制、检查点、波形及物体状态。空闲实验台的首次控制点击会取得全局控制权；已有操作者时，其他玩家使用分析仪顶部的 `REQUEST CONTROL` 申请，由操作者通过 `GRANT REQUEST` 交接。

程序编辑板展示当前运行名称和 ROM 的 HEX/BIN/反汇编；独立列表面板提供 24 个预置的源码/注释与编码预览，ASM 源码用 IBM Plex Mono 全量显示并可上下滚动（HEX/BIN 分页），点击加载按钮才切换 ROM。独立 ISA 手册有 39 页；LED 读取实际 OUT，8×8 生命游戏由 CPU 更新双缓冲 RAM。关于面板展示项目介绍和公开 GitHub 地址，右下角内嵌每位玩家本地的中/日/英和 AUTO 语言选择。状态平板可查看实际电路、门数据、ROM 和独立 32×32 RAM 纹理，并绑定 RAM 探头。速度范围为 0.25–256 周期/秒；RESET 保留 RAM，RESTART 和加载程序恢复初始化 RAM。

## 打开工程

仓库根目录就是 Unity 工程，使用 **Unity 2022.3.22f1**。将克隆目录添加到 VRChat Creator Companion，通过 Manage Project 恢复 `Packages/vpm-manifest.json` 锁定的 VRChat Base / Worlds SDK **3.10.5** 和 VPM Resolver **0.1.29**，再打开 `Assets/CircuitLab/Scenes/CircuitLab.unity`。

场景包含预制三语文字，超过 GitHub 普通文件大小限制，使用 Git LFS 保存。克隆前安装 Git LFS，克隆后运行 `git lfs pull`，确认场景是完整 Unity YAML 文件后再打开工程。

场景、网格、材质、Udon 程序、原始 `.meta` 和 ProjectSettings 均纳入 Git。VRChat SDK 由 VCC 下载，VPM Resolver 按 VCC 默认规则保留在仓库；Unity Pipeline 的 2022 本地兼容版本保存在 `Packages/com.unity.pipeline`，更新时必须保留兼容补丁。

## 开发与同步

运行中的工程位于 Windows 主机 `192.168.101.5` 的 `D:\Projects\VRChatProjects2\vrc-cpu-world`。Linux 工作区维护 HDL、源码、工具、验证结果和完整工程快照。

```sh
python tools/unity_remote.py --check-result editor_status
python tools/deploy_circuit_lab.py
# 仅部署编辑器测试与构建脚本
python tools/deploy_circuit_lab.py --builder-only
# 退出 PlayMode，保存本次场景修改后，采集真实资产与 GUID
python tools/snapshot_unity_project.py
git status --short
git add <本次修改的文件>
git commit -m "Describe the verified change"
git push origin main
python tools/sync_windows_git.py
```

采集工具遇到未保存的场景或与远端不同的本地未提交资产时会停止，不会覆盖未部署的修改。对已确认由 Editor 更新的序列化镜像，可用 `snapshot_unity_project.py --accept-editor-file Assets/...` 采集指定文件；工具先保留本地备份，其它差异仍会拒绝覆盖。纯文本部署会跳过序列化资产和 `.meta`；资产制作、移动和导入统一通过 Unity Pipeline 完成。每次完成并验证修改后，采集、提交并推送 GitHub；不要在两端同时修改同一文件。

`sync_windows_git.py` 用 SSH 传输 Git bundle，使 Windows 工程的 HEAD、索引和辅助资料与已推送的提交一致。它先核对每个 Unity 文件的哈希，不改写场景或资产，也不要求 Windows 端配置 GitHub 凭据。

## 项目资料

- [交互、共享状态与验证](CIRCUIT-LAB.md)
- [教学 CPU 和 HDL 工具链](TEACHING-CPU.md)
- [Unity 环境与兼容补丁](UNITY-SETUP.md)
- [Unity 操作规范](UNITY-GUIDE.md)
- [最初的 GPU 冒烟](NETLIST-SMOKE.md)
- [LCVEX SoC 可行性评估](LCVEX-GPU-EVALUATION.md)
- [最初创意记录](IDEA.md)

24 项程序已完成参考模型、RTL、综合门级和独立算法验证；PlayMode 已验证单客户端 Udon/GPU、新增短程序及独立状态恢复。64/100 位 π 的 GPU 检查各约 4,100 周期，尚未等待完整输出；256 Hz 理想耗时分别为 11.31 / 27.39 小时。验证汇总见 [teaching-validation-summary.json](results/circuit-lab/teaching-validation-summary.json)。真实 VRChat 多人网络、原生 PC/VR 输入、客户端 AUTO 语言变化及完整 LCVEX 整机 GPU 仿真仍需对应环境验证。
