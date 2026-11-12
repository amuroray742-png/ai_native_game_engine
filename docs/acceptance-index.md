# 当前验收总索引

本页供项目维护者与验收人员查找能力、已归档验证范围和待补证据，不替代各模块契约，也不改写历史记录。

## 1. 索引基准与状态含义

- 整理日期：2026-11-12。
- 整理所依据的代码提交：`d36bb3fcea019c0d21d9733a6ff9c073bfaa4303`；这是阅读基准，不是一次新验收的被测版本。
- 本次只整理仓库文档、配置及已有证据入口，未重新运行构建、测试、Unity 或远端 CI，未逐文件比对全部历史源码指纹。
- 下表日期为原记录声明的验收日期。提交时间、整理时间与实际运行时间不能互相替代。

| 状态 | 含义 |
| --- | --- |
| 已实现／有历史验收 | 能力已有实现，链接记录证明其指定版本与范围通过；不自动证明当前 HEAD 通过 |
| 待验证 | 已有能力或验收要求，但尚缺对应范围的执行证据 |
| 待阶段收口 | 已有部分实践，但未找到满足阶段退出条件的完整索引 |
| 范围外 | 当前设计不承诺；不因列入本页而成为已批准开发任务 |

历史验收只能用于原被测源码、配置与环境。判断能否复用时，应核对该轮提交或 `sources.json` 等源码指纹及后续变更；不能把不同版本的测试数相加，也不能用最新专项为较早的完整矩阵续签。

当前契约入口：[总设计](ai-native-game-engine-design.md)、[架构规范](engineering-guidelines.md)、[v2 契约](framework-v2.md)、[UnityCore](unitycore-lifecycle.md)、[Assets](unitycore-assets.md)、[资源增强](assets-enhancement-urp.md)。

## 2. 模拟内核、物理与 Unity 宿主

| 能力／状态 | 已归档验证范围 | 版本与证据入口 | 未覆盖或待补 |
| --- | --- | --- | --- |
| Core／Hosting／Headless：已实现／有历史验收 | 2026-11-09 Windows 本机：271 项 .NET、108 次真实 CLI、原生损坏探针；包含故障分类、编码失败终止、恢复 runtime 核对及 Int32 负零回归 | [修复记录](review-fixes-20261109.md)；[摘要与源文件指纹](evidence/review-fixes-20261109/summary.json)，记录基准提交及最终受影响源码 SHA-256 | 该轮未执行 Linux、远端 CI、Player／IL2CPP；当前 HEAD 未在本次重验 |
| ABI 4 托管物理分层：已实现／有历史验收 | 2026-11-04 Windows／Linux CI 各 236 项测试、108 次 CLI，两类原生探针与五组平台比较 | 被测提交 `f746ead912ebb160643c9939e9ce838d11ee344f`；[迁移记录与 CI 41837315399](managed-physics-migration.md) | 原生 Add 内存耗尽回滚未做注入验收；该固定提交结果不替代后续代码验证 |
| GridGame Unity Play：已实现／有历史验收 | 2026-11-10 URP Windows Editor：双 Domain Reload、同域重入、棋盘颜色像素；四条快慢表现轨迹各与 CLI 的 16 检查点一致 | [URP 记录](assets-enhancement-urp.md)及[源码指纹](evidence/assets-enhancement-urp/package-sources.json)；基础契约与历史证据见 [Play 宿主](unity-play-host.md) | 独立 Player、IL2CPP、其他平台及人工玩法体验待单独验收 |
| PhysicsSandbox Unity Play：已实现／有历史验收 | 2026-11-10 URP Windows Editor：双重载各 27 项及退出／重入；暂停输入、Tick 90／91 恢复与十组 CLI 比较 | [URP 记录](assets-enhancement-urp.md)及[源码指纹](evidence/assets-enhancement-urp/package-sources.json)；基础场景与通过条件见 [PhysicsSandbox](phase-3-physics-sandbox-unity-plan.md) | 同上；AssetsDemo 的 Player 结果不能证明 Jolt 模拟宿主 Player 可用 |
| UnityCore：已实现／有历史验收 | 2026-11-09 生命周期、取消、关闭、调度及双重载回归；后续日志专项另记；异步关闭与 Assets 的集成还由资源回归覆盖 | [生命周期分轮记录](unitycore-lifecycle.md)、[回归摘要](evidence/unitycore/simplify-20261109/summary.json)、[Assets 集成记录](unitycore-assets.md) | 日志专项不是完整生命周期回归；独立包测试与示例集成不能互相替代 |

