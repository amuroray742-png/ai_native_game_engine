# PhysicsSandbox

在 Unity 6000.3.23f1 中打开 `Assets/Scenes/PhysicsSandbox.unity`。示例使用 URP 17.3.0 3D Forward。Jolt 按 v2 固定步长推进，PlayHost 负责暂停输入、插值与读档续跑。

## 运行

在仓库根目录准备运行时插件、场景输入和资源 Bundle。

```powershell
python scripts/unity_physics.py prepare
unity run examples/unity/PhysicsSandbox -- -executeMethod AiNative.Examples.PhysicsPlay.Editor.SandboxContent.BuildContent
```

启动 Play 后可生成球或盒、指定目标 ID、施加冲量、删除刚体、暂停、存读档或重开。修改 HUD 或刚体预制体后可在 Editor 选择 **PhysicsSandbox / Build and deploy presentation content** 重新构建。

## 资源与生命周期

`Assets/Presentation/ResourceGroups.asset` 收集 HUD、Box 和 Sphere。两个刚体预制体共用 `Assets/Rendering/Body.mat`。SandboxDisplay 根据 Jolt 观察结果设置位置、缩放和每个实例的颜色。HUD 由 UnityCore UI 打开，Presenter 负责命令绑定。

SandboxApplication 在场景连接时创建资源作用域，加载两种预制体，打开 HUD 后创建对局。场景停用时同步撤销按钮绑定和释放物理世界，随后清理窗口及资源作用域。重进须等待前一次清理结束。重开或读档可复用已加载的预制体，准备新世界期间保留当前对局。

## 验证

`python scripts/unity_physics.py verify --output TestResults/physics-new` 创建独立工程副本，构建并部署内容，运行双 Domain Reload、HUD 点击、资源释放、暂停、读档、续跑与 CLI 逐 Tick 对照。输出目录必须是新目录，隔离工程位置记录在 `workspace.txt`。Player 构建使用同一内容与裁剪保留信息。

[PhysicsSandbox 实施与验收](../../../docs/phase-3-physics-sandbox-unity-plan.md) · [资源增强与 URP 示例](../../../docs/assets-enhancement-urp.md)
