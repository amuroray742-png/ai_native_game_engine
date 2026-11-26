# S1 角色 Host 与部署配置

`AiNative.Server.Host` 是实际 .NET 10 入口，同一可执行文件每次只启动 gateway、auth 或 lobby。game 必须由后续显式装配玩法的 Examples.GameServer 启动。本入口当前只提供传输、认证、生命周期与健康状态；**始终 ready=false，不接受游戏客户端，不提供账号、匹配或房间业务**。这些业务按 S3/S4 实施，不用基础链路连通冒充服务器可玩。

## 配置与运行

部署清单 JSON 使用 version=1 和 services 数组。每个实例的字段如下，未知/重复字段拒绝。配置只保存非敏感身份与预算，临时证书、密码、PID、bootId 不写入配置。

| 字段 | 规则 |
| --- | --- |
| serviceId | 唯一 ASCII 标识，长度 1–128 |
| role | gateway/auth/lobby/game |
| address | 明确 IP 地址，禁止通配；IPv4 映射地址归一后检查冲突 |
| certificateSha256 | 唯一叶证书 SHA-256 指纹，64 位 hex |
| internalPort | Auth/Lobby/Game 必填，Gateway 不配置 |
| clientPort | 仅 Gateway 必填；S1 不打开游戏监听 |
| gameId/gameVersion/configurationDigest | 仅 Game 必填，摘要严格为小写 SHA-256 |
| budgets | 可省略，使用 ServerBudgets 待测初值；可以覆盖其中字段 |

首期唯一 Auth/Lobby；至少一个 Gateway/Worker。Lobby 目录上限须覆盖所有 Worker 房间上限，主动连接额度须覆盖静态出站拓扑。证书文件由运行环境提供，含私钥且 pin 与部署清单匹配。S1 没有自动生成生产证书的脚本，测试夹具只生成临时证书。

```powershell
dotnet run --project src/AiNative.Server.Host -- --deployment <deployment.json> --service lobby-1 --role lobby --validate
dotnet run --project src/AiNative.Server.Host -- --deployment <deployment.json> --service lobby-1 --role lobby --certificate <run-directory/lobby.pfx>
```

加密证书密码只通过 `--password-env <环境变量名>` 读取，不接收命令行密码值。`--validate` 只检查配置和角色选择，不打开证书/端口。启动失败 stderr 只输出错误类别，不打印凭证或完整输入。

默认 Ctrl+C 触发关闭。测试或本地编排可显式加 `--control-stdin`，通过该子进程专用 stdin 发送 status、drain 或 stop；stdin EOF 也关闭。它不是远程管理端口。drain 在 S1 没有活跃房间业务，停止准入后进入连接排空；S2/S3 接入房间后按架构补自然排空期。

## 生命周期与认证链路

RoleLifetime 区分 Starting、Live、Ready、Draining、Stopping、Stopped、Faulted。S1 Host 只进入 Live，不调用 MarkReady。资源在启动时显式登记，逆序清理，所有清理共享一个角色级截止时间。回调必须非阻塞并返回 Task，不能在监督线程同步 Join 房间。超时记录资源名、返回不干净结果并观察迟到异常；不把强杀当正常释放。

Lobby 产生随机控制 epoch 并监听内部端点。Auth/Gateway 首先认证连接 Lobby，取得可信 epoch，然后才启动其他内部链路；Auth 先监听，不等待尚未启动的 Worker。其他对端连接在后台按有上限的随机退避重试，不重放已发业务。静态拓扑与角色授权见[架构](server-architecture.md)。同一对端只保留一条活动链路，旧连接尚未清理时拒绝第二条，避免并行路径破坏顺序。Lobby 失联使依赖角色故障并排空，不把新 epoch 静默接到旧进程状态上。

内部 Socket 显式 NoDelay=true。接纳前取得总连接与 TLS 握手额度，失败即关闭本次 Socket；认证成功后释放握手额度，连接真正收尾后释放连接额度。接入内部 LinkHello 和 ServerLink；新增 LinkPing/LinkPong 只做进程传输心跳，不代表玩家在线、业务提交或 Worker 注册成功。业务消息返回本地 Stopped 并记录 business-not-ready，不接纳玩法。

BeginDrain 取消新监听/重试接纳；关闭阶段停止发送接单并等待链路任务，截止时取消和关闭剩余流。停机日志保留 serviceId、bootId、状态及事件名，不含 token、私钥或原始消息正文。输出失败使角色进入故障，不能声明干净停止。

## 验证范围

独立服务器测试项目通过构建依赖准备 Host，再用真实子进程验证显式角色选择、拒绝 game/错误角色、Lobby 绑定真实端口、ready=false 和 stdin 停止。另以真实 TLS 验证 Lobby→Auth 先启动、Worker 尚未就绪时 Auth 可以 Live，以及直接 BeginDrain 停止监听。生命周期测试验证逆序/幂等关闭、共享期限、部分启动清理与临界故障状态。

本轮不代表六进程游戏业务、跨机容灾、容量、Unity 或公网验收。S1 整体完成状态以[实施记录](server-implementation.md)及最终逐项验收为准。
