using System;
using System.IO;

namespace AiNative.Headless
{
    internal static class AtomicFilePublisher
    {
        internal static string Preflight(string path)
        {
            string? destination = null;
            try
            {
                destination = Path.GetFullPath(path);
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new HostOperationException("HostFailure", "OutputExists", "输出目标已存在。", destination);
                }
                if (!Directory.Exists(Path.GetDirectoryName(destination)))
                {
                    throw new IOException("输出父目录不存在。");
                }
                return destination;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException)
            {
                throw new HostOperationException("HostFailure", "OutputWriteFailed", "无效输出路径。", destination, error);
            }
        }

        /// <summary>同目录写完并关闭临时文件，再禁止覆盖地发布；不保证断电耐久性。</summary>
        internal static string Publish(string path, Action<Stream> write)
        {
            string? temporary = null;
            string? destination = null;
            bool ownsTemporary = false;
            try
            {
                destination = Preflight(path);
                string directory = Path.GetDirectoryName(destination) ?? throw new IOException("输出缺少父目录。");
                temporary = Path.Combine(directory, ".ainative-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    write(stream);
                }
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                }
                catch (IOException error) when (File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new HostOperationException("HostFailure", "OutputExists", "发布时输出目标已存在。", destination, error);
                }
                return destination;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException)
            {
                throw new HostOperationException("HostFailure", "OutputWriteFailed", "无法发布输出文件。", destination, error);
            }
            finally
            {
                if (ownsTemporary && temporary != null)
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        // 清理是尽力行为，不能覆盖原始发布失败；目标从未被主动删除。
                    }
                }
            }
        }
    }
}
