# 服务器实施记录

当前状态（2026-11-26）：**S1 已完成**，181 项测试通过，最终独立审计通过。逐项交付见[S1 验收](server-s1-acceptance.md)、[线协议](server-protocol.md)与[Host](server-host.md)。以下各批保留当时状态，早期“尚未完成”不代表最终状态；S2–S6 尚未实施。

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

`AiNative.Server` 当前是只依赖 .NET 10 标准库的实际项目，后续有调用需求时才添加协议与 Hosting 引用。`AiNative.Server.Tests` 使用已有锁定版本的 xUnit，直接引用 Server，不引用原测试入口、Unity 或原生物理项目。首批两项目已追加到根解决方案，原有项目与未提交内容保留。第三批已新增两个协议项目，其他规划项目仍未创建。

Actor 的行为对象仅由执行器访问；调用方不得持有其可变状态作为跨 Actor 访问通道，消息必须不可变。工厂在返回行为对象前发生失败时，自行清理尚未转交的资源；返回后即使 OnStart 失败，执行器也调用一次 OnStop。OnStop 须支持部分初始化。Completion 传播处理／清理异常；监督者必须观察它，不能把 Accepted 当作业务回复。故障时待处理消息丢弃，后续业务宿主需要按操作身份报告失败／未知结果。取消 StopAsync 仅取消等待，不能终止线程或跨线程释放模拟。

TryPost 的 byteCount 由内部消息编码／适配方提供，是正文字节预算，不是对任意 CLR 对象的内存估算。当前没有序列化 DTO 或房间业务，不能据本批声称客户端不能伪造预算。泛型执行器不承诺任意 T 自动不可变。

FramedStream 不拥有 Stream，不负责认证、JSON、超时或关闭。首批接收正文满时报错；下述第二批已改为异步等待额度。连接宿主仍须接入等待超时、关闭与下游邮箱背压。异常发生后连接所有者必须关闭底层流并等待另一方向 I/O 收尾。Frame Dispose 后不再使用已借用 Body。当前预算覆盖正文，帧头、DTO、TLS 和系统缓冲另计。没有发送队列、路由公平调度或合批，没有原始 Socket.SendAsync 适配；Stream 层不存在成功返回部分写入的契约。

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

1. 完成 S1：两个版本化协议项目及严格 DTO 校验、角色授权与代次验证；配置、生命周期和 Host 显式角色选择；认证 ServerLink、连接超时、接收背压的宿主集成与发送有界调度。补齐设计中对应反例测试。
2. S2：房间状态机、定时期限唤醒、游戏适配、GridGame 权威命令和去重；基于本批专用线程执行器接入，不在 Actor 基础层实现玩法。
3. 按原 S3–S6 顺序实施控制服务、网关、重连、六进程故障与 Unity 表现；每阶段取得独立审查和对应证据再声明退出。

本批没有服务可执行入口，不应尝试部署或把基础测试当作服务器可玩版本。

## S1 第二批：接收背压（2026-11-26）

本批补齐分帧层的暂停与恢复，不新增项目、依赖或服务入口。既有 Actor、发送准入、TCP 帧格式保持不变；尚未达到 S1 全部退出条件。

### 行为与所有权

- `FramedStream.ReadAsync` 在读取帧头前异步取得一个条数额度；条数满时不预读下一帧头。
- 长度通过校验后，异步取得正文额度；字节满时不分配或读取正文。释放 `ReceivedFrame` 唤醒等待者，继续读取而不丢弃该帧。
- `FrameBudget` 在同一短锁内检查额度并取得等待信号，释放时换代信号并在锁外通知；等待不占专用线程，不轮询。取消等待不会占用后续释放的额度。
- EOF、非法长度、截断、取消或读取故障归还本次已经取得的条数／字节；成功交付后由消费者 Dispose 恰好归还一次。任何读取取消仍使该分帧实例失效，宿主应关闭流，不续读。
- 收发预算应分开配置。此预算用于连接级流量限制；若跨连接共享，等待帧头的空闲读取也占条数，且竞争唤醒不保证公平。它不能替代进程准入、每路由公平发送或控制消息保留额度。
- 取消令牌覆盖预算等待和 I/O；期限由后续连接宿主设置。当前未实现等待年龄监控、慢连接超时、TLS、协议解码、下游邮箱满的协调与发送调度，不能称为完整 ServerLink 背压。

### 验证

本机独立服务器测试 33 项通过，0 失败、0 跳过。新增覆盖帧头前暂停、正文前暂停、两处取消、EOF 配额回收、重复 Dispose 不多放额度、共享预算多等待者竞争、100 轮取消与释放竞争，以及真实 TCP 在接收暂停期间仍能回包、释放后按序续读。证据：`TestResults/server/s1-receive-backpressure/final.trx`。

