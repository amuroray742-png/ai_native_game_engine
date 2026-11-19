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

`SendAsync(HttpRequest)` 支持 HTTP/HTTPS、请求头和二进制正文。返回状态码、响应头和独立正文数组，4xx/5xx 也返回完整响应。默认超时 30 秒，使用真实经过时间，在主线程首次观察到超时时中止请求。主动取消或模块关闭产生 `OperationCanceledException`，超时产生 `TimeoutException`，连接或接收失败产生 `IOException`。协议和状态码的业务含义由调用方处理。请求使用 Unity 的证书校验和项目 HTTP 策略。

## 长连接

TCP 帧由四字节大端无符号长度和对应字节数的消息体组成，长度不含帧头。空消息合法。构造模块的 `maxMessageBytes` 默认 1 MiB，发送时检查大小，接收时在分配正文前检查。接收端保留一条在读消息，对下一条的读取跟随主线程消费进度。每次 Update 每个方向最多交付 16 条。收到长度超限或不完整的帧，以 `ProtocolError` 结束连接。

每个连接最多接受 16 条尚未完成的发送（包含正在写入的一条）。接单时复制调用方数组，队满立即抛出 `InvalidOperationException`。业务可等待先前的发送再提交。发送完成表示本地流已写入；连接中断时可能无法判断服务端是否收到。需要确认、去重或重试的操作由业务协议定义。收到的数组归本次通知的消费者使用，同一事件的多个订阅者共享该数组。

连接成功后立即订阅消息与断开事件。主动关闭、对端帧间 EOF、I/O 故障、协议故障以及模块关闭分别通过 `DisconnectReason` 区分。每个连接只发布一次断开，随后解除订阅。断开后的发送立即失败；正常关闭等待在途 Socket I/O 收尾，Core 关闭会中止显式关闭的等待并接管收尾。退出 Play 时同步释放套接字和 HTTP 资源，迟到的底层任务只负责结束自身。

业务对象可接收 `IMessageClient` 构造参数，通过 `IMessageConnection` 使用整条消息。未来 KCP 接入时可提供新的接口实现，自行管理 UDP、会话和协议时钟。TCP 帧头只在 TCP 实现与其服务端之间传输。当前包可用于访问应用服务；游戏命令校验、模拟 Tick 和回放继续由游戏宿主管理。

## 本机示例与验收

1. 在仓库根目录运行 `python scripts/network_fixture.py --http 18080 --tcp 18081`。省略端口时由操作系统分配并以 JSON 打印。
2. 打开 UIDemo 的 `Assets/Network/NetworkDemo.unity`，直接从此场景进入 Play。GET 获取配置，Connect 接收服务端的 welcome，Send 回显输入，发送 `/drop` 可观察对端断开并重新连接。此场景是独立的应用启动入口。
3. 在 Windows 运行 `python scripts/verify_unity_network.py TestResults/network-<新名称>`。脚本在独立目录复制工程，启动单独的回环服务，运行双 Domain Reload 的 Editor 测试、实际退出/重入 Play 测试，再构建并启动 Windows x64 IL2CPP Player。端口、XML、Player 日志与结果汇总写入指定目录。
