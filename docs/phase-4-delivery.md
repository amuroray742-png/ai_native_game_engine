# 第四阶段交付案例索引

本页按[总设计第 4 节](ai-native-game-engine-design.md#4-agent-工作流与项目知识)记录三类已有、可复核的案例。案例来自不同版本，不能将历史测试转记为当前提交的测试。阶段退出还要求每例的独立审查与人工体验验收；缺项在下表明示。

| 类别与目标 | 改动和版本 | 自动验证与复现入口 | 独立审查／人工验收 |
| --- | --- | --- | --- |
| 数值调整：GridGame HUD 将通用 UI 默认的 1920×1080 参考分辨率覆盖为 900×650，以匹配棋盘和按钮布局 | 默认值见 [UIOptions](../packages/com.ainative.unitycore.ui/Runtime/UIOptions.cs)，可配置入口由 `cd66065` 提供，GridGame 装配见 `f2e63d9` 的 [GridUiApplication](../examples/unity/GridGame/Assets/Play/GridUiApplication.cs)；这是显示数值，不是玩法平衡值 | [GridGame UI 验收](unity-play-host.md)记录 Editor 23 次、CLI 对照 4×16 检查点、两张截图；[复现脚本](../scripts/unity_grid.py)；[历史归档](evidence/grid-ui-20261117/summary.json) | GridGame UI 实现有[独立复核](evidence/grid-ui-20261117/review.json)；尚无针对该数值的产品体验签收 |
| 规则变更：移动输入从目标坐标改为方向，连续命令按执行时位置计算 | `f4bf6ad`；当前规则见 [GridGameRules](../examples/AiNative.GridGame/GridGameRules.cs) | [第一阶段记录](phase-1-headless-gameplay-plan.md#方向命令调整)记载变更后 59 项测试、只读格式与默认演示通过；按该提交检出后运行 `dotnet test` 与默认 CLI 演示可重验 | 本轮独立核对历史提交与当前 v2 规则，GameplayTests 20/20。人工玩法体验待签收 |
| Bug 修复：AssetsDemo 启动中途失败或对象销毁时统一关闭 Core，并验证资源释放 | 本分支的 [AssetsDemoBootstrap](../examples/unity/AssetsDemo/Assets/AssetsDemoBootstrap.cs) 与 [ShaderProbeVerification](../examples/unity/AssetsDemo/Assets/ShaderProbeVerification.cs) | [Assets 验证脚本](../scripts/verify_unity_assets.py)加入 `after-start`、`after-first-load` 和销毁路径，要求预期退出码与结果；本轮执行记录见下文 | 独立审查发现及回归证据见下文。人工 GUI 与体验结果待签收 |

以上三例均未达到设计文档中的完整交付退出条件，不能标为阶段 4 完成。特别是数值案例仅覆盖 UI 参考分辨率；若退出条件要求玩法平衡调整，还须另选具体需求和验证场景。

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

## 待人工完成的验收

在仓库外的 AssetsDemo 隔离副本中，选中 `Assets/ResourceGroups.asset`，通过 Inspector 的 **Build Windows bundles** 与 **Copy latest build to StreamingAssets** 按钮操作并记录成功提示、版本、部署目录；再用 **File > Build Profiles** 的 Windows 配置执行标准 Player 构建、启动与退出，保留 GUI 结果和完整日志。此流程明确要求原生鼠标操作；命令行构建与 Editor 菜单调用不能替代。本轮尝试打开隔离 Editor，但窗口截图连续超时、输入几何不可用，未能可靠定位或点击 Inspector／Build Profiles；与[既有工具限制记录](assets-enhancement-urp.md)一致。

GridGame 与 PhysicsSandbox 的 Player 自动探针分别覆盖输入、HUD 注册、暂停、恢复和释放，但仍需人检查实际反馈、操作手感与退出体验。上述数值和规则历史案例也需产品侧确认目标表现；签收时应记录操作者、被测提交、Windows Player 后端、场景、预期／实际和问题，而不能仅写“通过”。

## PR 33 独立审查与回归

独立审查者核对了 HUD 参数案例、方向规则的执行顺序和拒绝路径、两种 Player 的结果与截图，以及 514 个源码指纹。审查者独立运行当前 GameplayTests，20 项通过。

AssetsDemo 的重复入口在注册失败后可能关闭已有 Core。修复将关闭责任放在注册成功之后，清理结束即释放该引用。Unity 测试先启动一个 Core，再创建并销毁重复入口。旧版实际从 Running 变为 Stopped，修复版保持同一根对象和 Running 状态。[旧版 XML](evidence/player-phase4-20261118/review/ownership-red.xml) · [修复版 XML](evidence/player-phase4-20261118/review/ownership-green.xml)

另两处修复覆盖清理错误的报告。Player 在 CloseAsync 完成后检查 Unity 错误日志。故障注入脚本核对顶层异常首行，独立的 Python 负对照覆盖 Close 失败和资源残留两种包装异常。修复后在隔离工程重跑 SBP Forward，Domain Reload 开关合计 336 项通过。Mono 和 IL2CPP 的成功、两处故障、销毁路径均通过。所有权回归在另一个隔离工程单独运行。[执行摘要](evidence/player-phase4-20261118/review/summary.json) · [源码指纹](evidence/player-phase4-20261118/review/sources.json)

独立复核已确认两项 P1 和一项 P2 均已关闭，没有剩余代码阻断。

GUI 重试时，Unity 的窗口截图命令成功读到 ResourceGroups Inspector 的 Scriptable Build Pipeline 配置。Windows 原生窗口捕获仍报告 FrameArrived 超时，窗口激活动作报告输入几何不可用。已在隔离工程准备好资源配置和 Bootstrap 启动场景，等待实际按钮操作与体验结果。
