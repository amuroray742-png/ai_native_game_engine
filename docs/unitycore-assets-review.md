# UnityCore Assets 提交前复审

本轮对照原实施计划，重点检查引用计数、独立取消、失败关闭、实际销毁和跨代际清理。主代理实现修复并执行验证；两个独立只读 reviewer 分别审查运行时所有权和构建/目录契约。沿用单一失败最多三轮假设、修改、验证预算，不修改模拟内核、PlayHost、Logger 发布宏或引入并发框架。

## 发现与修复

| 问题 | 影响 | 修复与验证 |
| --- | --- | --- |
| 池 OnRent/OnEnable 可同步取消，仍交付租约 | 调用者已取消却产生活跃所有权 | 两个回调边界后检查令牌，销毁候选；验证活跃数、闲置数和最终 Bundle 归零 |
| InstanceLifetime 在 OnDestroy 内立即释放 | 其他组件 OnDestroy 仍可能访问已卸载依赖 | 正常路径在帧末观察 GameObject 实际失效；强退在 DestroyImmediate 返回后释放；真实 Bundle 测试检查业务 OnDestroy 中材质、贴图和包引用仍有效 |
| 场景卸载失败后作用域仍解除登记，失败任务永久缓存 | 尚存场景失去父级追踪，恢复条件后无法重试 | 仅在资源、请求与子树全部排空后解除登记；场景/作用域/池失败后可重试，各次完成源独立；拒绝卸载后保留父子作用域和包引用，恢复后只释放一次 |
| 调用前已取消的场景请求仍启动 Bundle 加载 | 无需求的在途工作延长清理 | 登记和加载之前拒绝取消；验证底层加载数为零 |
| CRC 为零时 Unity 跳过 CRC 检查 | 缺失 CRC 字段可导致同长度损坏文件绕过预期校验 | 零 CRC 使用完整 SHA-256 后备；合法内容可加载，同长度损坏内容被拒绝 |
| 部分底层错误没有公开地址/阶段 | 调用者难以定位失败资源及共享依赖包 | 加载和卸载边界补上下文，保留错误码、实际失败包及内部原因；不包装取消和已释放所有者错误 |
| 循环、报告覆盖验收不够具体 | 仅目录图或 null 报告不能覆盖真实分包路径 | 新增实际 ScriptableObject 跨显式组循环，以及真实 BuildReport 缺少计划资源的拒绝测试 |

关闭失败不会伪装为排空；已经失败的调用者仍收到原错误，重试不会改写其任务结果。Unity 拒绝卸载最后一个场景时仍须保留依赖，调用方应保留或恢复启动场景。作用域可重试不代表 UnityCore 的 Faulted 状态自动允许重启。

零 CRC 后备会在主线程扫描文件；正常非零 CRC 仍交给 Unity 校验。本轮没有承诺大文件扫描帧预算。用户提供的池业务回调异常仍按原异常传播，诊断记录其原因。

## 原计划对照

| 原计划范围 | 对照结果 |
| --- | --- |
| 独立 UPM 包、单向依赖、Editor/Runtime/Tests/Samples、独立示例 | 已实现；AssetsDemo 直接引用本地源码包 |
| 显式归属优先、完整依赖闭包、AB/AC/ABC 共享集合、稳定包名 | 已实现，有顺序扰动和显式归属回归 |
| 场景独立包、外部依赖唯一所有者、循环拒绝 | 已实现；实际场景共享身份与跨组循环均验证 |
| 实际报告归属、缺证据拒绝、失败不覆盖旧产物 | 已实现；重复产物、缺报告、覆盖缺项和已有目标负例验证 |
| 独立句柄、作用域、共享请求、部分/全部取消、迟到回收 | 已实现，正常与失败路径均有回归；无 LRU、全局卸载或 Resources 回退 |
| 实例、池预热/容量/旧租约/外部销毁/回调失败 | 已实现；本轮补回调取消和业务销毁期间引用保护 |
| Additive 场景、重复拒绝、激活、取消、外部卸载 | 已实现；本轮补失败保留所有权和重试 |
| 可选异步关闭、逆序清理、强退隔离、旧句柄与重启 | 已实现；关闭故障、迟到结果、连续 Play 和两种 Domain Reload 均覆盖 |
| 目录/平台/路径/大小/CRC/SHA、Editor 双模式、诊断 | 已实现；CRC 零值补后备，文件大小和对象内存测量分列 |
| Windows Mono/IL2CPP、脚本裁剪、归档与复现 | 最终复跑结果见下；原验收作为历史证据保留 |

仍未单独注入测试的分支是原生 BuildReport 的文件映射歧义；实现遇到映射不唯一会拒绝发布，本轮未把静态检查描述为实际注入通过。其他平台、远端更新、长期压力、性能预算和 CI 不在本次已验收范围内。

## 验证与证据

开发回归曾因帧观察顺序出现一次池计数断言失败，现场保留在 `TestResults/assets-review-development/play-r1.json`；调整为帧末观察后，第二轮 44 项 PlayMode 全通过。最终新增完整性与错误上下文测试后，在独立源码副本重新执行，而非沿用开发结果。

最终两种 Domain Reload 设置各通过 45 项 PlayMode、37 项 EditMode，共 164 次测试，无失败。Windows Mono Development 和 IL2CPP Player 均构建成功，真实 Bundle 冒烟退出码 0，渲染截图已检查。7 个产物 Bundle、6 个公开地址的归属及 SHA-256 复核通过。

```powershell
python scripts/verify_unity_assets.py TestResults/assets-review-20261109 --players
```

本轮证据：[机器摘要](evidence/unitycore-assets/review-followup/summary.json)、[原始证据包](evidence/unitycore-assets/review-followup/assets-review-evidence.zip)、[工作树字节指纹](evidence/unitycore-assets/review-followup/sources.json)、[Git 源文件 blob 指纹](evidence/unitycore-assets/review-followup/sources-git.json)、[Player 文件摘要](evidence/unitycore-assets/review-followup/player-files.json)。证据包包含原始 BuildReport、归属与负例报告、四份测试 XML、构建日志、设置、复现脚本及本轮开发失败现场。Git blob 指纹按仓库 LF 规则记录，避免 Unity 在本机写出的 CRLF 影响跨检出核对。

初次实现的验收记录仍见 [Assets 文档](unitycore-assets.md)，不回写历史源码指纹。独立 reviewer 只做源码与证据审查，没有代替主代理执行 Unity，也没有把本机验证称为 CI 或人工试玩。

Unity 自动生成的空 YAML 字段包含行尾空格；`.gitattributes` 仅针对本次包和示例的生成文件忽略这一项空白检查，保留 Unity 序列化输出，不格式化场景、材质或设置文件。源码和文档仍执行完整 `git diff --check`。
