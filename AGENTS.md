# 项目协作说明

- 始终使用简体中文回复用户，除非用户要求其他语言。
- 临时文件统一放在 `~/tmp/vrc-cpu-map/`，按任务建立子目录；避免使用 `/tmp`。远端 Windows 工程使用 `~/tmp/vrc-cpu-world/`。
- Unity 工程位于 Windows 主机 `192.168.101.5` 的 `D:\Projects\VRChatProjects2\vrc-cpu-world`。
- 进行 Unity 开发时，必须阅读并遵循 [UNITY-GUIDE.md](UNITY-GUIDE.md)。
- 操作 Unity 时使用 [.agents/skills/unity-pipeline/SKILL.md](.agents/skills/unity-pipeline/SKILL.md)。
- 使用 `python tools/unity_remote.py <Pipeline 命令及参数>` 通过 SSH、PowerShell 调用 Unity；脚本会显式指定目标工程。
- Pipeline 是 Unity 2022 本地适配包。更新前检查远端 `Packages/com.unity.pipeline/Documentation~/unity2022-compatibility.md`，保留兼容补丁。
- GitHub 私有仓库：`chiro2001/vrc-circuit-lab`，主分支 `main`。仓库根目录是 Unity 工程，源码位于 `Assets/`，编辑器自动化脚本位于 `AgentScripts/`。
- 每次完成并验证实质修改后：停止 PlayMode，保存本次有意义的改动，运行 `python tools/snapshot_unity_project.py` 采集 Windows 工程，检查差异后提交并 `git push origin main`。不纳入凭据、缓存和客户端本地数据；不要只同步 C# 而遗漏场景、材质或 `.meta`。
- Windows 工程也使用同一远端与 `main`。两端开始编辑前先检查 Git 状态与远端提交；完成后确保两端 HEAD 与 GitHub 一致。勿用 checkout/reset 覆盖正在编辑的场景或未提交修改。
- Linux 推送后运行 `python tools/sync_windows_git.py`：通过 SSH bundle 对齐 Windows Git，先检查文件哈希；仅更新 HEAD/索引及辅助资料，不改写 Unity 资产。Windows 不需要另行配置 GitHub 凭据。
