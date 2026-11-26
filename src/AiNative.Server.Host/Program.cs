using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Configuration;
using AiNative.Server.Lifecycle;

namespace AiNative.Server.Host
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                Dictionary<string, string> options = Parse(args);
                string path = Required(options, "--deployment");
                if (new FileInfo(path).Length > 1024 * 1024)
                { throw new ArgumentException("部署清单过大。"); }
                DeploymentConfiguration deployment = DeploymentConfiguration.Parse(await File.ReadAllBytesAsync(path));
                ServiceConfiguration service = deployment.Select(Required(options, "--service"), Required(options, "--role"));
                if (options.ContainsKey("--validate"))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { serviceId = service.ServiceId, role = service.Role.ToString(), validated = true }));
                    return 0;
                }
                string? password = options.TryGetValue("--password-env", out string? variable) ?
                    Environment.GetEnvironmentVariable(variable) ?? throw new ArgumentException("证书密码环境变量未设置。") : null;
                using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(Required(options, "--certificate"), password);
                var host = new RoleHost(deployment, service, certificate, status => Console.WriteLine(JsonSerializer.Serialize(status)));
                using var stop = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
                Console.CancelKeyPress += cancel;
                try
                {
                    Task<RoleStopResult> running = host.RunAsync(stop.Token);
                    Task control = options.ContainsKey("--control-stdin") && !running.IsCompleted ? ControlAsync(host, stop) : Task.CompletedTask;
                    RoleStopResult result = await running;
                    stop.Cancel();
                    if (options.ContainsKey("--control-stdin"))
                    {
                        try
                        { await control; }
                        catch (OperationCanceledException) { }
                    }
                    return result.Clean ? 0 : 1;
                }
                finally { Console.CancelKeyPress -= cancel; }
            }
            catch (Exception error)
            {
                // 不输出输入正文、证书路径、私钥或密码；类型用于定位启动失败类别。
                Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "HostFailed", category = error.GetType().Name }));
                return 2;
            }
        }

        private static async Task ControlAsync(RoleHost host, CancellationTokenSource stop)
        {
            while (!stop.IsCancellationRequested)
            {
                string? command = await Console.In.ReadLineAsync(stop.Token);
                if (command == null || command == "stop")
                { stop.Cancel(); return; }
                if (command == "drain")
                { host.BeginDrain(); stop.Cancel(); return; }
                if (command == "status")
                { Console.WriteLine(JsonSerializer.Serialize(new { state = host.State.ToString(), ready = false })); }
            }
        }

        private static string Required(Dictionary<string, string> options, string key) =>
            options.TryGetValue(key, out string? value) ? value : throw new ArgumentException("缺少必要参数。");

        private static Dictionary<string, string> Parse(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index++)
            {
                string key = args[index];
                bool flag = key == "--validate" || key == "--control-stdin";
                if (!flag && key != "--deployment" && key != "--service" && key != "--role" && key != "--certificate" && key != "--password-env")
                { throw new ArgumentException("未知参数。"); }
                string value = flag ? "true" : ++index < args.Length ? args[index] : throw new ArgumentException("参数缺少值。");
                if (!options.TryAdd(key, value))
                { throw new ArgumentException("重复参数。"); }
            }
            return options;
        }
    }
}
