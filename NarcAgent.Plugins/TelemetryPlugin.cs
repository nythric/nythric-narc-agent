using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Command.Interfaces;
using NarcAgent.Command.Models;

namespace NarcAgent.Plugins
{
    public class TelemetryPlugin : IAgentPlugin
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        private static long _prevIdleTime = 0;
        private static long _prevTotalTime = 0;

        // Cliente HTTP lazy para medir latência com a API
        private static readonly HttpClient _latencyClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        public IReadOnlySet<string> SupportedActions => new HashSet<string> { "request_telemetry" };

        public async Task<CommandResult> ExecuteAsync(JsonDocument command, CancellationToken ct)
        {
            var agentId = command.RootElement.TryGetProperty("agent_id", out var aid) ? aid.GetString() : null;
            var requestId = command.RootElement.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;

            // Latência real (ms) até a API. -1 se falhar.
            var latency = await MeasureLatencyAsync(ct);

            var data = new
            {
                uptime = GetUptimeString(),
                cpu_usage = Math.Round(await GetCpuUsageAsync(ct), 2),
                ram_usage = GetRamUsagePercent(),
                disk_usage = GetDiskUsagePercent(),
                location = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "unknown",
                latency = latency,
                os = GetOsInfo()
            };

            // Envelope de telemetria (type= telemetry) — o AgentCore envia isso via WS
            var envelope = new Dictionary<string, object?>
            {
                ["type"] = "telemetry",
                ["agent_id"] = agentId,
                ["timestamp"] = DateTime.UtcNow.ToString("O"),
                ["data"] = data
            };

            if (!string.IsNullOrEmpty(requestId))
                envelope["request_id"] = requestId;

            var json = JsonSerializer.Serialize(envelope);
            return CommandResult.Content(json, "Telemetry data");
        }

