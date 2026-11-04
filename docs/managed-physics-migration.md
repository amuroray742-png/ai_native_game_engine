# 物理适配分层迁移

## 目标与边界

将实体身份、BodyID 分配、快照元数据和候选恢复编排迁到 `AiNative.Physics.Jolt`。Core、Physics 公共契约及游戏规则保持不变；继续使用 netstandard2.1 / C# 9。原生层保留 Jolt 资源聚合所有权、异常边界、工作线程事件收集、批量查询及内部状态校验与恢复。

原生世界仅认识 BodyID，不保存游戏实体 ID 或项目快照外层。C# 是实体映射与分配 generation 的唯一权威所有者；已删除 BodyID 的映射保留到下一次 Step 事件转换完成，避免复用导致事件串实体。状态查询和事件以批量接口跨越 ABI，不引入工作线程到 C# 的回调。

## 契约

- C ABI 升至 4，物理快照 magic 升至 `0x344a4e41`。旧后端或旧 magic 明确拒绝，不静默迁移。外层协议仍为 v2，PhysicsSandbox 游戏版本仍为 2。
- C# 快照外层依次为 magic、UInt32 工作线程数、1024 字节 generation、UInt32 对象数、按实体 ID 排序的对象定义与 BodyID，随后为完整 Jolt 内部状态。对象记录保持 72 字节，包含实体 ID、定义和 BodyID。
- 创建：C# 持有 SafeHandle；原生创建入口负责返回句柄之前的失败清理。释放明确调用 Dispose，原生聚合对象负责停止工作线程和按依赖顺序销毁资源。
- 恢复：C# 离线校验元数据 → 创建候选 → 按记录重建对象与映射 → 原生校验并恢复内部状态 → 完整快照核对 → 返回候选。任何失败释放候选，调用方持有的原世界不受影响。
- 原生回调记录 BodyID / 子形状 / 类型；Step 完成后 C# 映射为实体、规范方向、排序去重，再清理退役映射。转换失败使该世界故障，避免丢事件后继续推进。
- 保存仍要求更新及事件消费完成、删除后已完成下一次 Step；不主动推进来制造保存边界。
- 复用 joltc 的 `JPH_PhysicsSystem_Update2`、临时分配器接口和物理设置 Get/Set，移除重复的 AJ_Update / AJ_EnableDeterminism。使用上游 16 MiB 临时分配器及其 malloc fallback，配置进入构建身份；保留完整状态、单线程 jobs、异常安全创建和最终注册表清理扩展。

## 实施与验证

实现由主 Agent 完成，最终由独立 Reviewer 复核 C#/原生所有权、损坏状态和回调边界。保留现有玩法、并行物理与 CLI 验收；新增实体排序/退役映射、托管快照损坏、候选清理及旧格式拒绝回归。原生损坏探针针对重建候选后的内部状态，托管测试覆盖完整快照。实际结果在执行后追加；未执行的 Windows/Linux CI 或 Unity 验收不声明完成。

## 本地验证记录

2026-11-04 Windows x64：原生构建与线程启动失败探针通过；托管构建零警告/错误，236 项测试全部通过，无失败或跳过；108 次真实 CLI 全部通过；原生子进程完成内部状态的 334 个截断长度（含零长度）与 334 个逐字节变异，资源计数回到零。与旧 ABI 3 探针不同，原生探针仅处理 Jolt 内部流；完整快照的所有截断位置和每字节变异由新增托管回归覆盖，失败候选清理且旧世界可继续推进。

新增回归还覆盖空世界、删除最后刚体后保存、实体排序与 BodyID 排序不同、UInt64 高位实体、退役碰撞身份、非法操作不改变分配状态、跨 Step generation 绕回及中途恢复、旧后端和旧 magic 拒绝。未通过注入模拟原生 Add 内存耗尽，不将普通非法输入测试描述为该失败路径的执行证据。

证据：`TestResults/managed-native-final.log`、`TestResults/managed-build-final.log`、`TestResults/managed-unit-final/`、`TestResults/managed-process-final/`。只读格式检查、文档链接、Python 语法和 `git diff --check` 通过。独立源码审查未发现阻断项，复核了托管预留/回滚、退役映射、恢复候选所有权及上游 allocator 行为。远端双平台验证待本分支 CI 执行；Unity Editor、Player、IL2CPP 和性能收益仍未验收。
