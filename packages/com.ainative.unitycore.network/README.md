# UnityCore Network

Unity 应用层的 HTTP 请求和可靠有序消息连接。包使用 UnityCore、UniTask 2.5.11 和 UnityWebRequest 内置模块。目标环境为 Unity 6000.3、Windows Editor 和 Windows x64 IL2CPP。

## 装配和使用

在应用启动入口注册一次模块，Core 进入 Running 后发起 I/O。模块间依赖在装配或 `AfterInitialize` 阶段按接口绑定。调用、取消和事件均在 Unity 主线程。每次重连都创建新的连接句柄。

```csharp
var network = new NetworkModule();
core.RegisterModule(network);
await core.RunAsync(token);
var http = core.GetModule<IHttpService>();
var messages = core.GetModule<IMessageClient>();

var response = await http.SendAsync(new HttpRequest("GET", configurationUri), token);
var connection = await messages.ConnectAsync(host, port, token);
connection.MessageReceived += HandleMessage;
connection.Disconnected += HandleDisconnect;
await connection.SendAsync(payload);
await connection.CloseAsync();
```

默认的 `new NetworkModule()` 同时注册两个接口。需要替换消息传输时，注册 `NetworkModule.HttpOnly()`，再由另一个模块注册 `IMessageClient`。需要替换 HTTP 时，注册 `NetworkModule.TcpOnly()` 并提供自己的 `IHttpService`。包外实现可用 `new HttpResponse(statusCode, headers, body)` 创建响应，传入的响应头和正文直接交给调用方。

`SendAsync(HttpRequest)` 支持 HTTP/HTTPS、请求头和二进制正文。返回状态码、响应头和独立正文数组，4xx/5xx 也返回完整响应。默认超时 30 秒，使用真实经过时间，在主线程首次观察到超时时中止请求。主动取消或模块关闭产生 `OperationCanceledException`，超时产生 `TimeoutException`，连接或接收失败产生 `IOException`。协议和状态码的业务含义由调用方处理。请求使用 Unity 的证书校验和项目 HTTP 策略。

## 长连接

每条连接拥有一个 `Socket` 和两条专用后台线程。发送线程阻塞等待队列，`SendAsync` 复制消息并立即唤醒它，接收回调内的回复也无需等待下一帧。接收线程可连续读取多条完整消息，主线程按到达顺序批量取走。主线程在 Update 交付消息和发送完成通知。关闭会打断套接字读写并唤醒队列等待者，正常关闭异步等待两条线程退出。

TCP 帧由四字节大端无符号长度和对应字节数的消息体组成，长度不含帧头。空消息合法。构造模块的 `maxMessageBytes` 默认 8 MiB，发送时检查大小，接收时在分配正文前检查。每连接最多预读 16 条消息，排队与在读的正文合计最多为 `maxMessageBytes`。条数满时停在下一帧头之前；字节空间不足时只读帧头，等主线程腾出空间才分配并读取正文。连接正常收发时，每次 Update 最多交付 16 条接收消息和 16 个发送结果。进入关闭流程时，会在断开通知后一次结算所有剩余发送任务。收到长度超限或不完整的帧，以 `ProtocolError` 结束连接。

发送队列按 FIFO 接单。原三参数构造和 `TcpOnly(maxMessageBytes, maxPendingSends, maxPendingSendBytes)` 的后两个参数现为正数告警水位，可分别调整。默认水位为 256 个待通知任务和 4 MiB 待写编码字节（含在写帧及每帧 4 字节头）。主线程在 Update 派发前采样。任一指标持续超出水位 1 秒时记录 Warning，以后每 10 秒最多提醒一次，两项都回落后重新计时。日志包含对端、待写帧数及字节、已写完待通知数和持续时长，帧数与字节数为并发采样值。积压期间继续接单，对端长期停读时内存可能持续增加。

主线程在 `SendAsync` 返回前复制调用方数组，可立即重用原数组。发送线程完整写入本地流后归还缓冲。关闭时已完整写入的任务成功，未写完的任务在断开通知后失败。需要业务确认、去重或重试时由应用协议处理。

每条连接按需回收最多 128 个容量为 256 字节的发送数组（数组正文最多约 32 KiB）。编码长度不超过 256 字节的发送可取用，未命中时按实际长度向原数组池租用。持续发送极小包时，原池返回的较小数组仍归还原池。正常写入由发送线程归还，关闭时待写帧直接交还原池，发送线程退出后清空连接缓存。

发送线程立即取走队首，单帧直接写。队列中已有连续小帧时，每次最多合并 32 个编码帧、共 8 KiB；遇到较大帧后先写当前批次，再按序写大帧。每次套接字写入返回后，按实际跨过的完整帧边界更新进度。一次短写后断线时，已经写完整的前缀仍成功。

接收事件改用 `MessageReceivedHandler(ReadOnlySpan<byte> payload)`。现有 `HandleMessage(byte[])` 方法和自定义传输的事件声明需要同步迁移并重新编译。监听者可在回调内直接解析有效正文；跨帧缓存、异步处理或交给其他线程时，在回调内先调用 `payload.ToArray()`。同一帧的全部同步监听者返回后，连接归还接收缓冲。

连接成功后立即订阅消息与断开事件。主动关闭、对端帧间 EOF、I/O 故障、协议故障以及模块关闭分别通过 `DisconnectReason` 区分。每个连接只发布一次断开，随后解除订阅。断开后的发送立即失败；帧间 EOF 到达后，先交付所有已接收的消息。最后一批回调返回且主线程观察到 EOF 后停止接单，给在途回复最多 2 秒，在首次观察到超时的 Update 中关闭。主动关闭和 Core 关闭立即打断这段等待。正常关闭等待在途 Socket I/O 收尾，Core 关闭会中止显式关闭的等待并接管收尾。退出 Play 时同步释放套接字和 HTTP 资源，迟到的底层任务只负责结束自身。

业务对象可接收 `IMessageClient` 构造参数，通过 `IMessageConnection` 使用整条消息。未来 KCP 接入时可提供新的接口实现，自行管理 UDP、会话和协议时钟。TCP 帧头只在 TCP 实现与其服务端之间传输。当前包可用于访问应用服务；游戏命令校验、模拟 Tick 和回放继续由游戏宿主管理。

## 本机示例与验收

1. 在仓库根目录运行 `python scripts/network_fixture.py --http 18080 --tcp 18081`。省略端口时由操作系统分配并以 JSON 打印。
2. 打开 UIDemo 的 `Assets/Network/NetworkDemo.unity`，直接从此场景进入 Play。GET 获取配置，Connect 接收服务端的 welcome，Send 回显输入，发送 `/drop` 可观察对端断开并重新连接。此场景是独立的应用启动入口。
3. 在 Windows 运行 `python scripts/verify_unity_network.py TestResults/network-<新名称>`。脚本在独立目录复制工程，启动单独的回环服务，运行双 Domain Reload 的 Editor 测试、实际退出/重入 Play 测试，再构建并启动 Windows x64 IL2CPP Player。端口、XML、Player 日志与结果汇总写入指定目录。
