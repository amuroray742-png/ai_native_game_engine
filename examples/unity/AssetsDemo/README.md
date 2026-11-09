# AssetsDemo

Unity 6000.3.23f1，Windows x64，直接引用仓库中的 UnityCore 与 Assets 包。打开 `Assets/Bootstrap.unity`，示例显示 A/B 两个共享材质、贴图的对象；点击 **Run cleanup checks** 执行池、场景、重启与关闭检查。`Assets/ResourceGroups.asset` 是可编辑配置。

首次运行先构建内容（关闭这个工程的 Editor 后执行）：

```powershell
unity run examples/unity/AssetsDemo -- -executeMethod AssetsDemoSetup.BuildContent
unity open examples/unity/AssetsDemo
```

内容写入被 Git 忽略的 `Assets/StreamingAssets/AiNativeAssets`。构建器只发布到新目录；重建时先停止运行、将旧内容及其目录 meta 归档到 Assets 外，再构建。不要在一轮运行中覆盖内容。

`AssetsDemoSetup.BuildMono` 构建 Development Mono；`BuildIl2Cpp` 构建 IL2CPP Player。需要安装当前 Editor 的 Windows IL2CPP 模块及兼容的 Visual C++ 工具链。构建入口将原生 Bundle Manifest 传给 Player 构建，保留 Bundle 引用的脚本类型。输出在仓库 `TestResults/assets-players`。

完整的隔离验收命令：

```powershell
python scripts/verify_unity_assets.py TestResults/assets-verification-new --players
```

目标目录必须不存在。脚本复制工程和两个源码包，重新构建内容，在开关 Domain Reload 两种设置下分别运行 EditMode/PlayMode 测试，再构建并运行两种 Player。保留源文件摘要、命令、XML、日志、原始 Bundle 报告、Player 结果与渲染截图。测试会生成故意重复或损坏的内容，仅在隔离副本/临时目录使用。

`BundleReportProbe.Run` 是原生 A/B/shared 最小报告探针，输出 `TestResults/assets-probe/bundles`。完整构建的严格归属检查由 `AssetBuilder` 执行。该样例不参与模拟内核或 PlayHost 的玩法装配。
