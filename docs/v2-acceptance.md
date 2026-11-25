# 通用框架 v2 验收

## TCP 小缓冲回收、批量写与接收队列对照（2026-11-25）

Windows Unity 6000.3.23f1 在 Domain Reload 开、关两种设置下各通过 28 项 PlayMode 和 1 项 EditMode，共 58 次，0 失败、0 跳过。覆盖 252/253 字节正文边界、空帧、32/33 帧边界、8 MiB 回显、短写停止在帧内或边界、主动关闭、半关闭、重启及缓冲归还。Windows x64 IL2CPP Player 通过 HTTP 配置获取、TCP 推送与回显、断线重连和主动关闭，进程记录 `NETWORK_SMOKE_PASSED IL2CPP`，退出码 0。首轮有一项旧测试假定 8 MiB 帧在关闭前一定写不完，Windows 回环上该帧已完成。改为在已写完的帧归还缓冲时控制发送线程停点，独立审查后完整重跑。[功能验收及源码指纹](evidence/unitycore-network/20261125-send-batch/summary.json)

**发送对照。** 在同一 Windows Editor 进程交错执行四种配置，表中为预热后 12 轮中位数。正文 128 字节，发送端启用 TCP_NODELAY，每轮 192 批、每批 64 条，吞吐测到对端读完和本地发送完成。空闲首包指约 1 ms 的空闲间隔后，从提交到对端读完整帧。分配仅统计提交线程的 Unity Profiler GC.Alloc 事件。

| 配置 | 提交 μs/条 | 单向吞吐 条/s | Socket.Send 次/64 条 | GC 分配次/64 条 | 空闲首包 P95 μs |
| --- | ---: | ---: | ---: | ---: | ---: |
| 修改前 | 0.474 | 87,582 | 64.00 | 64 | 111.0 |
| 仅小缓存 | 0.321 | 91,362 | 64.00 | 64 | 100.5 |
| 仅批量写 | 0.549 | 836,948 | 2.65 | 64 | 108.1 |
| 小缓存与批量写 | 0.324 | 1,057,644 | 2.89 | 64 | 104.0 |

**接收队列。** 固定两项发送优化与套接字读取方式，A 使用原有有界队列和 Monitor，B 使用 ConcurrentQueue、独立额度计数和空间等待。两个独立 Editor 进程分别交错执行，三种场景各预热后测 12 轮。下表为 B 相对 A 的中位数变化；每格按进程一、进程二排列。派发耗时统计有消息交付的整次 Pump 墙钟时间并按消息条数均摊；P95 从对端提交消息组计至接收回调。

| 场景 | 派发耗时变化 | 吞吐变化 | P95 延迟变化 |
| --- | ---: | ---: | ---: |
| 连续推送 | −35.36% / −34.81% | +26.76% / +26.27% | −24.55% / −19.74% |
| 主线程落后 | −23.34% / −25.97% | +0.20% / −1.05% | −0.90% / +0.52% |
| 正常逐帧 Update | −2.44% / −5.44% | −1.22% / +48.07% | +15.77% / −58.63% |

**生产接收队列保留 A。** 正常 Update 在首个进程中未达到 5% 改善，且 P95 从 699.70 增至 810.05 μs，超过 5% 回退上限。第二个进程正常 Update 的 P95 有改善，未改变双进程门槛的结论。以上性能数据限于 Windows Editor 回环，Player 执行的是功能验收。[逐轮指标和指纹](evidence/unitycore-network/20261125-send-batch/benchmark.json) · [测试负载](evidence/unitycore-network/20261125-send-batch/benchmark-workloads.cs.txt) · [接收 B 版差异](evidence/unitycore-network/20261125-send-batch/receive-candidate.patch)

## TCP 积压告警与缓冲池（2026-11-25）

完整 UIDemo 在 Windows Unity 6000.3.23f1、Domain Reload 开和关的设置下各通过 25 项 PlayMode 和 1 项 EditMode，共 52 次，0 失败、0 跳过。回归涵盖 1,024 帧突发顺序、8 MiB 回显、超长拒绝、停读与仅通知积压、告警计时与恢复、关闭与重入，以及截断、订阅者异常、回调内关闭和 Core 重启后的缓冲归还。Windows x64 IL2CPP Player 完成 HTTP 配置、TCP 推送与回显、重连和主动关闭，输出 `NETWORK_SMOKE_PASSED IL2CPP`，退出码 0。[用例及指纹](evidence/unitycore-network/20261125-pooled-tcp/summary.json)

