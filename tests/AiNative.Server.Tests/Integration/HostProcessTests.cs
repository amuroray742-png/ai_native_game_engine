using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Tests.Configuration;
using AiNative.Server.Configuration;
using AiNative.Server.Lifecycle;
using AiNative.Server.Tests.Transport;
using Xunit;

namespace AiNative.Server.Tests.Integration
{
    public sealed class HostProcessTests
    {
        [Theory]
        [InlineData("gateway", "gateway-1", 0)]
        [InlineData("auth", "auth-1", 0)]
        [InlineData("lobby", "lobby-1", 0)]
        [InlineData("game", "worker-1", 2)]
        [InlineData("auth", "gateway-1", 2)]
        public async Task ProcessRequiresExplicitMatchingRole(string role, string service, int expectedExit)
        {
            string directory = RunDirectory();
            string deployment = Path.Combine(directory, "deployment.json");
            await File.WriteAllTextAsync(deployment, DeploymentConfigurationTests.Sample().ToJsonString());
            using Process process = Start(deployment, service, role, "--validate");
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(expectedExit, process.ExitCode);
                await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), await output);
                await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), await error);
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }

        [Fact]
        public async Task LobbyListensReportsNotReadyAndStopsOwnedProcessCleanly()
        {
            string directory = RunDirectory();
            using X509Certificate2 certificate = AuthenticatedServerStreamTests.Certificate("lobby-1");
            string certificatePath = Path.Combine(directory, "temporary.pfx");
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pkcs12));
            JsonObject configuration = DeploymentConfigurationTests.Sample();
            int port;
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            configuration["services"]![3]!["internalPort"] = port;
            configuration["services"]![3]!["certificateSha256"] = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            string deployment = Path.Combine(directory, "deployment.json");
            await File.WriteAllTextAsync(deployment, configuration.ToJsonString());
            using Process process = Start(deployment, "lobby-1", "lobby", "--certificate", certificatePath, "--control-stdin");
            try
            {
                string? live = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                using JsonDocument status = JsonDocument.Parse(Assert.IsType<string>(live));
                Assert.Equal("live", status.RootElement.GetProperty("Event").GetString());
                Assert.False(status.RootElement.GetProperty("Ready").GetBoolean());
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                probe.Dispose();
                await process.StandardInput.WriteLineAsync("stop");
                process.StandardInput.Close();
                Task<string> remaining = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(0, process.ExitCode);
                Assert.Contains("stopped", await remaining);
                Assert.Empty(await error);
            }
            finally
            {
                if (!process.HasExited)
                { process.Kill(true); await process.WaitForExitAsync(); }
                File.Delete(certificatePath);
            }
        }

        [Fact]
        public async Task AuthStartsBeforeWorkersAndDirectDrainStopsAdmission()
        {
            using var lobbyCertificate = AuthenticatedServerStreamTests.Certificate("lobby-1");
            using var authCertificate = AuthenticatedServerStreamTests.Certificate("auth-1");
            using var workerOne = new TcpListener(IPAddress.Loopback, 0);
            using var workerTwo = new TcpListener(IPAddress.Loopback, 0);
            workerOne.Start();
            workerTwo.Start();
            JsonObject root = DeploymentConfigurationTests.Sample();
            int lobbyPort = FreePort();
            int authPort = FreePort();
            root["services"]![3]!["internalPort"] = lobbyPort;
            root["services"]![2]!["internalPort"] = authPort;
            root["services"]![4]!["internalPort"] = ((IPEndPoint)workerOne.LocalEndpoint).Port;
            root["services"]![5]!["internalPort"] = ((IPEndPoint)workerTwo.LocalEndpoint).Port;
            root["services"]![3]!["certificateSha256"] = lobbyCertificate.GetCertHashString(HashAlgorithmName.SHA256);
            root["services"]![2]!["certificateSha256"] = authCertificate.GetCertHashString(HashAlgorithmName.SHA256);
            DeploymentConfiguration configuration = DeploymentConfiguration.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
            var lobbyLive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var authLive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var lobby = new RoleHost(configuration, configuration.Select("lobby-1", "lobby"), lobbyCertificate,
                status => { if (status.Event == "live") { lobbyLive.TrySetResult(); } });
            var auth = new RoleHost(configuration, configuration.Select("auth-1", "auth"), authCertificate,
                status => { if (status.Event == "live") { authLive.TrySetResult(); } });
            Task<RoleStopResult> lobbyRun = lobby.RunAsync(stop.Token);
            await lobbyLive.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<RoleStopResult> authRun = auth.RunAsync(stop.Token);
            try
            {
                await authLive.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(RoleState.Live, auth.State);
                auth.BeginDrain();
                Assert.True((await authRun.WaitAsync(TimeSpan.FromSeconds(5))).Clean);
                using var probe = new TcpClient();
                await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, authPort));
            }
            finally
            {
                stop.Cancel();
                await Task.WhenAll(lobbyRun, authRun).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static Process Start(string deployment, string service, string role, params string[] extra)
        {
            string root = Root();
            string build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            string host = Path.Combine(root, "src", "AiNative.Server.Host", "bin", build, "net10.0", "AiNative.Server.Host.dll");
            var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { host, "--deployment", deployment, "--service", service, "--role", role })
            { start.ArgumentList.Add(argument); }
            foreach (string argument in extra)
            { start.ArgumentList.Add(argument); }
            return Process.Start(start) ?? throw new InvalidOperationException("无法启动测试子进程。");
        }
        private static string RunDirectory()
        {
            string path = Path.Combine(Root(), "TestResults", "server", "host-process", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
        private static string Root()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AiNative.sln")))
            { directory = directory.Parent; }
            return directory?.FullName ?? throw new InvalidOperationException("未找到仓库。");
        }
    }
}
