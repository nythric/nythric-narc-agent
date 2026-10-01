using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Core.Services;
using NarcAgent.Plugins;
using Xunit;

namespace NarcAgent.Tests
{
    public class TelemetryPluginTests
    {
        [Fact]
        public async Task ExecuteAsync_GeneratesCompleteTelemetryPayload()
        {
            var plugin = new TelemetryPlugin();
            var agentId = "agent-unit-test-123";
            AgentAuthContext.Update("fake-token", agentId);

            var cmdJson = $"{{\"action\":\"request_telemetry\",\"agent_id\":\"{agentId}\"}}";
            using var command = JsonDocument.Parse(cmdJson);

            var result = await plugin.ExecuteAsync(command, CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.False(string.IsNullOrEmpty(result.Payload));

            using var doc = JsonDocument.Parse(result.Payload!);
            var root = doc.RootElement;

            Assert.Equal("telemetry", root.GetProperty("type").GetString());
            Assert.Equal(agentId, root.GetProperty("agent_id").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("timestamp").GetString()));

            var data = root.GetProperty("data");

            // Verify essential metrics requested
            Assert.True(data.TryGetProperty("cpu_percent", out var cpuPercentProp));
            Assert.True(cpuPercentProp.GetDouble() >= 0);

            Assert.True(data.TryGetProperty("cpu_usage", out var cpuUsageProp));
            Assert.True(cpuUsageProp.GetDouble() >= 0);

            Assert.True(data.TryGetProperty("ram_used_mb", out var ramUsedProp));
            Assert.True(ramUsedProp.GetDouble() > 0, "ram_used_mb should be greater than 0");

            Assert.True(data.TryGetProperty("ram_total_mb", out var ramTotalProp));
            Assert.True(ramTotalProp.GetInt32() > 0, "ram_total_mb should be greater than 0");

            Assert.True(data.TryGetProperty("ram_usage", out var ramUsageProp));
            Assert.True(ramUsageProp.GetDouble() >= 0);

            Assert.True(data.TryGetProperty("disk_total_gb", out var diskTotalProp));
            Assert.True(diskTotalProp.GetInt32() > 0, "disk_total_gb should be greater than 0");

            Assert.True(data.TryGetProperty("disk_usage", out var diskUsageProp));
            Assert.True(diskUsageProp.GetDouble() >= 0);

            Assert.True(data.TryGetProperty("hostname", out var hostProp));
            Assert.False(string.IsNullOrWhiteSpace(hostProp.GetString()), "hostname should not be blank");

            Assert.True(data.TryGetProperty("kernel", out var kernelProp));
            Assert.False(string.IsNullOrWhiteSpace(kernelProp.GetString()), "kernel should not be blank");

            Assert.True(data.TryGetProperty("os", out var osProp));
            Assert.False(string.IsNullOrWhiteSpace(osProp.GetString()), "os should not be blank");

            Assert.True(data.TryGetProperty("agent_version", out var verProp));
            Assert.False(string.IsNullOrWhiteSpace(verProp.GetString()), "agent_version should not be blank");

            Assert.True(data.TryGetProperty("uptime", out var uptimeProp));
            var uptimeStr = uptimeProp.GetString();
            Assert.False(string.IsNullOrWhiteSpace(uptimeStr));
            Assert.Contains("S:", uptimeStr);
            Assert.Contains("|A:", uptimeStr);
        }
    }
}
