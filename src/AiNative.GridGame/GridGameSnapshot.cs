using System;
using AiNative.Core;

namespace AiNative.GridGame
{
    /// <summary>不可变的会话与玩法恢复材料；构造只检查 null，Restore 负责领域及跨模块校验。</summary>
    public sealed class GridGameSnapshot
    {
        public GridGameSnapshot(SessionState session, GridGameState game)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Game = game ?? throw new ArgumentNullException(nameof(game));
        }

        public SessionState Session { get; }
        public GridGameState Game { get; }
    }
}
