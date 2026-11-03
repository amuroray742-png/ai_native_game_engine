using System.IO;
using System.Text.Json;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;

namespace AiNative.Headless
{
    public static class ScenarioLoader
    {
        /// <summary>读取场景文件，复用共享格式校验；保留旧入口的 JsonException 失败类型。</summary>
        public static ScenarioDefinition Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                return ScenarioCodec.Decode(bytes);
            }
            catch (ProtocolException error)
            {
                throw new JsonException(error.Message, error);
            }
        }
    }
}
