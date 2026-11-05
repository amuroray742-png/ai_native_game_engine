using System;
using System.Collections.Generic;
using System.Text.Json;
using AiNative.Core;

namespace AiNative.Hosting
{
    /// <summary>显式装配的游戏适配。验证方法不执行输入、不推进物理、不读取文件。</summary>
    public interface IGameModule
    {
        string Id { get; }
        int Version { get; }
        SimulationTiming Timing { get; }
        JsonElement Description { get; }
        IReadOnlyList<Type> CodeTypes { get; }
        IReadOnlyList<string> NativeLibraries { get; }
        JsonElement ReadConfiguration(JsonElement value);
        JsonElement ReadCommand(JsonElement value);
        JsonElement ReadState(JsonElement configuration, JsonElement state, long processed);
        void ValidateResult(JsonElement result);
        IHostedGame Create(JsonElement configuration, ulong seed);
        IHostedGame Restore(JsonElement configuration, JsonElement state, RuntimeState runtime);
    }

    public interface IHostedGame : IDisposable
    {
        long Tick { get; }
        long Submitted { get; }
        long Processed { get; }
        int Pending { get; }
        ulong RandomState { get; }
        SimulationTiming Timing { get; }
        long Submit(JsonElement command);
        JsonElement Advance();
        JsonElement Observe();
        JsonElement CaptureGameState();
    }

    public sealed class GameRegistry
    {
        private readonly Dictionary<string, IGameModule> _modules = new Dictionary<string, IGameModule>(StringComparer.Ordinal);

        public GameRegistry(params IGameModule[] modules)
        {
            foreach (IGameModule module in modules)
            {
                if (module == null || string.IsNullOrWhiteSpace(module.Id) || _modules.ContainsKey(module.Id))
                {
                    throw new ArgumentException("游戏模块不能为空或重复。");
                }
                _modules.Add(module.Id, module);
            }
        }

        public IEnumerable<IGameModule> Modules => _modules.Values;
        public IGameModule Get(string id) => _modules.TryGetValue(id, out IGameModule? module)
            ? module : throw new ProtocolException("UnknownGame", "未注册游戏：" + id, "$.gameId");

        /// <summary>校验 v2 场景并选择已注册模块。宿主负责读取文件。</summary>
        public (IGameModule Module, JsonElement Configuration) ReadScenario(JsonElement root)
        {
            JsonData.Version(root);
            ProtocolJson.Object(root, "$", "formatVersion", "gameId", "gameVersion", "configuration");
            IGameModule module = Get(ProtocolJson.String(root.GetProperty("gameId"), "$.gameId"));
            JsonData.Require(ProtocolJson.Int32(root.GetProperty("gameVersion"), "$.gameVersion") == module.Version, "UnsupportedGameVersion", "$.gameVersion");
            return (module, module.ReadConfiguration(root.GetProperty("configuration")));
        }
    }
}
