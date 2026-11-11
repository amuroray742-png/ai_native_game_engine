# GridGame

使用 Unity 6000.3.23f1、URP 17.3.0、3D Forward。入口为 `Assets/Scenes/GridGame.unity`。正交相机、SpriteRenderer、排序层和运行时棋子纹理保持原有行为；GridBoard 显式接收 `Assets/Rendering/GridSprite.mat`，支持纹理、顶点色与透明混合。

`python scripts/unity_grid.py prepare` 准备托管插件；`python scripts/unity_grid.py verify --output TestResults/grid-new` 在独立工程验证快慢表现、双 Domain Reload、重入及 CLI 权威轨迹。输出目录不得已存在，隔离工程位置记录在 `workspace.txt`。

管线重建入口为 `ExampleUrpSetup.Apply`，公共 helper 来源为 `scripts/unity/ExampleUrpSetup.cs`。迁移详情见 [资源增强与 URP 示例](../../../docs/assets-enhancement-urp.md)，历史宿主证据见 [GridGame Play 宿主](../../../docs/unity-play-host.md)。本轮范围是 Windows Editor，不新增 Player／IL2CPP 已通过声明。
