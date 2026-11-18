# 第四阶段交付案例索引

本页按[总设计第 4 节](ai-native-game-engine-design.md#4-agent-工作流与项目知识)记录三类已有、可复核的案例。案例来自不同版本，不能将历史测试转记为当前提交的测试。阶段退出还要求每例的独立审查与人工体验验收，三项人工结果见下文。

| 类别与目标 | 改动和版本 | 自动验证与复现入口 | 独立审查／人工验收 |
| --- | --- | --- | --- |
| 数值调整：GridGame HUD 将通用 UI 默认的 1920×1080 参考分辨率覆盖为 900×650，以匹配棋盘和按钮布局 | 默认值见 [UIOptions](../packages/com.ainative.unitycore.ui/Runtime/UIOptions.cs)，可配置入口由 `cd66065` 提供，GridGame 装配见 `f2e63d9` 的 [GridUiApplication](../examples/unity/GridGame/Assets/Play/GridUiApplication.cs)；这是显示数值，不是玩法平衡值 | [GridGame UI 验收](unity-play-host.md)记录 Editor 23 次、CLI 对照 4×16 检查点、两张截图；[复现脚本](../scripts/unity_grid.py)；[历史归档](evidence/grid-ui-20261117/summary.json) | GridGame UI 实现有[独立复核](evidence/grid-ui-20261117/review.json)；用户已在该 HUD 配置下完成 GridGame 操作验收 |
| 规则变更：移动输入从目标坐标改为方向，连续命令按执行时位置计算 | `f4bf6ad`；当前规则见 [GridGameRules](../examples/AiNative.GridGame/GridGameRules.cs) | [第一阶段记录](phase-1-headless-gameplay-plan.md#方向命令调整)记载变更后 59 项测试、只读格式与默认演示通过；按该提交检出后运行 `dotnet test` 与默认 CLI 演示可重验 | 本轮独立核对历史提交与当前 v2 规则，GameplayTests 20/20。用户已确认移动、攻击、回合切换与重开正常 |
| Bug 修复：AssetsDemo 启动中途失败或对象销毁时统一关闭 Core，并验证资源释放 | 本分支的 [AssetsDemoBootstrap](../examples/unity/AssetsDemo/Assets/AssetsDemoBootstrap.cs) 与 [ShaderProbeVerification](../examples/unity/AssetsDemo/Assets/ShaderProbeVerification.cs) | [Assets 验证脚本](../scripts/verify_unity_assets.py)加入 `after-start`、`after-first-load` 和销毁路径，要求预期退出码与结果；本轮执行记录见下文 | 独立审查发现及回归证据见下文。用户已确认 GUI 构建、部署和运行清理通过 |

本轮数值案例覆盖 UI 参考分辨率。三项人工验收的环境、操作和结果如下。

## 本轮执行记录（2026-11-18）

下表归档对应初次提交 `afe41cc`。AssetsDemo 的后续复核使用下节的新证据。

环境：Windows x64、Unity 6000.3.23f1、Unity CLI 1.0.1、Direct3D11。所有 Unity 验证使用仓库外隔离副本；本地原始日志和结果保存在 `C:/work/` 下，以脚本的源码指纹及命令文件对应。仓库不包含大体积 Player 二进制。

| 检查 | 结果与材料 | 边界 |
| --- | --- | --- |
| 无头回归 | 当前分支 `dotnet restore --locked-mode`、`dotnet test --no-restore` 通过 271/271，`dotnet format --verify-no-changes --no-restore` 通过；`python -m py_compile` 验证两份新增／修改脚本 | 这是当前规则回归，不是历史方向变更当时的独立审查 |
| AssetsDemo Editor 基准夹具 | 该提交的 Domain Reload 开／关各 110 EditMode、58 PlayMode，共 336 次通过，0 失败；[摘要](evidence/player-phase4-20261118/assets-summary.json)、[EditMode On XML](evidence/player-phase4-20261118/assets-sbp-EditMode-reload-On.xml)、[EditMode Off XML](evidence/player-phase4-20261118/assets-sbp-EditMode-reload-Off.xml)、[PlayMode On XML](evidence/player-phase4-20261118/assets-sbp-PlayMode-reload-On.xml)、[PlayMode Off XML](evidence/player-phase4-20261118/assets-sbp-PlayMode-reload-Off.xml)、[源码指纹](evidence/player-phase4-20261118/assets-sources.json)及[夹具](evidence/player-phase4-20261118/assets-fixture-source.asset)已归档 | 使用 SBP／URP Forward 基准夹具；未重跑 Builtin 或 Forward+ 组合 |
| AssetsDemo Mono／IL2CPP Player | 两后端正常路径、启动后失败、首次加载后失败、销毁路径均通过；每个后端退出码依次为 0、1、1、0，成功路径 Shader 像素图已生成；[Mono 图](evidence/player-phase4-20261118/assets-sbp-Mono-smoke.shader.png)、[IL2CPP 图](evidence/player-phase4-20261118/assets-sbp-Il2Cpp-smoke.shader.png)及[结果](evidence/player-phase4-20261118/assets-summary.json)已归档 | 自动场景／像素与清理检查，不替代 Inspector 或 Build Profiles GUI 操作 |
| GridGame Player | Mono 与 IL2CPP 实际输入、Tick、攻击、HUD 注册、重开及退出清理通过；两后端 Tick 3，Bundle／scope 清理计数为 0，见[结果](evidence/player-phase4-20261118/players-summary.json)、[源码指纹](evidence/player-phase4-20261118/grid-sources.json)和[IL2CPP 场景截图](evidence/player-phase4-20261118/grid-Il2Cpp.png) | 截图来自场景相机，不包含 Overlay HUD；不能据此认定人工视觉体验通过 |
| PhysicsSandbox Player | Mono 与 IL2CPP 暂停排队、存档拒绝、恢复及世界清理通过；恢复 Tick 1、4 个刚体，断开后世界数 0；部署的 Jolt DLL 与 manifest 哈希一致，见[结果](evidence/player-phase4-20261118/players-summary.json)、[源码指纹](evidence/player-phase4-20261118/physics-sources.json)和[IL2CPP 场景截图](evidence/player-phase4-20261118/physics-Il2Cpp.png) | 自动检查不替代人操作体验 |

构建、测试和复现耗时未以统一方式测量，本页不提供推测的成本基线。CI 对本分支的状态需以提交后的实际工作流为准；Unity CI 尚未接入。

从仓库根目录使用不存在的输出目录重新生成本轮自动证据（前置 Unity 与原生工具见各示例文档）：

```powershell
python scripts/verify_unity_players.py C:/work/new-player-evidence --examples both --backends both
python scripts/verify_unity_assets.py C:/work/new-assets-evidence --players --baseline-fixture
```

前一脚本写入每个 Player 的构建命令、完整日志、结果、场景截图、工作区源码指纹与 Jolt 部署哈希；Player 构建器仅在隔离副本的场景中注入验收探针，因此构建场景与工作区场景文件不相同。后一脚本写入 Editor XML、构建与运行日志、异常路径结果、Shader 像素截图、源码指纹及基准夹具。脚本拒绝覆盖已有证据目录。

## Windows 人工验收

2026-11-18，用户完成 [PR #33](https://github.com/amuroray742-png/ai_native_game_engine/pull/33) 的三项人工验收。AssetsDemo 使用与 `6306449` 一致的 Bootstrap 源码。GridGame 和 PhysicsSandbox 的运行日志均指向此前验收的 Windows IL2CPP Player。

| 示例 | 人工操作及结果 |
| --- | --- |
| AssetsDemo | 在 ResourceGroups Inspector 点击 **Build Windows bundles** 和 **Copy latest build to StreamingAssets**，版本 1 构建、部署成功。通过 **File > Build Profiles** 构建 Windows Player，启动后点击 **Run cleanup checks**，显示 PASS。 |
| GridGame | 移动、攻击和回合切换正常。点击 **New Match** 可以重新开局。 |
| PhysicsSandbox | 暂停时输入可以排队，存档和读档正常。 |

AssetsDemo 的 Editor 日志记录 Windows 构建成功，耗时 18.103 秒。发布目录 `BundleBuildOutput/1`、项目 StreamingAssets 和最终 Player 中的 catalog 与 7 个 Bundle 逐文件 SHA-256 一致。运行日志指向 `C:/work/pr33-gui-player`，并记录 `PASS: shared resources, pool, scene, script types, restart and cleanup`。

## PR 33 独立审查与回归

独立审查者核对了 HUD 参数案例、方向规则的执行顺序和拒绝路径、两种 Player 的结果与截图，以及 514 个源码指纹。审查者独立运行当前 GameplayTests，20 项通过。

AssetsDemo 的重复入口在注册失败后可能关闭已有 Core。修复将关闭责任放在注册成功之后，清理结束即释放该引用。Unity 测试先启动一个 Core，再创建并销毁重复入口。旧版实际从 Running 变为 Stopped，修复版保持同一根对象和 Running 状态。[旧版 XML](evidence/player-phase4-20261118/review/ownership-red.xml) · [修复版 XML](evidence/player-phase4-20261118/review/ownership-green.xml)

另两处修复覆盖清理错误的报告。Player 在 CloseAsync 完成后检查 Unity 错误日志。故障注入脚本核对顶层异常首行，独立的 Python 负对照覆盖 Close 失败和资源残留两种包装异常。修复后在隔离工程重跑 SBP Forward，Domain Reload 开关合计 336 项通过。Mono 和 IL2CPP 的成功、两处故障、销毁路径均通过。所有权回归在另一个隔离工程单独运行。[执行摘要](evidence/player-phase4-20261118/review/summary.json) · [源码指纹](evidence/player-phase4-20261118/review/sources.json)

独立复核已确认两项 P1 和一项 P2 均已关闭，没有剩余代码阻断。