同机 Editor 回环比较改动前后的实现。两版按正序、逆序各跑一次，每个进程预热后取 3 轮，每轮 96 批、每批 64 条、正文 128 字节。下表耗时和吞吐为各版本 6 轮的中位数；发送吞吐以对端读完整批数据为准，回显吞吐包括主线程派发。池预热后以 Unity Profiler 的 `GC.Alloc` 事件计数，分配正对照和独立完成对象对照每 128 次操作均产生 128 个事件。[各轮指标和源码指纹](evidence/unitycore-network/20261125-pooled-tcp/benchmark.json) · [同一套对照用例](evidence/unitycore-network/20261125-pooled-tcp/benchmark-methods.cs.txt)

| 指标 | 修改前 | 修改后 |
| --- | ---: | ---: |
| 主线程提交，微秒/条 | 0.385 | 0.454 |
| 单向发送，条/秒 | 85,922 | 93,710 |
| 回显及派发，条/秒 | 90,096 | 80,936 |
| 128 次编码 / 128 次正文读取的分配事件 | 128 / 128 | 0 / 0 |
| 128 次完整发送提交的分配事件 | 256 | 128 |

本次单向发送吞吐提升约 9%，主线程提交耗时增加约 18%，完整回显吞吐下降约 10%。这是 Windows Editor 回环微基准，Player 执行了功能验收。对照用例可插入两版隔离 UIDemo 的 `NetworkTests` 类，使用 Unity CLI 的 PlayMode 过滤器分别运行 `LoopbackBench` 和 `FrameBufferAllocations`。

## TCP 发送预算（2026-11-25）

Windows Editor 6000.3.23f1 在 Domain Reload 开、关两种设置下，各通过 24 项 PlayMode 和 1 项 EditMode，共 50 次，0 失败、0 跳过。新增回归验证单次突发 256 条消息的有序写入和第 257 条的背压、按编码帧大小精确计费、写完后在下一次 Update 前复用额度，以及对端停读时在写帧持续占用预算。Windows x64 IL2CPP Player 完成 HTTP 配置获取、TCP 推送和回显、断线重连与主动关闭。Player 记录 `NETWORK_SMOKE_PASSED IL2CPP` 并以 0 退出。[逐项结果及源码、Player 指纹](evidence/unitycore-network/20261125-send-budget/summary.json)

## TCP 批量接收与半关闭（2026-11-25）

完整 UIDemo 在 Windows Editor 6000.3.23f1 的 Domain Reload 开、关两种设置下，各通过 21 项 PlayMode 和 1 项 EditMode，共 44 次执行，0 失败、0 跳过。覆盖 64 条突发消息的顺序、按帧派发与条数背压、分配前的字节预算、已写完发送在主动关闭或 Core 重启时的结算，以及半关闭时的完整回复、停读超时和重启接管。真实退出并重入 Play 的用例也通过日志检查。Windows x64 IL2CPP Player 完成 HTTP 配置、TCP 推送与回显、断线重连和主动关闭，记录 `NETWORK_SMOKE_PASSED IL2CPP`，退出码 0。

首次完整工程启动超时。中间专项工程的 HTTP 响应用例也曾超时，改动前源码单独执行可复现。最终在核对源码指纹并复用隔离工程的 Library 后，完整专项通过。独立 .NET 8 回环测量在目标 60 FPS 下突发 64 条 64 字节正文，修改前 3 轮需 55–64 次 Update，修改后两组各 3 轮分别需 7–9 次和 4 次。[各轮结果、源码及 Player 指纹](evidence/unitycore-network/20261125/summary.json)

## TCP 专用收发线程 Windows IL2CPP（2026-11-22）

安装 Unity 6000.3.23f1 Windows Build Support (IL2CPP) 后，使用网络专项脚本在隔离工程重新运行双 Domain Reload，各通过 18 项 PlayMode 和 1 项 EditMode，共 38 次，0 失败、0 跳过。Windows x64 IL2CPP Player 构建成功，在独立回环服务上完成 HTTP 配置读取、TCP 推送、回显、对端断线、重连与主动关闭。进程记录 `NETWORK_SMOKE_PASSED IL2CPP`，退出码 0。[测试用例、运行结果、可执行文件与源码指纹](evidence/unitycore-network/20261122/summary.json)

