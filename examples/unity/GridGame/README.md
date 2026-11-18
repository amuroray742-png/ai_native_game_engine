# GridGame

使用 Unity 6000.3.23f1、URP 17.3.0、3D Forward。入口为 `Assets/Scenes/GridGame.unity`。正交相机、SpriteRenderer、排序层和运行时棋子纹理保持原有行为；GridBoard 显式接收 `Assets/Rendering/GridSprite.mat`，支持纹理、顶点色与透明混合。

`python scripts/unity_grid.py prepare` 准备托管插件；`python scripts/unity_grid.py verify --output TestResults/grid-new` 在独立工程验证快慢表现、双 Domain Reload、重入及 CLI 权威轨迹。输出目录不得已存在，隔离工程位置记录在 `workspace.txt`。

## UI 模块接入

HUD 已改为 `Assets/UI/GridHud.prefab`，由 UnityCore UI 的 Hud 角色和 Assets 的真实 SBP Bundle 加载。场景只保留棋盘、相机和玩法宿主，不再内嵌 Canvas 或 EventSystem。参考分辨率保持 900×650，原有行动方、回合、AP、Tick、选中单位生命、结束回合和重开行为保持不变。

首次本地试玩，先执行 prepare，再构建 UI 内容，然后打开场景：

```powershell
python scripts/unity_grid.py prepare
unity run examples/unity/GridGame -- -executeMethod AiNative.Examples.GridGame.Play.Editor.GridUiBuilder.BuildContent
unity open examples/unity/GridGame
```

修改 HUD 预制体、棋盘图形或材质后需重新构建内容。`verify` 会在隔离副本中自动构建部署 Bundle，不使用旧部署内容充当本次构建。生成的 Bundle 与构建目录不提交 Git。`GridUiBuilder.Migrate` 是旧场景的迁移入口，仓库场景已迁移，正常试玩无需重复执行。

职责如下：

- `GridUiApplication` 在场景装配入口查找或创建一次，持有 Core、Assets、UIModule 与持久 EventSystem。窗口定义只登记一次；场景重载复用应用服务，不重复注册模块。
- `GridGameBootstrap` 保留权威命令提交、Tick 推进、棋盘输入和对局替换。UI 尚未就绪时拒绝棋盘点击，UI 射线优先于棋盘。
- `GridHudModel` 仅保存展示观察与可撤销命令，不拥有 GridHost。每次场景连接创建新模型；重开对局只更新展示观察，可保留 HUD。
- `GridHudPresenter` 管理按钮监听与展示订阅；`GridHud : UIView` 只显示控件。就绪通知独立于玩法 Draw，初次对局创建失败时仍可点击重开重试。
- 场景停用先同步撤销模型命令并取消加载，再串行 Clear。新的 Attach 等待旧操作和 Clear 完成；旧按钮、旧句柄与迟到加载不能影响新场景。

Clear 后窗口、Bundle、资源与挂起操作归零；应用仍在运行，因此保留 application 和新一代 UI 两个空 scope。退出 Play 时由 Core 关闭应用服务。运行错误保留在 `UIError`/`LastError` 并写入日志，不把失败当作就绪。

管线重建入口为 `ExampleUrpSetup.Apply`，公共 helper 来源为 `scripts/unity/ExampleUrpSetup.cs`。迁移详情见 [资源增强与 URP 示例](../../../docs/assets-enhancement-urp.md)，历史宿主证据见 [GridGame Play 宿主](../../../docs/unity-play-host.md)。

棋盘资源由同一套内容构建。场景连接时通过独立的 AssetScope 加载 Assets/Art/GridBoardArt，取得格子、双方棋子精灵和共享材质后创建首局。重开复用该场景的美术资源，停用场景时释放。Windows IL2CPP 复现命令为 python scripts/verify_unity_players.py TestResults/grid-player-new --examples grid --backends Il2Cpp。
