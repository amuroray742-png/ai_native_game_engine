# NetworkDemo

独立的 Unity 6000.3.23f1 工程，演示 UnityCore 启停期间的 HTTP 请求与 TCP 消息连接。工程通过相对路径引用仓库内的 UnityCore 和 Network 源码包。交互界面使用 Unity 内置 IMGUI。

## 启动

在仓库根目录启动回环服务，并保持此终端运行。

```powershell
python scripts/network_fixture.py --http 18080 --tcp 18081
```

通过 Unity Hub 打开 `examples/unity/NetworkDemo`，或在另一个终端运行 `unity open examples/unity/NetworkDemo`。打开 [NetworkDemo 场景](Assets/Network/NetworkDemo.unity)，进入 Play，在 Game 窗口操作。

1. 点击 **GET /config**，查看 HTTP 状态码和响应正文。
2. 点击 **Connect**，查看服务端主动推送的 `welcome`。
3. 输入消息并点击 **Send**，在 **Received** 查看回显。
4. 发送 `/drop`，观察对端关闭。再次点击 **Connect** 建立新连接，再点击 **Disconnect** 主动关闭。

省略服务脚本的端口参数时，由操作系统分配端口并以 JSON 输出。可将实际端口填入面板。服务监听 `127.0.0.1`。

## 自动验收

在 Windows 仓库根目录运行以下命令，指定一个尚不存在的输出目录。需要 Unity CLI、Windows IL2CPP Build Support 和 Visual C++ 工具链。

```powershell
python scripts/verify_unity_network.py TestResults/network-demo-local
```

脚本复制 NetworkDemo 和两个本地源码包，启动独立回环服务，执行两种 Domain Reload 设置下的 PlayMode 与退出/重入 Play 测试，再构建和运行 Windows x64 IL2CPP Player。测试 XML、命令、源码指纹、Player 日志和结果汇总写入指定目录。[网络包接口](../../../packages/com.ainative.unitycore.network/README.md)