恢复与回放要求同构建、同环境及同物理线程配置；跨平台 `compare` 匹配仅证明两份记录相符，不扩大为跨平台快照互通。历史 v2 基线、协议行为映射和旧阶段入口保留在 [v2 验收](v2-acceptance.md)，不作为当前全仓通过声明。

## 3. Assets、构建部署与 Shader

统一环境以各轮记录为准，主要为 Unity 6000.3.23f1、Windows x64、URP 17.3.0、SBP 2.6.2。Builtin 在此指 Bundle 构建后端，不等于 Built-in 渲染管线。

| 能力／状态 | 已归档验证范围 | 版本与证据入口 | 未覆盖或待补 |
| --- | --- | --- | --- |
| Assets 句柄、作用域、共享请求、实例／池／场景：已实现／有历史验收 | 2026-11-10 模式所有权复审：双重载各 96 EditMode／45 PlayMode，SBP Mono／IL2CPP Player；后续资源增强及 Shader 轮次另有回归 | [所有权复审说明](unitycore-assets.md#模式切换所有权复审)、[摘要](evidence/unitycore-assets/mode-ownership-review/summary.json)、[源码指纹](evidence/unitycore-assets/mode-ownership-review/sources.json) | 不将较早 Player 结果视为最新源码全矩阵通过；长期压力和性能预算未验收 |
| 双后端构建、版本与发布：已实现／有历史验收 | 2026-11-10 两后端构建、配置持久化、取消、发布及保存失败等；最终 80 EditMode／45 PlayMode | [SBP 契约](unitycore-assets-sbp.md)、[版本记录](unitycore-assets.md#构建版本与输出目录)、[摘要](evidence/unitycore-assets/build-version/summary.json)、[源码指纹](evidence/unitycore-assets/build-version/sources.json) | 不承诺 Player 内置内容与 Bundle 全局零重复；原生 BuildReport 文件映射歧义尚缺单独注入测试，见 [复审记录](unitycore-assets-review.md) |
| 精简部署与裁剪证据：已实现／有历史验收 | 2026-11-10 两后端×双重载；Builtin／SBP 各 Mono／IL2CPP，共四个 Player；白名单目录、批处理和菜单 API | [部署契约及验收](unitycore-assets-deployment.md)、[摘要](evidence/unitycore-assets/deployment/summary.json)、[源码指纹](evidence/unitycore-assets/deployment/sources.json) | Inspector／标准 Build Profiles 鼠标流程待验证；Builtin 标准入口须有匹配 Manifest；进程崩溃不保证跨目录事务恢复 |
| URP 与资源增强：已实现／有历史验收 | 2026-11-10 Forward／Forward+×两后端×双重载；两渲染配置×两后端×Mono／IL2CPP 八个 Windows D3D11 Player；无 URP 夹具兼容性与最终补测另记 | [分轮结果与补测边界](assets-enhancement-urp.md)、[摘要](evidence/assets-enhancement-urp/summary.json)、[矩阵指纹](evidence/assets-enhancement-urp/package-sources.json)、[交付指纹](evidence/assets-enhancement-urp/current-sources.json) | 部分最终变更只做针对性补测；早期日志含 JobTempAlloc，须结合下一行，不宣称原矩阵全进程无警告 |
| JobTempAlloc 工程规避与日志门禁：已实现／有历史验收 | 2026-11-11 Forward、关闭 Domain Reload：104 EditMode／55 PlayMode；两后端新 Bundle 与两个 Mono D3D11 Player；最终完整日志无目标警告 | [定位与适用边界](assets-jobtempalloc.md)、[摘要](evidence/assets-jobtempalloc/summary.json)、[源码指纹](evidence/assets-jobtempalloc/sources.json) | 是锁定 Editor 下的工程规避，不是 Unity 原生修复；未重跑 IL2CPP、Forward+、双重载全矩阵 |
| Shader 自动收集与合并：已实现／有历史验收 | 2026-11-11 Editor D3D11：110 EditMode／55 PlayMode；双重载各六项专项；Forward／Forward+ 实际像素；两后端真实集合加载与 WarmUp | [收集契约及验收](assets-shader-auto-collection.md)、[摘要](evidence/assets-shader-auto-collection/summary.json)、[源码指纹](evidence/assets-shader-auto-collection/sources.json) | 没有 Player 端采集导出；不覆盖未执行的动态分支；该轮未重跑 Player／IL2CPP |
| ShaderVariantDemo：已实现／有历史验收 | 2026-11-12 Windows Mono／D3D11／Forward：两后端×保留／裁掉／恢复六个 Player；双重载各三项专项；110 EditMode／55 PlayMode 基准回归 | [最新专项记录](assets-shader-variant-demo.md)、[摘要](evidence/assets-shader-variant-demo/summary.json)、[源码指纹](evidence/assets-shader-variant-demo/sources.json) | 未覆盖 IL2CPP／Forward+；通过控制器 API，不是鼠标自动化；不替代全 Assets 最新矩阵 |

测试数量仅帮助定位原报告，不作跨轮累计完成度。`--baseline-fixture` 使用 Git HEAD 中的 ResourceGroups 替换隔离副本配置，因此基准回归不等于用户当前本地配置已通过；核对夹具时应同时查运行记录和输出的 `fixture-source.asset`，不能只看开发工程配置。

## 4. 自动门禁与阶段收口

| 项目 | 现状 | 退出条件／证据要求 |
| --- | --- | --- |
| 无头 CI | [工作流](../.github/workflows/build.yml)已配置 Windows／Linux 原生构建、锁定恢复、构建、测试、只读格式、真实 CLI 及平台比较；有固定提交历史成功记录 | 要声明新版本通过，必须定位该被测提交的 run、日志及制品；本次未查询最新远端状态 |
| Unity CI | 待接入；当前工作流没有 Unity 作业 | 按授权与执行环境另行决定；本地脚本存在不等于 CI 已执行 |
| 阶段 1／2／2.5 | 已有历史验收，当前执行统一 v2 | 查 [v2 索引](v2-acceptance.md)及其历史链接，不使用退役 v1 命令验证当前版本 |
| 阶段 3 | 两个模拟示例已有 Windows Editor 证据 | Player／IL2CPP、其他平台、人工体验各自独立验收 |
| 阶段 4 | 待阶段收口；已有审查、脚本、CI 和归档实践，尚未找到三类交付的完整索引 | 数值调整、规则变更、Bug 修复各一例：需求、改动、独立审查、自动验证、人工验收及版本／复现入口；按 [总设计](ai-native-game-engine-design.md#4-agent-工作流与项目知识)补齐，不为归档创建空体系 |
| 阶段 5／范围外能力 | 未因此启动 | 网络、ECS／GAS、远端更新、更多平台等先有产品需求或性能证据，再独立设计 |

远端平台制品按当前工作流保留七天，不能假定旧下载链接仍可用。仓库历史 ZIP、源码指纹及机器摘要提供定位信息；本地 `TestResults/` 路径仅帮助原机器定位，不是跨机器可用的交付链接。本次未重新校验历史归档哈希或远端制品可下载性。

## 5. 当前待补清单

以下是证据与交付缺口，不是新批准的开发计划，也不把静态观察当成已复现缺陷。

| 编号 | 待补事项 | 收口依据 |
| --- | --- | --- |
| A1 | 为拟交付版本执行受影响范围的验证并对齐版本 | 记录提交／未提交源码指纹、配置、环境、实际命令、成功与失败材料；未跑的矩阵继续明确保留 |
| A2 | AssetsDemo 入口失败／销毁清理审查与回归 | [入口](../examples/unity/AssetsDemo/Assets/AssetsDemoBootstrap.cs)成功路径关闭 Core，但失败路径未统一关闭，销毁令牌仅用于 Continue 等待；需单独核实中途失败与销毁，不据静态观察宣称永久泄漏 |
| A3 | 两个模拟示例的 Player／IL2CPP 与人工体验 | 各自实际构建运行、输入／暂停／恢复与退出检查；PhysicsSandbox 还需验证原生桥部署及身份；不得借用 AssetsDemo 结果 |
| A4 | 构建部署 GUI 实操 | 真实 Inspector 和标准 Build Profiles 操作、结果与错误反馈；API／菜单调用不替代鼠标流程 |
| A5 | 第 4 阶段三类交付索引及成本基线 | 按设计补齐三类案例，记录构建／测试／复现耗时与人工介入；不虚构历史测量 |
| A6 | 性能、压力及发布范围决策 | 先确定目标资源规模、场景、时长与预算，再测量；不将功能用例或少量对象内存采样当作性能验收 |

## 6. 当前复现入口

以下只是命令入口，不表示本次已执行。均在仓库根目录运行；证据目录必须不存在，具体前置条件见 [CLAUDE.md](../CLAUDE.md)及各模块记录。需要匹配被测版本，不能用当前脚本直接冒充历史版本重验。

```sh
# 无头完整顺序；物理依赖 Python、CMake／Ninja 和相应 C++ 工具链
python scripts/build_native.py
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build --no-restore --logger trx --results-directory TestResults/acceptance-unit-new
dotnet format --verify-no-changes --no-restore
python scripts/verify_v2.py --output TestResults/acceptance-v2-new

# 两个模拟示例：Windows Editor，verify 内部执行 prepare
python scripts/unity_grid.py verify --output TestResults/acceptance-grid-new
python scripts/unity_physics.py verify --output TestResults/acceptance-physics-new

# Assets：默认 Editor 基准；完整矩阵额外要求 Windows IL2CPP 工具链
python scripts/verify_unity_assets.py TestResults/acceptance-assets-editor-new --baseline-fixture
python scripts/verify_unity_assets.py TestResults/acceptance-assets-matrix-new --backend both --renderers both --baseline-fixture --players

# 实际图形绘制专项，不是无图形验收
python scripts/verify_shader_collection.py TestResults/acceptance-shader-collection-new
python scripts/verify_shader_variant_demo.py TestResults/acceptance-shader-variant-new --backend both
```

Unity 使用 6000.3.23f1 和有效授权。Shader 变体专项严格限定 Mono／D3D11／Forward，自动收集专项是 Editor 验证；两者不能互相扩大覆盖范围。核对完整进程日志中的错误及 JobTempAlloc／TLS 警告，不能只看 XML 或成功 JSON。

## 7. 更新规则

1. 新验收先写入对应模块记录及机器证据，再更新本索引的范围和链接。
2. 每轮记录被测版本／指纹、配置、平台与后端、实际执行项、失败与跳过项、证据位置；专项与完整矩阵分别记录。
3. 新增或改变能力而未执行相关验证时，标为待验证；不改写旧记录为新版本通过，不修改 `docs/evidence/` 和 `docs/contracts/phase-2/` 历史证据。
4. 契约变化同步维护模块文档；本页只作导航和状态索引，不复制完整 API 契约。
