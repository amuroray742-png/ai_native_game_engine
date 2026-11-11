# PhysicsSandbox

使用 Unity 6000.3.23f1、URP 17.3.0、3D Forward。入口为 `Assets/Scenes/PhysicsSandbox.unity`。SandboxScene 向表现层显式注入 `Assets/Rendering/Body.mat`；物理体通过 `_BaseColor` 区分静态体、球体和盒体。迁移不改变 Jolt、插值、暂停输入或读档续跑。

`python scripts/unity_physics.py prepare` 准备插件；`python scripts/unity_physics.py verify --output TestResults/physics-new` 在独立工程验证双 Domain Reload、暂停、续跑与 CLI 检查点。输出目录不得已存在，隔离工程位置记录在 `workspace.txt`。

管线重建入口为 `ExampleUrpSetup.Apply`，公共 helper 来源为 `scripts/unity/ExampleUrpSetup.cs`。迁移详情见 [资源增强与 URP 示例](../../../docs/assets-enhancement-urp.md)，历史验收见 [PhysicsSandbox 实施与验收](../../../docs/phase-3-physics-sandbox-unity-plan.md)。本轮范围是 Windows Editor，不新增 Player／IL2CPP 已通过声明。
