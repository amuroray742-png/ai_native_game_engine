# 服务器实施记录

## S1 首批：Actor 执行器与分帧基础（2026-11-26）

目标是为[服务器设计](server-architecture.md)的房间宿主和连接宿主建立可单独验证的执行与资源边界。本批只实现基础库，不启动角色进程；S1 尚未达到全部退出条件，S2–S6、H1–H4 尚未实施。

### 已实现边界

| 代码 | 行为与所有权 |
| --- | --- |
| [ActorRef](../src/AiNative.Server/Actors/ActorRef.cs) | actorId、随机 incarnation、TryPost 的 Accepted／Full／Stopped；Accepted 仅代表接纳 |
| [DedicatedThreadActor](../src/AiNative.Server/Actors/DedicatedThreadActor.cs) | 工厂、OnStart、消息和 OnStop 在同一专用线程；空闲等待唤醒；RequestStop 停止接纳并排空；StopAsync 等待清理与真实线程退出 |
| [SharedActor](../src/AiNative.Server/Actors/SharedActor.cs) | 非房间 Actor 使用线程池串行执行，每轮默认最多 32 条；停止排空；不自动重启故障实例 |
| [ActorMailbox](../src/AiNative.Server/Actors/ActorMailbox.cs) | 条数和正文字节双预算，正在执行的消息也占用预算；只有执行结束才归还 |
| [FramedStream](../src/AiNative.Server/Transport/FramedStream.cs) | 四字节大端正长度，读取前校验上限；单读／单写可并行，同方向重入拒绝；短读与截断处理；Stream.WriteAsync 完整写契约；I/O 失败后禁止复用 |
| [ReceivedFrame](../src/AiNative.Server/Transport/ReceivedFrame.cs) | 独占正文至 Dispose，归还接收预算；发送借用正文到任务结束；当前不使用对象池 |

`AiNative.Server` 当前是只依赖 .NET 10 标准库的实际项目，后续有调用需求时才添加协议与 Hosting 引用。`AiNative.Server.Tests` 使用已有锁定版本的 xUnit，直接引用 Server，不引用原测试入口、Unity 或原生物理项目。两项目已追加到根解决方案，原有项目与未提交内容保留。其余规划项目未创建。

Actor 的行为对象仅由执行器访问；调用方不得持有其可变状态作为跨 Actor 访问通道，消息必须不可变。工厂在返回行为对象前发生失败时，自行清理尚未转交的资源；返回后即使 OnStart 失败，执行器也调用一次 OnStop。OnStop 须支持部分初始化。Completion 传播处理／清理异常；监督者必须观察它，不能把 Accepted 当作业务回复。故障时待处理消息丢弃，后续业务宿主需要按操作身份报告失败／未知结果。取消 StopAsync 仅取消等待，不能终止线程或跨线程释放模拟。

TryPost 的 byteCount 由内部消息编码／适配方提供，是正文字节预算，不是对任意 CLR 对象的内存估算。当前没有序列化 DTO 或房间业务，不能据本批声称客户端不能伪造预算。泛型执行器不承诺任意 T 自动不可变。

FramedStream 不拥有 Stream，不负责认证、JSON、超时或关闭。接收正文满时当前原语报错，连接宿主尚须实现调用前暂停预读和恢复；不能把这一失败路径称为完整背压。异常发生后连接所有者必须关闭底层流并等待另一方向 I/O 收尾。Frame Dispose 后不再使用已借用 Body。当前预算覆盖正文，帧头、DTO、TLS 和系统缓冲另计。没有发送队列、路由公平调度或合批，没有原始 Socket.SendAsync 适配；Stream 层不存在成功返回部分写入的契约。

### 检查与复现

```powershell
dotnet restore tests/AiNative.Server.Tests/AiNative.Server.Tests.csproj --locked-mode
dotnet test tests/AiNative.Server.Tests/AiNative.Server.Tests.csproj --no-restore --logger "trx;LogFileName=server-tests.trx" --results-directory TestResults/server/<run-id>
dotnet format whitespace tests/AiNative.Server.Tests/AiNative.Server.Tests.csproj --verify-no-changes --no-restore
```

本机 .NET SDK 10.0.202：26 项测试通过，0 失败、0 跳过，覆盖：

- 固定线程创建／处理／清理、FIFO、条数与字节预算、故障丢弃、初始化失败清理、取消停止等待。
- 共享执行器并发投递串行处理、多轮执行、故障停止、并发停止不重复清理。
- 拆帧／粘包、帧边界 EOF、非法与超长长度、各位置截断、在途预算、幂等释放、取消与失败后禁止续用。
- 回环 TCP 同时双向收发；写失败不报成功并归还配额。

本机证据：`TestResults/server/s1-foundations/s1-reviewed.trx`。独立审查发现 SharedActor 终止检查与调度抢占竞态，已改为先取得调度权再检查终止状态，并增加并发停止回归。Reviewer 复核关闭该问题，独立重跑 26 项通过，证据为 `TestResults/server/s1-foundations-review-fixed/review-fixed.trx`。锁定依赖恢复、只读空白格式与文档相对链接检查通过。此记录不代表六进程、TLS、性能、多机、Unity 或远端 CI 通过。

### 剩余实施顺序

1. 完成 S1：两个版本化协议项目及严格 DTO 校验、角色授权与代次验证；配置、生命周期和 Host 显式角色选择；认证 ServerLink、连接超时、接收背压与发送有界调度。补齐设计中对应反例测试。
2. S2：房间状态机、定时期限唤醒、游戏适配、GridGame 权威命令和去重；基于本批专用线程执行器接入，不在 Actor 基础层实现玩法。
3. 按原 S3–S6 顺序实施控制服务、网关、重连、六进程故障与 Unity 表现；每阶段取得独立审查和对应证据再声明退出。

本批没有服务可执行入口，不应尝试部署或把基础测试当作服务器可玩版本。