独立 Reviewer 对两阶段预留、通知换代、失败路径和所有权进行了源码审查，未发现阻塞问题；初轮独立运行 30 项通过，证据：`TestResults/server/s1-receive-backpressure-review/review.trx`。补齐竞争和真实 TCP 双向测试后，Reviewer 最终复核关闭本轮审查，独立运行 33 项通过，证据：`TestResults/server/s1-receive-backpressure-review/review-final.trx`。只读空白格式、Markdown 围栏与相对链接、`git diff --check` 检查通过。未运行引擎、Unity、性能或六进程验收；现有解决方案改动与 NetworkDemo 目录保持原状。

## S1 第三批在制：两个版本化协议项目（2026-11-26）

S1 目标保持为完整协议、Actor、Transport、Lifecycle、Configuration、Server.Host 及相应退出验证。以下进展不代表 S1 已完成。

- 已创建 [Multiplayer.Protocol](../src/AiNative.Multiplayer.Protocol/AiNative.Multiplayer.Protocol.csproj)，目标 netstandard2.1/C# 9，沿用仓库锁定 System.Text.Json 10.0.14；不引用服务器、Unity、Hosting 或游戏。`MultiplayerMessage` 持有校验后独立的 JsonElement 副本，`MultiplayerCodec` 提供正文编码与 TryDecode；不改变现有 TCP 帧格式。
- 已实现客户端 v1 消息方向、请求 ID、外层和已知 payload 必填／未知字段、递归重复字段、UTF-8 与转义 Unicode、深度 16、64 KiB、UUID、十进制 Int64 字符串、席位及玩家排列校验。RoomSnapshot 可以是带请求 ID 的响应，也可以是 null ID 的主动基线推送。游戏 command/configuration/observation/events 内部字段由适配拥有，协议只验证对应结构与递归 JSON 完整性；客户端 side 由房间适配按 InvalidCommand 拒绝，不在网络层消费序号。
- 已创建 [Server.Protocol](../src/AiNative.Server.Protocol/AiNative.Server.Protocol.csproj)，目标 net10.0，不引用 Actor 或游戏；第四批增加对客户端协议的单向引用来校验嵌套消息。`ServerCodec` 使用独立版本、256 KiB/深度 32，以 `ServerReceiveContext` 校验认证对端、目标身份、bootId、角色授权和 epoch。上下文必须由认证链路和注册状态提供；当前没有 TLS 接入，因此仅为注入上下文的单元验证。
- 内部 payload 已开始定义注册、容量、分配、成员、绑定和转发消息结构；本批结束时尚未完成内部协议交付：完整应答族、注册握手、嵌套路由与信封/当前所有者的一致性检查、全部消息反例和独立审查仍待完成。内部字段草案目前以源码为在制依据，完成前同步为正式线协议说明，不能据已通过的信封测试声明可部署。
- 两协议项目已加入根解决方案及独立服务器测试引用；原有 Unity 解决方案条目保持原状。没有增加无内容的目录，也没有修改 Core/Hosting。

客户端独立审查修复两项：非法转义代理项引发解码异常；主动 RoomSnapshot 被错误要求携带请求 ID。最终客户端协议测试 39 项独立通过，证据 `TestResults/server/s1-client-protocol-review/client-protocol-review-final.trx`，该部分审查已关闭。总体服务器测试 81 项通过（Actor/Transport 33、客户端协议 39、内部信封 9），仅覆盖已写测试，不证明剩余 S1 能力。内部协议尚未独立审查。只读空白检查返回成功，但工具报告 netstandard 项目引用没有匹配的元数据引用；正常构建该项目成功，暂不将此工具结果当作完整语义分析证据。

下一步继续补齐内部协议和路由校验，再实现部署配置/显式角色入口、TLS ServerLink、连接期限、有界发送调度、控制预留与统一关闭流程，最终按设计第 12 节逐项验收。六进程业务联通属于后续 S3–S5，不能以 S1 入口存在代替。

## S1 第四批：内部路由与双向 TLS（2026-11-26）

### 已实现

- 内部应答族、创建/加入成员意图、RoomRequest（恢复/查询/离开），字段与角色方向已写入[线协议说明](server-protocol.md)。Server.Protocol 现在单向引用 Multiplayer.Protocol，复用嵌套 RoomEnvelope 的校验，不让内部、客户端两套结构规则分叉；不依赖 Actor 或游戏。
- `ServerRouteValidation` 验证嵌套路由与信封对应的 Worker/Gateway/epoch，并提供房间当前 incarnation、玩家与连接代次的二次验证入口。RoomEnvelope 只允许六类房间消息，嵌套 match 必须与外层 route 相同。
- `ServerHelloCodec` 定义 TLS 后首帧，证书身份约束 serviceId/role，传递 bootId 与 epoch 信息。握手不直接使 Worker Ready，不替代控制注册。
- `AuthenticatedServerStream` 以精确叶证书 SHA-256 pin、有效期、EKU 和 TLS 私钥持有证明验证双方，固定 ALPN，TLS/Hello 共用可取消期限。拥有底层流，成功后由调用方 Dispose，失败/取消主动关闭。没有新增 TLS 第三方依赖，测试证书只在夹具内临时生成。

