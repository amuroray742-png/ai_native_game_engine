using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;

namespace AiNative.Headless
{
    internal sealed class SnapshotStore
    {
        private readonly HostEnvironment _environment;

        internal SnapshotStore() : this(SnapshotEnvironment.Current()) { }

        // 测试可使用协议夹具环境；生产入口使用无参构造，不提供跳过兼容性选项。
        internal SnapshotStore(HostEnvironment environment)
        {
            _environment = environment;
        }

        /// <summary>通过对局入口捕获完整状态，不接受可被混配的规则与会话引用。</summary>
        internal string Save(string path, GridGameRuntime game)
        {
            byte[] bytes = SnapshotCodec.Encode(game.ExportState(), _environment);
            return AtomicFilePublisher.Publish(path, stream => stream.Write(bytes));
        }

        internal GridGameRuntime Load(string path, ScenarioDefinition? expectedConfiguration = null)
        {
            byte[] bytes = JsonInput.ReadFile(path);
            return SnapshotCodec.Decode(bytes, _environment, expectedConfiguration);
        }
    }
}
