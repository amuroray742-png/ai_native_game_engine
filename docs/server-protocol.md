# S1 联机与内部线协议

本文记录当前协议源码对应的边界，配合[架构](server-architecture.md)与[实施记录](server-implementation.md)。协议、TLS、基础 ServerLink 与配置校验已实施；Host 和生命周期基础已完成，角色业务待 S2–S4，不能据此启动六进程或声明 S1 退出。

## 项目与所有权

- `AiNative.Multiplayer.Protocol`：netstandard2.1，只依赖锁定的 System.Text.Json。客户端 v1 消息见架构第 8 节。Message 持有 Clone 后的 JsonElement，不借用网络接收帧或调用方 JsonDocument；游戏字段只做 JSON 结构验证，玩法适配继续校验其规则。
- `AiNative.Server.Protocol`：net10.0，提供内部 v1 消息、路由值对象和编解码。单向引用 Multiplayer.Protocol，唯一用途是复用 RoomEnvelope 中客户端消息的严格校验，避免两套校验漂移；客户端项目不反向引用服务器。内部版本仍独立于客户端版本。
- `AiNative.Server`：引用两协议，拥有 TLS、TCP 帧和后续连接生命周期；不依赖游戏。Hosting 引用留到实际游戏宿主接入时添加。

反例返回明确的 MessageError／ServerMessageError；编码本地非法消息抛 ArgumentException。网络解码不通过异常表达预期结构拒绝。TCP 仍为四字节大端正长度加 UTF-8 JSON，客户端上限 64 KiB/深度 16，内部上限 256 KiB/深度 32。重复字段、非法 UTF-8、非法转义代理项、未知/缺失字段、非法类型拒绝，包括不透明游戏 JSON 内的重复字段与非法字符串。

## TLS 与启动身份

`AuthenticatedServerStream` 工厂取得底层 Stream 所有权，成功由返回对象拥有，认证、握手失败或取消均关闭流。调用方拥有本地证书，至少保持到握手完成。失败后不得复用底层连接；当前 Dispose 中止连接，不代表业务排空或消息送达。

首期部署使用**精确叶证书 SHA-256 pin**映射 serviceId/role；TLS 私钥持有证明、证书有效期与对应 clientAuth/serverAuth EKU 都必须满足。不使用系统根链或 DNS 名称作为替代信任，也不根据正文声称的角色授权。相同 pin 在允许列表出现多次时拒绝，Host 仍须在启动时校验配置唯一性。证书更新要求更新部署清单并重建链路；在线撤销分发与公网证书运营不在首期范围。TLS 1.2/1.3，禁重协商，ALPN 固定 `ainative-server/1`。

TLS 完成后双方各发送一条 `LinkHello`，使用同一 TCP 帧格式，正文上限 4096 字节/深度 4。TLS 和应用握手共用一个截止时间（调用方提供，必须大于零、不超过一分钟；设计默认 5 秒）。必填字段：

| 字段 | 类型/要求 |
| --- | --- |
| serverProtocolVersion | JSON 整数 1 |
| type | LinkHello |
| serviceId | 与证书映射完全一致的字符串 |
| bootId | 非空 UUID，当前进程启动身份 |
| role | Gateway、Auth、Lobby、Game，与证书映射一致 |
| controlEpoch | UUID 或 null，尚未确认控制 epoch 时为 null |

握手只确认对端进程身份，不代表 Worker 注册成功、控制服务 Ready 或可接纳玩法。只有受信 Lobby 的控制协调才能确认 epoch；不能直接采用任意对端 Hello 的 epoch。Host 已从可信 Lobby 确认 epoch 后构造 ServerLink，业务注册状态机仍由 S3 实施。业务信封的 targetBootId 必填：应用握手完成后双方已知对端 bootId，不需要空目标代次。

## 内部信封与路由

固定字段：`serverProtocolVersion,type,operationId,sourceServiceId,sourceBootId,targetServiceId,targetBootId,controlEpoch,payload`。operationId 为 UUID，所有业务计数使用无前导零非负 Int64 十进制字符串。UUID 为 36 字符 D 格式。

`ServerReceiveContext` 由认证链路与本地控制状态构造，不能从待验消息反推。来源、目标身份及 bootId 精确匹配；角色由上下文提供。通常要求本地已有 epoch 且信封完全一致。仅协议解析器在显式 AllowRegistration 的 RegisterWorker 请求中允许 null epoch；实际 ServerLink 拒绝打开该例外，必须由宿主先确认 epoch 再建立业务链路。应答使用已确认 epoch。

- room route：`controlEpoch,roomId,workerId,workerBootId,roomIncarnation`。
- connection route：`gatewayId,gatewayBootId,connectionId,connectionGeneration`。
- match：`controlEpoch,roomId`。

协议层核对 route 的 epoch、直接通信 Worker 的身份与 bootId，以及直接通信 Gateway 的身份与 bootId。成员意图和 activeMatch 的 epoch 也必须一致。它不替代权威目录与房间状态：房间 Actor 应调用 `ServerRouteValidation.CheckRoom` 或 `CheckPlayer`，传入自己当前的 RoomRoute/PlayerBinding，拒绝旧 incarnation、旧连接代次或其他玩家。绑定新连接的操作由 Auth 发起，由房间执行专门绑定状态转换，不能套用普通玩法的旧绑定检查代替它。