### 审查与证据

内部 Reviewer 发现 RoomEnvelope 曾未核对内外 match，且缺少非玩法 RoomRequest。已修复并回归；复核关闭，内部协议 46 项独立通过：`TestResults/server/s1-internal-protocol-review/internal-protocol-review-final.trx`。

TLS Reviewer 未发现阻塞问题；按建议补客户端拒绝错误服务端证书、TLS 完成后不发 Hello 超时。最终 9 项独立通过：`TestResults/server/s1-tls-review/tls-review-final.trx`。

总体独立服务器项目 127 项通过，0 失败、0 跳过（客户端协议 39、内部协议 46、Actor/分帧/背压 33、TLS 9）。本轮最终证据 `TestResults/server/s1-routing-tls/final.trx`。这些是本机回环和单元测试，不代表多机、性能、公网或远端 CI。

### S1 剩余项

S1 尚未完成：部署配置及显式 gateway/auth/lobby 进程入口、统一启动/停止/排空、完整 ServerLink 读写循环、认证连接容量与超时、下游邮箱背压、有界公平发送、控制消息预留，以及这些能力的集成与独立审查。未运行引擎或 Unity 测试；未改动原有 NetworkDemo 内容。

## S1 第五批：发送调度与 Actor 控制预留（2026-11-26）

新增 `BoundedSendQueue` / `SendQueueLimits`。发送入口复制正文并在准入时计费，队列和正在写入的帧共同占用总预算及路由子预算，实际写入结束才归还。返回 Accepted 只代表入队，输出 written Task 只代表本地完整写入，不代表远端收到或业务执行。发送中取消/异常使在写与待写任务全部失败，停止继续接纳；已经成功写完的前缀不改写为失败。

路由队列按字节 deficit/quantum 轮转，每路由保持 FIFO；大于 quantum 的合法帧累计额度后获得进展。独立控制队列只用于与房间无关的心跳/监督；房间绑定、基线和状态必须使用同一路由，不能借控制入口超车。连续控制达到 ControlBurst 后让出一次路由发送机会，计数在上限饱和，不随长期控制流溢出。总量分为固定控制份额与路由份额，数据不能借控制预留；没有额外正文对象池、合批或性能承诺。诊断发送仍须由后续 ServerLink 分类与限额，当前没有诊断消息族。

`Complete()` 停止接纳并唤醒写循环排空；只允许启动一次 RunAsync。队列不拥有底层流、没有独立计时器或关闭流程，完整 ServerLink 仍须设置发送期限、关闭流并等待 I/O 收尾。单帧上限由对应 FramedStream 校验，Host 配置必须保证各类队列能容纳其最大合法帧。

Actor 增加可选 reservedControlMessages/reservedControlBytes 和 `ActorRef.TryPostControl`。默认预留为零，保留已有调用行为；可信宿主才使用控制入口，不根据客户端字段决定消息优先级。普通消息受总预算减预留限制，控制消息仍受总硬上限限制。两类消息保持单一 FIFO，正在执行也计费；控制额度满时仍明确返回 Full，由监督者实施设计要求的故障边界，不静默丢弃生命周期消息。

本轮新增 8 项发送调度与 4 项控制预留测试，总体 139 项通过，0 失败、0 跳过：`TestResults/server/s1-send-control/final.trx`。覆盖公平轮转、控制突发、复制所有权、在写计费、同路由在写时再入队、并发 Complete/Enqueue、写失败/取消结算、两种 Actor 的条数/字节预留与 FIFO。没有运行无关引擎或 Unity 测试。

剩余 S1：完整 ServerLink 收发/故障/超时与下游投递协调；严格部署配置；gateway/auth/lobby 显式 Host 与生命周期；对应集成和最终独立审查。完成全部退出条件后再统一提交并创建或更新 PR，不把本批基础测试当作整个 S1 退出。

第五批独立审查已关闭，未发现剩余阻塞问题；Reviewer 复核控制计数饱和与 Actor 分类预算释放，独立运行发送/Actor 相关 21 项通过。证据：`TestResults/server/s1-send-queue-review/final-review.trx`。只读空白检查与 `git diff --check` 通过；格式工具仍报告前述 netstandard 元数据工作区警告，不据此声明完整静态分析已完成。