## TCP 专用收发线程（2026-11-21）

Windows Editor 6000.3.23f1 在 Domain Reload 开、关各通过 18 项 PlayMode 和 1 项 EditMode，共 38 次，0 失败、0 跳过。回环测试验证每连接两条独立后台线程、关闭及重启后线程退出、单帧接收背压、半关闭时回调内回复、首个终止原因，以及 1 MiB 消息回显。另以可控短写验证分段发送的偏移与内容。首轮发现的 localhost 混合地址连接失败已修复，最终复测通过。Windows Player 构建因缺少此版本的 Windows IL2CPP 后端失败。[用例、XML 摘要与源码指纹](evidence/unitycore-network/20261121/socket-threads.json)

## UnityCore Network 审查回归（2026-11-21）

Windows Editor 6000.3.23f1 在 Domain Reload 开、关各通过 14 项 PlayMode 和 1 项 EditMode，共 30 次，0 失败、0 跳过。新增四项覆盖包外 HTTP 响应、HTTP/TCP 独立注册、接收回调当帧写出及主线程完成通知、稳定订阅下的消息分发。最后一项在预热后单独调用分发方法 256 次，记录到 0 次 GC.Alloc。Windows Player 构建因本机缺少该版本的 Windows IL2CPP 后端而失败。[用例、XML 摘要和源码指纹](evidence/unitycore-network/20261121/summary.json)

## UnityCore Network（2026-11-19）

Windows Editor 6000.3.23f1 在 Domain Reload 开、关两种设置下，各通过 10 项 PlayMode 和 1 项 EditMode 测试，共 22 次执行，0 失败、0 跳过。每项 EditMode 用例实际进入和退出 Play 两次，核对在途 HTTP 取消和 TCP 断开。PlayMode 覆盖错误响应、取消与超时、拆合帧、写入中断、显式重连，以及断开回调中重启 Core 的通知顺序。

Windows x64 IL2CPP Player 使用独立回环服务，完成 HTTP 获取配置、TCP 主动推送、二进制消息回显、对端断开、显式重连和主动关闭，进程退出码为 0。示例场景以无图形模式自动运行。另通过 12 项验收脚本与源码包复制测试，并完成网络包的独立代码审查。KCP、实际公网服务、人工界面操作和远端 CI 留待单独验收。[结果与用例](evidence/unitycore-network/20261119/summary.json) · [Player 日志](evidence/unitycore-network/20261119/player.log) · [接口与复现命令](../packages/com.ainative.unitycore.network/README.md)

## Unity 示例框架迁移（2026-11-18）

两个游戏继续由 v2 PlayHost 管理每局模拟。GridGame 从场景资源作用域取得棋盘和棋子精灵，PhysicsSandbox 从 Bundle 取得 HUD 和无碰撞体的显示预制体。AssetsDemo 的两个交互面板均通过 UnityCore UI 打开。UIDemo 沿用原有装配。

| Windows Editor 6000.3.23f1 | 本轮验证 |
| --- | --- |
| GridGame | 两种 Domain Reload 各通过 13 项 PlayMode，重入测试通过。快慢表现的四组回放各有 16 个检查点与 CLI 一致。覆盖棋盘资源缺失后重连、重复宿主注册、真实 Bundle 释放。[结果](evidence/unity-examples-20261118/grid-editor.json) · [Tick 2 画面](evidence/unity-examples-20261118/grid-tick-2.png) |
| PhysicsSandbox | 两种 Domain Reload 各通过 28 项 PlayMode、26 项 EditMode。10 组 CLI 轨迹比较通过，两个负例定位到预期分歧。[结果](evidence/phase-3/physics-play/framework-20261118/summary.json) · [读档首帧](evidence/phase-3/physics-play/framework-20261118/restored-90.png) |
| AssetsDemo 主场景 | 两种 Domain Reload 各通过 2 项真实场景测试，覆盖 Core 所有权、加载中停用、按钮恢复和清理后的结果显示。 |
| Shader Variant 场景 | 两种 Domain Reload 各通过 3 项测试；三次独立 Windows IL2CPP 构建分别验证绿色变体保留、剥离和恢复。剥离时捕获两条预期的缺失变体日志，画面像素和关闭后清理均通过。[结果](evidence/unity-examples-20261118/shader.json) · [剥离画面](evidence/unity-examples-20261118/sbp-stripped.missing.png) · [恢复画面](evidence/unity-examples-20261118/sbp-restored.green.png) |
| UIDemo | 两种 Domain Reload 各通过 36 项 UI EditMode、113 项 UI PlayMode、15 项真实 Bundle Demo 测试，共 328 次。[结果](evidence/unity-examples-20261118/ui-editor.json) |

