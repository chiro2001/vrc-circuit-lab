# Unity 开发环境

Unity 工程位于 `192.168.101.5`：

```text
D:\Projects\VRChatProjects2\vrc-cpu-world
```

环境版本：

| 组件 | 版本 |
| --- | --- |
| Unity Editor | 2022.3.22f1 |
| Unity CLI | 1.0.0-beta.12（机器已有） |
| Unity Pipeline | 0.8.0-exp.1，官方 Registry 源码的本地兼容版本 |
| VRChat Base / Worlds SDK | 3.10.5（工程已有） |

安装方法来自 [unity-cli-2022-mod](https://github.com/rocwood/unity-cli-2022-mod)。Pipeline 嵌入在远端工程的 `Packages/com.unity.pipeline`；Unity CLI 二进制和包内 Roslyn DLL 均未修改。

进行 Unity 开发前，阅读 [UNITY-GUIDE.md](UNITY-GUIDE.md) 和 [unity-pipeline 技能](.agents/skills/unity-pipeline/SKILL.md)。两份文件也安装到了远端工程。

## 从当前 Linux 工作区操作

```bash
# 查看 CLI 与编辑器连接状态
python tools/unity_remote.py --cli status --json

# 所有 Pipeline 命令自动指定远端工程，并返回 JSON
python tools/unity_remote.py editor_status
python tools/unity_remote.py set_autotick --enable true

# 按需发现当前实例的命令
python tools/unity_remote.py -- --query scene --limit 15

# 验证动态 C# 脚本编译与工程依赖
python tools/unity_remote.py run_script --file AgentScripts/VerifyScaffold.cs --entry VerifyScaffold.Run

# 修改脚本后，触发编译并查询最终结果
python tools/unity_remote.py recompile
python tools/unity_remote.py recompile_status
```

`tools/unity_remote.py` 通过 SSH 调用 `pwsh`，使用 UTF-16LE 的 `EncodedCommand` 传递命令，避免 Linux shell 与 PowerShell 的引号冲突。保留 Unity CLI 的退出码；域重载期间允许短暂连接失败，随后继续查询状态。

## Unity 2022 兼容改动

- 将包的 Unity 版本要求调整为 2022.3，Mono.Cecil 使用工程已有的 1.11.4。
- Unity 6 的 `PhysicsMaterial` 在旧版使用 `PhysicMaterial` 别名。
- 通过 `SerializedObject` 读取原始材质渲染队列，保留 `-1` 的继承语义。
- 安装前将五个 Roslyn 插件的导入描述转换为 Unity 2022 的 v2 格式，保留官方 GUID 和 DLL 内容。
- Unity 2022 缺少新版 `IAnalytic` 接口，因此关闭该版本下的可选使用统计；保留 Unity 6 的原始实现。
- Unity 2022 的 Console 计数通过 `LogEntries.GetCountsByType` 反射读取，保留后台状态采样。

完整兼容说明位于远端 `Packages/com.unity.pipeline/Documentation~/unity2022-compatibility.md`。更新 Pipeline 前先核对补丁；直接替换成 Registry 原版会重新引入兼容问题。

只读验证脚本位于远端 `AgentScripts/VerifyScaffold.cs`，不进入 `Assets` 的正常编译，不修改或保存场景。验证记录位于远端 `Tools/UnityCLI`。

## 已完成的验证

- CLI 连接到目标工程，编辑器状态为 `ready`，端口为 `127.0.0.1:7800`。
- 已启用后台自动 tick，能够发现并调用当前实例的 Pipeline 命令。
- `run_script` 在编辑器中完成内存编译与执行，无诊断错误；确认 Pipeline 为 Embedded 来源，VRChat Worlds SDK、UdonSharp 和 GPU 异步读回 API 均存在。
- Pipeline 兼容包初次导入编译成功；后续 `recompile` / `recompile_status` 返回 `up_to_date`、`failed: false`。
- Console 报告 0 个错误。6 个警告均来自编辑器已有的 `-disable-assembly-updater` 启动参数，提示跳过 DLL 自动更新。
- 当前 `VRCDefaultWorldScene` 未被修改或保存，Play Mode 保持停止。

网表渲染、Udon 调度和 GPU 仿真的 Play Mode 冒烟见 [NETLIST-SMOKE.md](NETLIST-SMOKE.md)。
