# 第三方依赖

项目使用的第三方源码和NuGet包统一下载到此目录。下载内容不提交Git；原生源码固定提交、NuGet版本与内容摘要通过各项目的`packages.lock.json`锁定。

| 目录 | 内容 | 获取方式 |
| --- | --- | --- |
| `JoltPhysics/` | Jolt 5.6.0，提交`e77f175595e64cb44218cc9d9d56fc365ad0e36a` | `python scripts/build_native.py` |
| `joltc/` | Jolt C接口，提交`2bbcf73b1bec261e0351814662922f1b4784454c` | `python scripts/build_native.py` |
| `nuget/` | .NET直接与传递依赖，包含JSON、测试SDK和xUnit | `dotnet restore --locked-mode` |

以上命令在仓库根目录执行。原生脚本仅在源码不存在时下载，并检查提交与clean状态；已有源码的改动不会被自动覆盖。高级调用仍可用`--jolt-source`和`--joltc-source`显式指定本地源码。

NuGet包恢复位置由[Directory.Build.props](../Directory.Build.props)统一配置；包引用仍在所属项目的csproj中，锁文件保持原位，不改变依赖版本或包源。恢复后的包内容及其随附许可证位于`nuget/`。此配置不迁移或删除用户级全局缓存。

项目维护的适配、补丁与验证代码位于[native/jolt](../native/jolt)，原生中间文件和发布产物位于`artifacts/`；运行时所需依赖由构建复制到各项目输出目录。

从旧目录升级时，可以将`TestResults/native-deps/JoltPhysics`与`TestResults/native-deps/joltc`移动到本目录对应位置，再执行上述命令。原生构建清单记录实际源码路径，移动后重建可能改变构建身份；严格快照恢复仍遵循[当前契约](../docs/framework-v2.md)，需要时重新生成回放材料。
