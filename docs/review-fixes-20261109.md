# 2026-11-09 宿主故障边界修复与验收

本轮基于 `1cc849f04c4f2fbb36037108a0b59b146449bf7e`，处理 review 复核确认的五项问题。未修改构建身份算法、补步积压策略、物理浮点负零、碰撞归并或原生实现。最终源文件 SHA-256、机器结果与独立审查范围见[摘要](evidence/review-fixes-20261109/summary.json)，原始 TRX、Unity XML、CLI 进程索引和比较汇总见[验证归档](evidence/review-fixes-20261109/verification.zip)。

## 改动与回归

| 问题 | 修复及验证 |
| --- | --- |
| 执行期协议异常误归为输入错误 | CLI 在创建/恢复、执行边界转换为 SimulationFailure/1，保留具体错误码、路径、操作索引及可信前缀。覆盖模块创建错误、结果校验失败、重复 JSON 字段输出、恢复核对失败；执行前输入错误仍为2。 |
| Tick 编码失败后仍可继续 | Core 新增显式 `MarkFaulted`，供 Hosting 在编码/帧交付失败时终止实例，不回滚 Tick。覆盖协议和类型化视图的 Tick 编码、观察/状态编码和错实例帧交付；故障后推进、提交和状态捕获拒绝。 |
| 恢复仅核对游戏状态 | 发布候选前核对全部 runtime 和游戏状态，失败释放候选。分别覆盖 Tick、submitted、processed、pending、随机状态、Timing 和游戏状态不一致。 |
| 退出 Play 时日志入口抛错 | 退出期间仅 `Core.Log` 返回临时 Logger，直接使用 Unity Debug；`Inst` 仍拒绝重建服务。测试覆盖模块 Shut、场景 OnDestroy、退出后的 Editor 日志和再次进入 Play。 |
| Int32 负零导致假分歧 | Int32 入口拒绝 `-0`。回放索引负零在环境探测与执行前返回输入错误2；整数边界仍可读，现有 Mono 浮点负零测试仍通过。 |

新增22项 .NET 用例与1项 Unity 退出日志用例。新公共入口只传递宿主已发现的故障，不向纯 C# Core 引入 JSON、Unity 或外部服务依赖。协议版本保持 v2；历史含 Int32 `-0` 的输入须改为 `0`，物理 binary32 的负零不变。构建变化后仍按原有身份门禁重新生成存档与回放。

## 独立审查

实现 Agent 完成改动后，由独立 Reviewer Agent 只读检查 diff、相关模块和契约。首轮未发现代码阻断项，建议补充观察/状态编码及帧交付失败测试，并澄清日志文档中的正常运行限定。两项均落实；复核未发现剩余具体问题。

Reviewer 独立读取了最终271项 .NET TRX及两种 Domain Reload 的 PhysicsSandbox XML，确认通过；最终 CLI 比较汇总由实现 Agent 核对。审查后的新增测试重新运行，最终只读格式检查和 diff 检查通过。`dotnet format` 输出了工作区加载警告，退出码为0；不将它描述为已启用额外分析器门禁。

## 本机实际验证

| 检查 | 结果 |
| --- | --- |
| .NET 全量测试 | 271通过，0失败、0跳过 |
| v2 真实 CLI 进程 | 108次全部满足预期退出码和结果码 |
| 原生损坏探针 | 334截断位置、334变异位置；结束时 worlds=0 |
| PhysicsSandbox，Domain Reload 开/关 | 每种24项 PlayMode、23项 EditMode全部通过，共94次 |
| PhysicsSandbox CLI 比较 | 十组全部匹配；每种设置均匀/补步245、暂停14、Tick90续跑153、Tick91续跑150个检查点 |
| GridGame，Domain Reload 开/关 | 每种7项 PlayMode通过，另1项进出Play测试通过 |
| GridGame CLI 比较 | 四组快慢表现全部匹配，每组16个检查点 |
| 格式与补丁 | `dotnet format --verify-no-changes --no-restore`、`git diff --check`通过 |

复现命令（输出目录须不存在）：

```powershell
dotnet test --no-restore --logger trx --results-directory TestResults/review-fixes-unit-final
python scripts/verify_v2.py --output TestResults/review-fixes-cli
python scripts/unity_physics.py verify --output TestResults/review-fixes-unity
python scripts/unity_grid.py verify --output TestResults/review-fixes-grid
dotnet format --verify-no-changes --no-restore
git diff --check
```

Unity CLI 为1.0.0-beta.17，Editor 为6000.3.23f1，Unity验收使用仓库外临时工程。完整日志、输入、回放和截图保留在上述本地目录；归档只收录测试结果和机器索引，不替代完整运行制品。本轮未运行远端 CI、Linux、Player、IL2CPP 或人工试玩，未重建原生库。没有已知未解决的本轮修复阻断项。