Windows x64 IL2CPP 实测中，GridGame 完成移动、攻击、结束回合和重开，断开后 Bundle 数为零。PhysicsSandbox 从存档恢复到 Tick 1 的四个物体，重开及退出后原生世界与表现资源均已释放。AssetsDemo 主场景验证了清理按钮、共享资源、对象池、场景、Core 重启和销毁取消；两次注入故障分别以预期错误退出。[Player 结果](evidence/unity-examples-20261118/players.json) · [GridGame 截图](evidence/unity-examples-20261118/grid-player.png) · [PhysicsSandbox 截图](evidence/unity-examples-20261118/physics-player.png)

复现步骤见 [GridGame](../examples/unity/GridGame/README.md)、[PhysicsSandbox](../examples/unity/PhysicsSandbox/README.md)、[AssetsDemo](../examples/unity/AssetsDemo/README.md) 和 [UIDemo](../examples/unity/UIDemo/README.md)。本轮使用自动输入与按钮事件，未新增人工试玩或远端 CI 记录。

## 历史验收

2026-11-17 GridGame接入UnityCore UI，Windows Editor双重载各11项PlayMode及1项重入测试通过，四组快慢表现各16检查点与CLI一致；范围与证据见[GridGame UI接入](unity-play-host.md#gridgame-ui-模块接入2026-11-17)，不扩展为GridGame Player验收。

UnityCore Assets 的独立资源包、异步关闭扩展与 Windows 资源验收记录见 [Assets 记录](unitycore-assets.md)。它不改变下述模拟内核和 v2 协议历史基线。

2026-11-13 UnityCore UI 基础生命周期修复与 Windows Editor 双 Domain Reload 验证见 [UI 记录](unitycore-ui.md#基础生命周期收敛2026-11-13)：两种设置各 20 项 PlayMode、19 项 EditMode，共 78 次执行通过。仅覆盖基础窗口关闭、重启、取消及强退解绑，不代表导航、缓存、Bundle、真实输入或 Player 已完成，也不改写下述历史基线。

同日后续 [UI Task 3 验收](unitycore-ui.md#task-3-页面栈模态与事务回滚2026-11-13)完成无动画页面栈、模态、事务回滚及基础输入/焦点；双 Domain Reload 各 43 项 PlayMode、19 项 EditMode，共 124 次执行通过并经独立复核。缓存、动画、Clear、Bundle、真实输入后端及 Player 仍未验收，前一轮 78 次记录保留为生命周期历史基线。

同日后续 [UI Task 4 验收](unitycore-ui.md#task-4-有界缓存lrutrim-与旧会话隔离2026-11-13)完成有界缓存、LRU、Trim 与旧会话句柄隔离，经独立代码审查后双 Domain Reload 各通过 60 项 PlayMode、19 项 EditMode，共 158 次执行，0 失败、0 跳过；包含新增 17 项缓存测试和原导航/生命周期回归。前两轮记录保留为历史基线；Clear、动画、安全区、完整迟到/外部销毁、Bundle、真实输入和 Player 仍未完成。

同日后续 [UI Task 5 验收](unitycore-ui.md#task-5-输入焦点安全区和内置过渡2026-11-13)完成输入路由、焦点、安全区及不受 timeScale 影响的内置过渡，经独立代码审查后双 Domain Reload 各通过 81 项 PlayMode、25 项 EditMode，共 212 次执行，0 失败、0 跳过。新增 21 项 PlayMode 与 6 项 EditMode；输入证据为 RaycastAll/ExecuteEvents，设备安全区、真实鼠标键盘、交互 Demo、Bundle、Player 与 CI 仍不在已验范围。前述记录保留为各阶段历史基线。

同日后续 [UI Task 6 验收](unitycore-ui.md#task-6-clear迟到任务外部销毁与诊断2026-11-13)完成 Clear、迟到任务隔离、外部销毁恢复与不可变诊断，经独立代码审查后双 Domain Reload 各通过 104 项 PlayMode、25 项 EditMode，共 258 次 UI 执行；另有 Core PlayMode 16 项、EditMode 24 项及 Assets 取消 12 项，共计 310 次执行，0 失败、0 跳过。迟到加载采用受控后端配合真实 Core/Assets/UI，组件诊断只记录资源身份、不推断最终渲染材质或批次。Task 7–8、真实 Bundle UI 集成、交互 Demo、设备、Player 与 CI 仍未验收。

同日后续 [UI Task 7 验收](unitycore-ui.md#task-7-独立-uidemo-与真实-bundle-集成2026-11-13)完成独立 UIDemo、真实 SBP Bundle 构建部署及 11 项集成测试；双 Domain Reload 各 UI PlayMode 104 项、EditMode 25 项、Demo 11 项，共 280 次执行通过，0 失败、0 跳过。可见 Windows Editor 另用操作系统注入的原生鼠标/键盘事件验证导航、模态返回、Clear 与 Single 切场景，并保存稳定页、双模态及双页过渡的 Game View screen 截图。Task 8、性能、设备、Player/IL2CPP 与远端 CI 仍未验收。

同日后续 [UI Task 8 验收](unitycore-ui.md#task-8-层级顺序隔离验证与交付2026-11-13)收敛 HUD/Overlay 成功提交顺序，新增统一隔离验证脚本；最终双 Domain Reload 各 UI PlayMode 108、EditMode 25、Demo 11，共 288 次执行通过，另有 10 项门禁单测与 2 项包复制单测通过。可见 Editor 独立复核稳定/双模态/过渡截图；自动矩阵不冒充视觉验收。性能、设备、Player/IL2CPP 和远端 CI 仍未验收。

同日后续 [UI 稳定路径分配与诊断采样](unitycore-ui.md#稳定路径分配与诊断采样2026-11-13)减少销毁扫描和安全区更新的空闲分配，将 Demo 完整诊断改为0.25秒采样并补充公共 API 文档。双重载各109 UI PlayMode、25 EditMode、12 Bundle Demo，共292次通过，另有10项Python门禁单测通过。受控方法的GC.Alloc检查不代表整帧零分配或完整性能验收；未重新截图，Task 8证据保持历史范围。

本记录只声明实际执行的检查。当前实现、协议与复现边界见[v2契约](framework-v2.md)。旧355测试/45次CLI的逐项行为映射见[迁移索引](v2-migration-index.md)。

2026-11-16 [UI Windows Player 与 IL2CPP专项](unitycore-ui.md#windows-player-与-il2cpp2026-11-16)完成两后端Release构建及运行，各十组真实Bundle/uGUI事件/缓存/Clear/显式退出检查通过，并检查4张Player屏幕截图；双Domain Reload回归296次通过，Python门禁12项通过。限定Windows x64、Direct3D11、Minimal stripping和合成事件路由，不声明原生输入/强退/移动端/远端CI通过，也不扩展两个模拟示例的历史验收范围。

同日后续 [UI原生输入与系统关闭](unitycore-ui.md#windows-player-原生输入与系统窗口关闭2026-11-16)补齐两后端用户鼠标、CUA原生Return/Right与Alt+F4退出：各十个有序状态通过，退出0，销毁回调资源归零。重新构建各十组自动smoke及13项Python门禁通过；未重跑上一轮296次Editor矩阵。抓图与AX关闭按钮工具失败如实保留，未声明本轮原生截图或标题栏按钮点击通过。

同日最新 [UI原生截图与标题栏×](unitycore-ui.md#原生交互截图与标题栏关闭2026-11-16)以帧末ScreenCapture记录真实原生会话客户端画面，Mono/IL2CPP各10交互及4视觉检查点通过，共34张PNG完整解码、8张代表图检查；用户按明确指令点击标题栏×并确认，两进程退出0且资源归零。14项Python与两后端各10组自动smoke通过。截图不含OS标题栏，退出方式另由用户确认与生命周期日志佐证；OS抓图/自动点击工具仍未修复，不冒称Editor矩阵或其他平台重验。

同日后续[UI加载与退出动画中关闭](unitycore-ui.md#加载与退出动画中关闭2026-11-16)完成Mono/IL2CPP共四场挂起状态下点击标题栏×，均退出0且资源/所有权归零；修复宿主将纯取消误报失败及Assets退休迟到对象误报类型错误。有效UI矩阵298、正确AssetsDemo宿主Core/Assets矩阵336、Python18通过；中断恢复、错误宿主与超时失败均保留。最后IL2CPP改用手动Back夹具，产品源码未再改变；加载仅证明受控结果交付延迟边界，不扩展物理IO、强杀或其他平台承诺。

2026-11-09 的宿主故障分类、编码失败终止、恢复 runtime 核对、退出日志和 Int32 负零修复，独立审查及本机271项 .NET、108次CLI和两个Unity宿主验收见[修复记录](review-fixes-20261109.md)。历史基线仍按下文保留。

后续 joltc 与多线程迁移的版本、契约和新增验收单列在[joltc迁移记录](joltc-migration.md)；本页222项测试/80次CLI记录保留为迁移前v2基线。

后续 C#/原生分层迁移的契约和实际检查见[物理分层迁移](managed-physics-migration.md)。

2026-11-09 的 UnityCore 有边界单例例外、生命周期与调度修复及 Windows Editor 验收单列在[UnityCore 记录](unitycore-lifecycle.md)。两种 Domain Reload 各通过 23 项 PlayMode、22 项 EditMode，并重新完成十组 PhysicsSandbox CLI 检查点比较；不改写下述历史基线，也不扩展模拟内核与 v2 文件协议的边界。

## 第三阶段 Windows Editor 验收

| 示例 | 本地验收范围 | 可复核证据 |
| --- | --- | --- |
| GridGame | 快慢表现与两种 Domain Reload 设置共四组，每组匹配 16 个检查点 | [Play 宿主记录](unity-play-host.md) · [机器摘要](evidence/phase-3/grid-play/summary.json) |
| PhysicsSandbox | 两种 Domain Reload 设置各通过 11 项 PlayMode 测试和 1 项退出 Play 测试。每种设置比较均匀推进、补步、暂停输入以及 Tick 90、91 的读档续跑，共十组匹配 | [实施与验收](phase-3-physics-sandbox-unity-plan.md) · [机器摘要](evidence/phase-3/physics-play/summary.json) · [原始制品](evidence/phase-3/physics-play/physics-play-evidence.zip) |

PhysicsSandbox 同轮本地无头回归通过 249 项 .NET 测试、108 次 CLI 进程验收及原生损坏探针。两份 Editor 记录来自本机 Windows x64。远端工作流运行无头检查，Player、IL2CPP 与人工试玩仍待单独验证。旧 GridGame 窗口的验证范围见[历史验收](phase-3-grid-editor.md)。

## 迁移前 v2 基线的验收入口与范围

| 范围 | 可执行入口/证据 |
| --- | --- |
| 模块边界 | HostingTests.FrameworkAssembliesDoNotDependOnExamplesOrUnity；独立Core/Hosting/Physics项目构建；grid-without-native进程 |
| 输入、Tick、随机与故障 | CoreTests；GridGame原GameplayTests/GridGameStateTests/ScenarioTests；fixed逐Tick独立状态向量 |
| 物理连续性 | PhysicsTests.CompleteStateRestoresFutureBodiesContactsAndAllocator；自由下落、碰撞、休眠、增删、冲量；physics-*跨进程续跑 |
| 生命周期与损坏状态 | PhysicsTests.WorldsAreIsolatedAndDisposalReturnsResourcesToBaseline/FailedCandidateAndInvalidNativeArgumentsLeaveOldWorldUsable；native-probe逐字节截断与变异 |
| 原生身份与保存边界 | wrong-native-version；ManyChangesInOneTickCannotReuseAnUndeliveredContactIdentity；Remove后保存拒绝 |
| 协议与表现 | HostingTests、CliTests、PresentationTests；输入/环境/摘要损坏、分歧定位、顺序/实时消费、旧回调失效 |
| 证据链 | scripts/verify_v2.py，80次真实CLI；CI Windows/Linux分别构建原生库、运行测试与脚本，另记录平台间compare |

本地复现命令见[README](../README.md#验证)。输出目录须不存在；process-index.json逐项保存实际命令/退出码/code，inputs、snapshot、replay、stdout/stderr、environment.json与native-probe结果同目录。TRX单列；原生构建材料含源码pin、编译器、flags、补丁hash、compile_commands与实际binary hash。

## 迁移前 v2 基线的运行记录

2026-11-03 Windows x64 初次完整验证：213项单元测试、80次CLI、1581个截断位置和1582个单字节变异通过；TestResults/v2-windows-final、TestResults/v2-unit。随后按独立审查修复装配/诊断和补充迁移回归；2026-11-04最终本地单元222项通过（TestResults/v2-unit-final），80次CLI与原生probe重跑通过（TestResults/v2-windows-reviewed）。独立Reviewer已复核这些结果，未发现剩余代码阻断项。

最终代码提交 `f9fba2c68c8daed949a1b53f06d229d1b9a181bb` 的[双平台 CI 41689668666](https://github.com/rayss1/ai_native_game_engine/actions/runs/41689668666)全部通过。两个平台均从固定源码构建真实原生库，执行锁定依赖恢复、构建、单元测试、只读格式检查与真实进程验收；并已下载上传制品核验，不仅检查作业状态。

| 环境 | 单元测试 | CLI进程 | 原生独立进程探针 | 制品ID |
| --- | --- | --- | --- | --- |
| Windows x64 / .NET 10.0.14 | 222通过，无失败/跳过 | 80通过 | 1581截断 + 1582单字节变异，worlds=0 | 13257786363 |
| Linux x64 / .NET 10.0.14 | 222通过，无失败/跳过 | 80通过 | 1581截断 + 1582单字节变异，worlds=0 | 13256532273 |

下载副本：`TestResults/v2-ci-41689668666/`，核验摘要 `verified-summary.json`。逐项核对TRX、80条命令实际/预期退出码、stdout code、原生探针及native-build.json中的binaryHash与实际文件一致。

独立平台比较制品 `13255588185` 中 GridGame 与 PhysicsSandbox 均为 `HostTracesMatched`。这是该次Windows/Linux构建的同起点同输入证据比较结果；严格恢复门禁仍要求同构建同环境，不扩大兼容承诺。

远端制品保留策略见workflow（平台制品7天），过期后按README与固定提交重新生成。Unity编辑器/Player/IL2CPP、资源/UI/音频框架未纳入本轮。

## 独立审查与修复

独立Reviewer进行了Core、物理、共享Hosting、Headless、表现与脚本审查，真实进程复现并复核下列修复：

1. 截断物理状态恢复期间持锁异常导致候选清理死锁：结构预读+固定Jolt恢复RAII补丁。
2. 同Tick 256次BodyID generation绕回导致碰撞结束归错实体：未消费ID避让。
3. Remove后、下一次Step前错误允许保存旧contact cache：明确拒绝保存。
4. 离线诊断body集合/motion与blob对象元数据不一致：共享物理校验比对，并有回归。
5. 原生版本错误诊断及实际加载位置：稳定HostFailure/NativeLibraryIncompatible，延迟绝对路径resolver确保身份一致。
6. 执行故障保留可信前缀、当前操作索引，输出失败保持已发布制品。

独立Reviewer已复核最终本地222项测试、80次CLI与原生探针，确认所有代码阻断项收口。随后双平台CI与制品核验完成，见上表；本轮六项改造的实现和验收均已收口。

2026-11-16 [UI 审查修复](unitycore-ui.md#ui-审查修复2026-11-16)收敛子 Canvas 继承排序约束，开放显示配置并校正实施计划状态；独立代码审查后在隔离 UIDemo 运行双 Domain Reload，各36项 UI EditMode、113项 UI PlayMode、15项真实Bundle Demo，共328次通过，另18项Python门禁通过。436个源码与配置文件指纹匹配；本轮未重跑Player/IL2CPP、截图、性能或远端CI，历史证据保留原范围。

2026-11-17 [合并后UI Player回归](unitycore-ui.md#合并后-windows-player-回归2026-11-17)以PR #30合并提交为产品包基线，补充验收夹具后重新构建Windows Mono/IL2CPP，各14组自动检查通过，共28组；另18项Python门禁通过，4张截图逐张复核。覆盖显示配置、继承排序、缓存及Covered恢复回滚，440份源码/配置指纹匹配。未重跑原生输入、系统窗口关闭、Editor矩阵、性能、设备或远端Unity CI，不改变v2模拟及协议历史基线。