        private static async Task<long> MeasureLatencyAsync(CancellationToken ct)
        {
            try
            {
                var apiUrl = Environment.GetEnvironmentVariable("API_CENTRAL_URL");
                if (string.IsNullOrEmpty(apiUrl)) return -1;

                var baseUrl = apiUrl
                    .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                    .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                    .TrimEnd('/');

                var sw = Stopwatch.StartNew();
                using var resp = await _latencyClient.GetAsync($"{baseUrl}/", ct);
                sw.Stop();
                return resp.IsSuccessStatusCode ? sw.ElapsedMilliseconds : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static async Task<double> GetCpuUsageAsync(CancellationToken ct)
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                try
                {
                    if (GetSystemTimes(out long idleTime, out long kernelTime, out long userTime))
                    {
                        long sysTotalTime = kernelTime + userTime;
                        
                        if (_prevTotalTime != 0)
                        {
                            long idleDelta = idleTime - _prevIdleTime;
                            long totalDelta = sysTotalTime - _prevTotalTime;
                            
                            _prevIdleTime = idleTime;
                            _prevTotalTime = sysTotalTime;

                            if (totalDelta > 0)
                            {
                                double usage = (1.0 - (double)idleDelta / totalDelta) * 100.0;
                                return Math.Max(0, Math.Min(100, Math.Round(usage, 2)));
                            }
                        }
                        else
                        {
                            _prevIdleTime = idleTime;
                            _prevTotalTime = sysTotalTime;
                            await Task.Delay(200, ct);
                            if (GetSystemTimes(out long newIdleTime, out long newKernelTime, out long newUserTime))
                            {
                                long newTotalTime = newKernelTime + newUserTime;
                                long idleDelta = newIdleTime - _prevIdleTime;
                                long totalDelta = newTotalTime - _prevTotalTime;
                                
                                _prevIdleTime = newIdleTime;
                                _prevTotalTime = newTotalTime;

                                if (totalDelta > 0)
                                {
                                    double usage = (1.0 - (double)idleDelta / totalDelta) * 100.0;
                                    return Math.Max(0, Math.Min(100, Math.Round(usage, 2)));
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            try
            {
                if (File.Exists("/proc/stat"))
                {
                    var line1 = await File.ReadAllTextAsync("/proc/stat", ct);
                    var cpu1 = ParseCpuLine(line1);

                    await Task.Delay(200, ct);

                    var line2 = await File.ReadAllTextAsync("/proc/stat", ct);
                    var cpu2 = ParseCpuLine(line2);

                    var totalDelta = cpu2.total - cpu1.total;
                    var idleDelta = cpu2.idle - cpu1.idle;
                    if (totalDelta <= 0) return 0;
                    return Math.Round((1.0 - (double)idleDelta / totalDelta) * 100, 2);
                }
            }
            catch { }

            // Fallback Genérico (Uso de CPU do Processo Atual)
            var proc = Process.GetCurrentProcess();
            var startCpu = proc.TotalProcessorTime;
            var startTime = DateTime.UtcNow;
            await Task.Delay(200, ct);
            var endCpu = proc.TotalProcessorTime;
            var endTime = DateTime.UtcNow;
            var cpuUsed = (endCpu - startCpu).TotalMilliseconds;
            var totalTime = (endTime - startTime).TotalMilliseconds * Environment.ProcessorCount;
            return totalTime > 0 ? Math.Round((cpuUsed / totalTime) * 100, 2) : 0;
        }

        private static (long total, long idle) ParseCpuLine(string content)
        {
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("cpu ")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) return (0, 0);

                long user = long.Parse(parts[1]);
                long nice = long.Parse(parts[2]);
                long system = long.Parse(parts[3]);
                long idle = long.Parse(parts[4]);
                long iowait = parts.Length > 5 ? long.Parse(parts[5]) : 0;
                long irq = parts.Length > 6 ? long.Parse(parts[6]) : 0;
                long softirq = parts.Length > 7 ? long.Parse(parts[7]) : 0;
                long steal = parts.Length > 8 ? long.Parse(parts[8]) : 0;

                long total = user + nice + system + idle + iowait + irq + softirq + steal;
                return (total, idle + iowait);
            }
            return (0, 0);
        }

        private static (long usedMb, long totalMb) GetRamUsage()
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                try
                {
                    MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                    memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                    if (GlobalMemoryStatusEx(ref memStatus))
                    {
                        long totalMb = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                        long availMb = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                        long usedMb = totalMb - availMb;
                        return (usedMb, totalMb);
                    }
                }
                catch { }
            }

            // Linux: lê /proc/meminfo
            try
            {
                if (File.Exists("/proc/meminfo"))
                {
                    var lines = File.ReadAllText("/proc/meminfo").Split('\n');
                    long totalKb = 0, availableKb = 0;
                    foreach (var line in lines)
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1) {
                            if (line.StartsWith("MemTotal:")) long.TryParse(parts[1], out totalKb);
                            if (line.StartsWith("MemAvailable:")) long.TryParse(parts[1], out availableKb);
                        }
                    }
                    if (totalKb > 0)
                    {
                        var usedKb = totalKb - availableKb;
                        return (usedKb / 1024, totalKb / 1024);
                    }
                }
            }
            catch { }

            // Fallback Genérico
            var proc = Process.GetCurrentProcess();
            return (proc.WorkingSet64 / (1024 * 1024), GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        }

        private static double GetRamUsagePercent()
        {
            var (usedMb, totalMb) = GetRamUsage();
            if (totalMb <= 0) return 0;
            return Math.Round((double)usedMb * 100.0 / (double)totalMb, 2);
        }

        private static double GetDiskUsagePercent()
        {
            try
            {
                var root = Path.GetPathRoot(Environment.CurrentDirectory);
                if (root == null) return 0;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return 0;
                if (drive.TotalSize <= 0) return 0;
                var used = drive.TotalSize - drive.AvailableFreeSpace;
                return Math.Round(used * 100.0 / drive.TotalSize, 2);
            }
            catch
            {
                return 0;
            }
        }

        private static (double usedGb, double totalGb) GetDiskUsage()
        {
            try
            {
                var root = Path.GetPathRoot(Environment.CurrentDirectory);
                if (root == null) return (0, 0);
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return (0, 0);
                var usedGb = (drive.TotalSize - drive.AvailableFreeSpace) / (1024.0 * 1024 * 1024);
                var totalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
                return (usedGb, totalGb);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static string GetUptimeString()
        {
            // Linux: /proc/uptime em segundos - uptime do sistema (VPS/servidor)
            string serverUptime = "N/A";
            try
            {
                if (File.Exists("/proc/uptime"))
                {
                    var raw = File.ReadAllText("/proc/uptime").Split(' ')[0];
                    if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        var ts = TimeSpan.FromSeconds(seconds);
                        serverUptime = $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
                    }
                }
            }
            catch { }

            // Uptime do processo do agent (tempo desde que o processo iniciou)
            var agentUptime = "N/A";
            try
            {
                var procTs = DateTime.Now - Process.GetCurrentProcess().StartTime;
                agentUptime = $"{(int)procTs.TotalDays}d {procTs.Hours}h {procTs.Minutes}m";
            }
            catch { }

            // Formato: "S:uptime|A:uptime" - server uptime e agent process uptime
            return $"S:{serverUptime}|A:{agentUptime}";
        }

        private static string GetOsInfo()
        {
            if (OperatingSystem.IsLinux()) return "linux";
            if (OperatingSystem.IsWindows()) return "windows";
            if (OperatingSystem.IsMacOS()) return "darwin";
            return Environment.OSVersion.Platform.ToString().ToLowerInvariant();
        }
    }
}