## S1 第六批：部署配置与 ServerLink（2026-11-26）

`DeploymentConfiguration.Parse` 接收最多 1 MiB 的 UTF-8 JSON，不访问文件。严格拒绝未知/重复/缺失字段、错误类型和非法 Unicode。version=1，services 列表包含 serviceId、role（小写 gateway/auth/lobby/game）、address、certificateSha256 与可选 budgets；Gateway 必须有 clientPort 且没有 internalPort，其他角色必须有 internalPort 且没有 clientPort。Game 额外必填 gameId/gameVersion/configurationDigest（小写 SHA-256）。只允许明确 IP 地址，不允许通配监听；IPv4 映射地址归一后再检查端点冲突。serviceId、证书 pin、监听端点不能重复。

首期配置要求唯一 Auth 和 Lobby，至少一个 Gateway/Worker；Lobby 目录预算覆盖 Worker 房间总量。ServerBudgets 提供设计对应的连接、握手、房间、帧、接收/发送、期限初值并校验关系，最大帧必须能进入控制与路由发送额度。纯解析不读取证书私钥，也不声明端口可绑定。Select(serviceId,role) 要求显式匹配；通用 Host 不允许 game，GameServer 后续显式装配玩法。进程入口本批尚未创建。

`ServerLink` 接管已认证流，唯一读循环执行完整帧期限→协议/身份/路由验证→同步非阻塞投递；邮箱 Full/Stopped 交给 rejected 回调，继续读下一帧，不把网络拒绝伪装成规则结果。已解码 DTO 独立于接收缓冲。达到每轮 32 帧或 64 KiB 后让出执行机会。唯一写循环使用 BoundedSendQueue，按 route.roomId 保持同房间 FIFO，无路由的消息走控制额度。控制 Full 关闭链路；路由 Full 由调用者隔离该路由。发送期限覆盖整帧写入，异常/取消结算全部未完成任务。

必须由宿主先从可信 Lobby 确认控制 epoch，再创建 ServerLink；拒绝 null/空 epoch 和 AllowRegistration=true，不允许在长期连接上保留注册旁路。构造校验失败不转移认证流所有权，成功后由 ServerLink 拥有。CompleteSending 停止发送接纳，排空后关闭双向连接；Dispose 中断 I/O，RunAsync 等待两方向收尾并传播失败。只有单次运行，不自动重连或重放。回调不得阻塞或直接跨 Actor 修改业务状态，宿主还需实现按类型的投递、拒绝回复、监督与重连策略。

新增配置/真实 TLS Link/发送期限测试；本轮初轮审查发现配置接受大写游戏摘要而协议拒绝，以及长期 Link 可保留注册例外。两项已修复并增加反例。当前 Host、连接准入/握手容量执行、统一角色排空/停止与进程集成仍待实施，不因基础 Link 存在声称 S1 完成。

第六批最终总体 168 项通过：`TestResults/server/s1-link-config/final.trx`。独立 Reviewer 复核关闭上述两项，相关 37 项通过：`TestResults/server/s1-link-config-review/final-review.trx`。未运行引擎/Unity/多机/性能测试。S1 完成与提交 PR 目标仍在进行中。

## S1 第七批：生命周期与 Host（2026-11-26）

新增 RoleLifetime、RoleHost 与实际 AiNative.Server.Host 项目，已加入纯 .NET 解决方案及服务器测试构建依赖。行为与启动参数见[Host 说明](server-host.md)。Host 提供真实内部监听、出站认证、链路心跳、有限连接/握手并发与关闭入口，始终 ready=false，不把尚未装配的 S3/S4 业务宣称可用。

生命周期三项测试和初轮 Host 六项进程测试通过。独立审查指出启动顺序（Auth 不能等待 Worker 才监听）、独立 BeginDrain 未停止准入，以及静态连接容量不足可能卡在 Starting。已改为先认证 Lobby、其他对端后台退避，排空取消准入，配置预先验证必要出站额度，并补真实 TLS 启动顺序/独立排空与低容量回归。最终审查与 S1 总体验收尚未完成；不能据本批编译和初轮测试提交完成声明。

最终收口：Host/配置/生命周期审查修复已复核关闭，相关 36 项独立通过。随后对第 12 节 S1 全部交付逐项审计，首字节/完整帧期限分离并保持碎片不刷新，默认轮转额度统一 32 KiB。全量 181 项通过（`TestResults/server/s1-final/s1-final.trx`），独立最终审计同样 181 项通过（`TestResults/server/s1-final-audit/s1-final-audit.trx`），无剩余阻塞；S1 完成。总体联机与 S2–S6 不在本次完成范围。