RoomEnvelope 的 message 必须通过客户端 codec，只允许 RoomSnapshot、CommandResult、StateUpdate、RoomStatus、RoomLeft、Error；嵌套 match 必须与外层 route 的 epoch/roomId 一致。它不能包装 Welcome 等身份消息。Gateway 还须以当前连接绑定检查最终收件人。

## 内部消息字段表

以下字段全部必填，只有明确标注可空的值允许 null。`code` 是操作结果码；`reason` 是架构列出的房间终止原因。结构校验不替代 Actor 的幂等台账、状态转换和业务授权。

| type | 方向 | payload |
| --- | --- | --- |
| RegisterWorker | Game → Lobby | gameId、gameVersion、configurationDigest（小写 SHA-256 hex）、maxRoomThreads（正计数） |
| RegisterWorkerResult | Lobby → Game | code |
| CapacityReport | Game → Lobby | activeThreads、reservedThreads、draining（布尔） |
| ControlHeartbeat / ControlHeartbeatResult | Game → Lobby / Lobby → Game | nonce（计数） |
| AllocateRoom | Lobby → Game | allocationId、roomId、gameId、gameVersion、configurationDigest |
| QueryAllocation | Lobby → Game | allocationId |
| AllocationResult | Game → Lobby | allocationId、code、route（Ok 必有，其他码为 null） |
| Authenticate | Gateway → Auth | connection、resumeToken（可空） |
| AuthenticateResult | Auth → Gateway | code、connection、playerId、resumeToken、activeMatch；Ok 的 playerId/token 必有且代次正数，失败三者为 null |
| ResolveMembership | Auth/Gateway → Lobby | playerId |
| ResolveMembershipResult | Lobby → Auth/Gateway | code、playerId、membershipOperationId、route；最后两者同时存在或同时 null |
| ReserveMembership | Gateway → Lobby | membershipOperationId、playerId、intent |
| ReserveMembershipResult | Lobby → Gateway | code、membershipOperationId、playerId、route（Ok 必有，其他码为 null） |
| AdmitPlayer / QueryMembership | Lobby → Game | membershipOperationId、playerId、route |
| MembershipResult | Game → Lobby | membershipOperationId、playerId、route、code、seat（Ok 为 0/1，其他码 null） |
| PrepareBinding | Gateway → Auth | playerId、route、connection |
| BindPlayer | Auth → Game | playerId、route、connection |
| PrepareBindingResult / BindingResult | Auth → Gateway / Game → Auth | playerId、route、connection、code |
| RelayCommand | Gateway → Game | playerId、route、connection、requestId、requestSequence、expectedTick、command |
| RoomRequest | Gateway → Game | playerId、route、connection、requestId、requestType（ResumeRoom/GetRoom/LeaveRoom） |
| RoomEnvelope | Game → Gateway | route、connection、message（完整客户端信封） |
| GatewayRouteLost | Gateway → Game | route、connection |
| RoomClosed | Game → Lobby | route、reason |
| WorkerClosed | Lobby → Auth/Gateway | workerId、workerBootId、reason |

intent 两种严格形状：`{type:"Create",gameId,gameVersion}` 或 `{type:"Join",match}`。Auth 分配候选连接代次，Gateway 不能把客户端输入直接作为内部 connection/route/playerId；首期服务端测试必须覆盖这条装配边界。

内部 code 当前枚举：Ok、Busy、Rejected、NotAllocated、NotAdmitted、Pending、OperationConflict、OperationExpired、RoomLost、StaleRoute、StaleConnection、InvalidResumeToken、SessionExpired、AlreadyInRoom、RoomFull、NotRoomMember、RoomNotFound、ControlUnavailable、ServiceUnavailable、OutcomeUnknown。各操作实际允许的状态转换仍由目标 Actor 规定；Unknown 不是“确定未执行”。成功条件不得仅凭收到 TCP 帧宣告。

DrainWorker/StopRole 属于本地运维入口到 Supervisor 的生命周期命令，不授予任何远端角色通过内部业务信封停止另一进程的能力；后续 Host 负责本地入口。分配/成员/绑定的未知结果查询与幂等执行、WorkerClosed 的可信丢失判定属于 S2/S3 业务实现，本文的 DTO 并不实现它们。

## 验证边界

协议测试覆盖客户端所有消息、内部字段表的成功编码与未知/缺失字段反例、源/目标 bootId 和 epoch 过期、Gateway 命令角色授权、嵌套路由与房间代次、内外 match 不一致。TLS 测试使用运行时临时证书和真实回环 TCP，覆盖双向身份、错误证书、过期/用途错误、冒用身份、TLS/应用握手超时及加密分帧数据。证据索引见实施记录。

尚需接入的验证包括角色启动配置、完整 ServerLink 有界收发、下游背压、发送公平性、控制预留、连接关闭/排空与进程入口；协议单元测试和回环 TLS 不代表六进程、性能或公网验收。
