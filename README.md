# VRChat CircuitLab

在 VRChat world 内使用 GPU 仿真综合后的门级网表，并通过可移动的模块、实体探头和逻辑分析仪观察电路。当前示例是 8 位教学 CPU，支持层级展开、子模块取出、连续记录、手动冻结和字符终端；只做逻辑状态与时钟周期仿真。

仿真在各玩家本地 GPU 执行，共享控制、检查点、波形及物体状态。空闲实验台的首次控制点击会取得全局控制权；已有操作者时，其他玩家使用分析仪顶部的 `REQUEST CONTROL` 申请，由操作者通过 `GRANT REQUEST` 交接。

## 打开工程

仓库根目录就是 Unity 工程，使用 **Unity 2022.3.22f1**。将克隆目录添加到 VRChat Creator Companion，通过 Manage Project 恢复 `Packages/vpm-manifest.json` 锁定的 VRChat Base / Worlds SDK **3.10.5** 和 VPM Resolver **0.1.29**，再打开 `Assets/CircuitLab/Scenes/CircuitLab.unity`。

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

采集工具遇到未保存的场景或与远端不同的本地未提交资产时会停止，不会覆盖未部署的修改。纯文本部署会跳过序列化资产和 `.meta`；资产制作、移动和导入统一通过 Unity Pipeline 完成。每次完成并验证修改后，采集、提交并推送 GitHub；不要在两端同时修改同一文件。

`sync_windows_git.py` 用 SSH 传输 Git bundle，使 Windows 工程的 HEAD、索引和辅助资料与已推送的提交一致。它先核对每个 Unity 文件的哈希，不改写场景或资产，也不要求 Windows 端配置 GitHub 凭据。

## 项目资料

- [交互、共享状态与验证](CIRCUIT-LAB.md)
- [教学 CPU 和 HDL 工具链](TEACHING-CPU.md)
- [Unity 环境与兼容补丁](UNITY-SETUP.md)
- [Unity 操作规范](UNITY-GUIDE.md)
- [最初的 GPU 冒烟](NETLIST-SMOKE.md)
- [LCVEX SoC 可行性评估](LCVEX-GPU-EVALUATION.md)
- [最初创意记录](IDEA.md)

PlayMode 已验证单客户端 Udon/GPU 和独立状态恢复。真实 VRChat 多人网络、实际 VR 输入及完整 LCVEX 整机 GPU 仿真仍需对应环境验证。
