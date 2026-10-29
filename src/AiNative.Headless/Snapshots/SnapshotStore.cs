using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.Headless
{
    internal sealed class SnapshotStore
    {
        private readonly SnapshotEnvironment _environment;

        internal SnapshotStore() : this(SnapshotEnvironment.Current()) { }

        // 测试可使用协议夹具环境；生产入口使用无参构造，不提供跳过兼容性选项。
        internal SnapshotStore(SnapshotEnvironment environment)
        {
            _environment = environment;
        }

        /// <summary>宿主保证 rules 属于 session，同步捕获期间不插入任何模拟调用。</summary>
        internal string Save(string path, SimulationSession<GridCommand, GridGameView> session, GridGameRules rules)
        {
            SessionState sessionState = session.ExportState();
            GridGameState gameState = rules.ExportState();
            byte[] bytes = SnapshotCodec.Encode(sessionState, gameState, _environment);
            return AtomicFilePublisher.Publish(path, stream => stream.Write(bytes));
        }

        internal RestoredGame Load(string path, ScenarioDefinition? expectedConfiguration = null)
        {
            byte[] bytes = JsonInput.ReadFile(path);
            return SnapshotCodec.Decode(bytes, _environment, expectedConfiguration);
        }
    }
}
