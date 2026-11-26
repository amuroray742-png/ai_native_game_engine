# 服务器 S1 验收

日期：2026-11-26。范围严格对应[架构第 12 节](server-architecture.md#12-实施顺序与验收)的 S1：协议、Actor、Transport、Lifecycle、Configuration 与显式角色 Host；S2–S6 的房间、账号/目录业务、客户端重连、六进程游戏验收和 Unity 尚未实施。Host 始终 ready=false，不裁定玩法。

## 交付与逐项证据

| S1 条件 | 实际交付/验证入口 | 结果 |
| --- | --- | --- |
| 客户端共享协议 | Multiplayer.Protocol（netstandard2.1），MultiplayerCodecTests | 所有消息方向/字段、版本、计数、UTF-8、重复/未知字段、嵌套 Unicode、主动快照通过 |
| 独立内部协议 | Server.Protocol（net10.0），ServerCodecTests/ServerRoutingTests | 角色授权、源/目标 bootId、epoch、内外房间身份、玩家连接代次通过 |
| 房间专用线程与其他 Actor 共享执行器 | ActorTests | 固定线程创建/消息/清理、FIFO、故障终止、取消等待与共享串行调度通过 |
| Full/Stopped、在途预算与控制预留 | ActorControlReserveTests/ActorTests | 条数/字节均包含执行中消息；普通满时控制可入队，维持 FIFO，停止后拒绝 |
| 异步分帧与 I/O 失败 | FramedStreamTests | 拆帧/粘包、非法长度/截断、并行单读单写、写失败、取消、释放恰好一次通过 |
| 写完整语义 | FramedStreamTests | 使用 Stream.WriteAsync 的完整 Memory 写入契约；失败不报成功。没有原始 Socket.SendAsync 适配，因此不虚构原始短写测试 |
| 接收背压 | FramedStreamTests | 条数满停在帧头前、字节满停在正文前、取消与释放竞争、TCP 接收暂停仍可回包通过 |
| 公平发送/控制额度 | BoundedSendQueueTests | 路由按字节轮转、FIFO、大小帧、控制突发、在写计费、并发排空、失败/期限结算通过 |
| TLS 与认证身份 | AuthenticatedServerStreamTests | 真实双向 TLS、pin/EKU/有效期、角色冒用、错误服务端证书、TLS 后停在 Hello 的超时通过 |
| 完整 ServerLink 基础循环 | ServerLinkTests | TLS 后双向协议收发、拒绝投递不阻塞下一帧、非法协议断链、超时/排空、拒绝注册旁路通过 |
| 期限不被碎片刷新 | FramedStreamTests/ServerLinkTests | 首字节等待与完整帧截止分离，滴流不能延长期限；写期限覆盖整帧 |
| 严格配置与拓扑 | DeploymentConfigurationTests | 唯一身份/端点、角色必需字段、错误预算、静态出站容量、游戏摘要与协议一致通过 |
| 显式进程入口 | HostProcessTests | gateway/auth/lobby 的配置角色选择、拒绝 game/错配、Lobby 真实子进程监听与停止通过 |
| 启动顺序/排空 | HostProcessTests | Lobby→Auth 在 Worker 未完成握手时可启动，独立 BeginDrain 停止准入并退出通过 |
| 生命周期与失败边界 | RoleLifetimeTests | 逆序幂等清理、共享期限、超时仍尝试其他清理、部分启动失败不报干净停止通过 |
| 独立测试与依赖边界 | Server.Tests.csproj/各 csproj | 仅构建纯 .NET 服务器项目与 Host，无 Jolt/Unity 构建依赖；客户端不反向依赖服务器 |

当前本机最终测试 **181 项通过，0 失败、0 跳过**，证据 `TestResults/server/s1-final/s1-final.trx`。构建由测试命令执行，锁定恢复通过。只读空白检查单列执行，工具存在 netstandard 项目引用元数据工作区警告，不据此宣称完整语义分析或远端 CI。未运行引擎、Unity、多机、性能或六进程完整游戏验收。

```powershell
dotnet restore tests/AiNative.Server.Tests/AiNative.Server.Tests.csproj --locked-mode
dotnet test tests/AiNative.Server.Tests/AiNative.Server.Tests.csproj --no-restore --logger "trx;LogFileName=s1-final.trx" --results-directory TestResults/server/<run-id>
dotnet format whitespace tests/AiNative.Server.Tests/AiNative.Server.Tests.csproj --verify-no-changes --no-restore
dotnet format whitespace src/AiNative.Server.Host/AiNative.Server.Host.csproj --verify-no-changes --no-restore
```

## 审查与范围

各批独立审查及修复证据见[实施记录](server-implementation.md)：共享 Actor 调度竞态、非法 Unicode、主动快照、内外房间身份、注册例外、配置摘要、Auth 启动顺序、排空准入和静态容量均有对应修复/回归。最终 S1 代码退出审计通过，未发现阻塞问题；Reviewer 独立运行全量 181 项通过，证据 `TestResults/server/s1-final-audit/s1-final-audit.trx`。S1 已完成，整体服务器仍待 S2–S6。

不把后续阶段需求偷换成已实现：GameWorker/Room 和游戏适配属于 S2；认证会话/目录/幂等台账与业务 Ready 属于 S3；Gateway 玩家接入/跨网关恢复属于 S4；六进程脚本、负载/容量、多机故障与诊断测量属于 S5；Unity 属于 S6。发送合批、性能优化及公网安全运营不作为本轮性能通过声明。既有模拟接口、v2 文件格式、单机 PlayHost 和恢复承诺未改变。
