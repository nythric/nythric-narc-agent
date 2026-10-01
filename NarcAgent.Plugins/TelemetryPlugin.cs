using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Command.Interfaces;
using NarcAgent.Command.Models;
using NarcAgent.Core.Services;

namespace NarcAgent.Plugins
{
    public class TelemetryPlugin : IAgentPlugin
    {
        [StructLayout(LayoutKind.Sequential)]
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

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

        private static long _prevIdleTime = 0;
        private static long _prevTotalTime = 0;

        // Cliente HTTP lazy para medir latência com a API
        private static readonly HttpClient _latencyClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        public IReadOnlySet<string> SupportedActions => new HashSet<string> { "request_telemetry" };

        public async Task<CommandResult> ExecuteAsync(JsonDocument command, CancellationToken ct)
        {
            var agentId = (command.RootElement.TryGetProperty("agent_id", out var aid) ? aid.GetString() : null)
                ?? AgentAuthContext.AgentId
                ?? Environment.GetEnvironmentVariable("AGENT_ID")
                ?? "unknown";

            var requestId = command.RootElement.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;

            // Latência real (ms) até a API. -1 se falhar.
            var latency = await MeasureLatencyAsync(ct);

            var cpuUsage = Math.Round(await GetCpuUsageAsync(ct), 2);
            var (ramUsedMb, ramTotalMb) = GetRamUsage();
            var ramUsagePercent = ramTotalMb > 0 ? Math.Round((double)ramUsedMb * 100.0 / (double)ramTotalMb, 2) : 0.0;
            var (diskUsedGb, diskTotalGb, diskUsagePercent) = GetDiskUsage();
            var hostname = GetHostName();
            var kernel = GetKernelInfo();
            var os = GetOsInfo();
            var agentVersion = GetAgentVersion();
            var timestamp = DateTime.UtcNow.ToString("O");
            var uptime = GetUptimeString();
            var location = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "unknown";

            var data = new Dictionary<string, object?>
            {
                ["agent_id"] = agentId,
                ["timestamp"] = timestamp,
                ["uptime"] = uptime,
                ["cpu_percent"] = cpuUsage,
                ["cpu_usage"] = cpuUsage,
                ["ram_used_mb"] = (double)ramUsedMb,
                ["ram_total_mb"] = (int)Math.Max(1, ramTotalMb),
                ["ram_usage"] = ramUsagePercent,
                ["disk_usage"] = diskUsagePercent,
                ["disk_total_gb"] = (int)Math.Max(1, Math.Round(diskTotalGb)),
                ["hostname"] = hostname,
                ["kernel"] = kernel,
                ["os"] = os,
                ["agent_version"] = agentVersion,
                ["location"] = location,
                ["latency"] = latency
            };

            // Envelope de telemetria (type= telemetry) — o AgentCore envia isso via WS
            var envelope = new Dictionary<string, object?>
            {
                ["type"] = "telemetry",
                ["agent_id"] = agentId,
                ["timestamp"] = timestamp,
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
            if (OperatingSystem.IsWindows())
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
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                    memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                    if (GlobalMemoryStatusEx(ref memStatus))
                    {
                        long totalMb = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                        long availMb = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                        long usedMb = Math.Max(0, totalMb - availMb);
                        if (totalMb > 0) return (usedMb, totalMb);
                    }
                }
                catch { }
            }

            // Linux - 1. Segue a lógica perfeita do nythric-narc-command: free -m
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    var freeOutput = RunBashOrDirect("free -m 2>/dev/null | grep -i 'Mem:'", "free", "-m");
                    if (!string.IsNullOrWhiteSpace(freeOutput))
                    {
                        foreach (var line in freeOutput.Split('\n'))
                        {
                            if (line.TrimStart().StartsWith("Mem:", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 3 &&
                                    long.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out long total) &&
                                    long.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out long used) &&
                                    total > 0)
                                {
                                    return (Math.Max(0, used), total);
                                }
                            }
                        }
                    }
                }
                catch { }

                // Linux - 2. Leitura robusta do /proc/meminfo com suporte a kernels sem MemAvailable
                try
                {
                    if (File.Exists("/proc/meminfo"))
                    {
                        long totalKb = 0;
                        long freeKb = 0;
                        long buffersKb = 0;
                        long cachedKb = 0;
                        long reclaimableKb = 0;
                        long availKb = -1;

                        foreach (var line in File.ReadLines("/proc/meminfo"))
                        {
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase)) long.TryParse(parts[1], out totalKb);
                                else if (line.StartsWith("MemFree:", StringComparison.OrdinalIgnoreCase)) long.TryParse(parts[1], out freeKb);
                                else if (line.StartsWith("Buffers:", StringComparison.OrdinalIgnoreCase)) long.TryParse(parts[1], out buffersKb);
                                else if (line.StartsWith("Cached:", StringComparison.OrdinalIgnoreCase)) long.TryParse(parts[1], out cachedKb);
                                else if (line.StartsWith("SReclaimable:", StringComparison.OrdinalIgnoreCase)) long.TryParse(parts[1], out reclaimableKb);
                                else if (line.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase)) long.TryParse(parts[1], out availKb);
                            }
                        }

                        if (totalKb > 0)
                        {
                            long effectiveAvailKb = availKb >= 0
                                ? availKb
                                : freeKb + buffersKb + cachedKb + reclaimableKb;
                            long usedKb = Math.Max(0, totalKb - effectiveAvailKb);
                            return (usedKb / 1024, totalKb / 1024);
                        }
                    }
                }
                catch { }
            }

            // Fallback Genérico via GC e Processo
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                long totalMb = (long)(gcInfo.TotalAvailableMemoryBytes / (1024 * 1024));
                long usedMb = (long)(Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024));
                if (totalMb > 0)
                {
                    return (Math.Max(1, usedMb), totalMb);
                }
            }
            catch { }

            var proc = Process.GetCurrentProcess();
            long procUsed = Math.Max(1, proc.WorkingSet64 / (1024 * 1024));
            return (procUsed, procUsed * 2);
        }

        private static (double usedGb, double totalGb, double usagePercent) GetDiskUsage()
        {
            // Linux - 1. Segue a lógica perfeita do nythric-narc-command: df -P -m /
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    var dfOutput = RunBashOrDirect("df -P -m / 2>/dev/null || df -m . 2>/dev/null", "df", "-P -m /");
                    if (!string.IsNullOrWhiteSpace(dfOutput))
                    {
                        var lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase)) continue;
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 4 &&
                                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double totalM) &&
                                double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double usedM) &&
                                totalM > 0)
                            {
                                var totalGb = Math.Round(totalM / 1024.0, 2);
                                var usedGb = Math.Round(usedM / 1024.0, 2);
                                var usagePercent = Math.Round((usedM / totalM) * 100.0, 2);
                                return (usedGb, totalGb, usagePercent);
                            }
                        }
                    }
                }
                catch { }
            }

            // Windows & Fallback: DriveInfo
            try
            {
                DriveInfo? drive = null;

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        var sysDir = Environment.SystemDirectory;
                        var sysRoot = Path.GetPathRoot(sysDir);
                        if (!string.IsNullOrEmpty(sysRoot))
                        {
                            drive = new DriveInfo(sysRoot);
                        }
                    }
                    catch { }
                }

                if (drive == null || !drive.IsReady || drive.TotalSize <= 0)
                {
                    var root = Path.GetPathRoot(Environment.CurrentDirectory);
                    if (!string.IsNullOrEmpty(root))
                    {
                        try { drive = new DriveInfo(root); } catch { }
                    }
                }

                if (drive == null || !drive.IsReady || drive.TotalSize <= 0)
                {
                    var drives = DriveInfo.GetDrives();
                    drive = drives.FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed && d.TotalSize > 0)
                         ?? drives.FirstOrDefault(d => d.IsReady && d.TotalSize > 0);
                }

                if (drive != null && drive.IsReady && drive.TotalSize > 0)
                {
                    var totalBytes = drive.TotalSize;
                    var freeBytes = drive.AvailableFreeSpace;
                    var usedBytes = totalBytes - freeBytes;

                    var usedGb = Math.Round((double)usedBytes / (1024.0 * 1024 * 1024), 2);
                    var totalGb = Math.Round((double)totalBytes / (1024.0 * 1024 * 1024), 2);
                    var usagePercent = Math.Round((double)usedBytes * 100.0 / (double)totalBytes, 2);
                    return (usedGb, totalGb, usagePercent);
                }
            }
            catch { }

            // Windows P/Invoke GetDiskFreeSpaceEx Fallback
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    string rootPath = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                    if (GetDiskFreeSpaceEx(rootPath, out ulong freeBytes, out ulong totalBytes, out _))
                    {
                        if (totalBytes > 0)
                        {
                            var usedBytes = totalBytes - freeBytes;
                            var usedGb = Math.Round((double)usedBytes / (1024.0 * 1024 * 1024), 2);
                            var totalGb = Math.Round((double)totalBytes / (1024.0 * 1024 * 1024), 2);
                            var usagePercent = Math.Round((double)usedBytes * 100.0 / (double)totalBytes, 2);
                            return (usedGb, totalGb, usagePercent);
                        }
                    }
                }
                catch { }
            }

            return (1.0, 10.0, 10.0);
        }

        private static string GetUptimeString()
        {
            // Uptime do sistema operacional (VPS/servidor)
            string serverUptime = "N/A";
            try
            {
                if (File.Exists("/proc/uptime"))
                {
                    var raw = File.ReadAllText("/proc/uptime").Split(' ')[0];
                    if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
                    {
                        var ts = TimeSpan.FromSeconds(seconds);
                        serverUptime = $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
                    }
                }
                else
                {
                    var uptimeMs = Environment.TickCount64;
                    var ts = TimeSpan.FromMilliseconds(uptimeMs);
                    serverUptime = $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
                }
            }
            catch { }

            // Uptime do processo do agent (tempo desde que o processo iniciou)
            string agentUptime = "N/A";
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
            if (OperatingSystem.IsLinux())
            {
                // Segue o nythric-narc-command: cat /etc/os-release | grep PRETTY_NAME
                try
                {
                    if (File.Exists("/etc/os-release"))
                    {
                        foreach (var line in File.ReadLines("/etc/os-release"))
                        {
                            var match = Regex.Match(line, @"PRETTY_NAME=""?([^""\r\n]+)""?");
                            if (match.Success)
                            {
                                var name = match.Groups[1].Value.Trim();
                                if (!string.IsNullOrWhiteSpace(name)) return name;
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    if (File.Exists("/etc/issue"))
                    {
                        var line = File.ReadLines("/etc/issue").FirstOrDefault()?.Replace("\\n", "").Replace("\\l", "").Trim();
                        if (!string.IsNullOrWhiteSpace(line)) return line;
                    }
                }
                catch { }

                return "Linux";
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var desc = RuntimeInformation.OSDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) return desc;
                }
                catch { }
                return "Windows";
            }

            if (OperatingSystem.IsMacOS()) return "macOS";
            return Environment.OSVersion.Platform.ToString();
        }

        private static string GetHostName()
        {
            // 1. Linux /etc/hostname ou /proc/sys/kernel/hostname
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    if (File.Exists("/etc/hostname"))
                    {
                        var h = File.ReadAllText("/etc/hostname").Trim();
                        if (!string.IsNullOrWhiteSpace(h)) return h;
                    }
                }
                catch { }

                try
                {
                    if (File.Exists("/proc/sys/kernel/hostname"))
                    {
                        var h = File.ReadAllText("/proc/sys/kernel/hostname").Trim();
                        if (!string.IsNullOrWhiteSpace(h)) return h;
                    }
                }
                catch { }

                try
                {
                    var h = RunBashOrDirect("hostname 2>/dev/null", "hostname", "");
                    if (!string.IsNullOrWhiteSpace(h)) return h.Trim();
                }
                catch { }
            }

            // 2. Dns.GetHostName
            try
            {
                var host = Dns.GetHostName();
                if (!string.IsNullOrWhiteSpace(host)) return host;
            }
            catch { }

            // 3. Environment.MachineName
            try
            {
                var machine = Environment.MachineName;
                if (!string.IsNullOrWhiteSpace(machine)) return machine;
            }
            catch { }

            // 4. Windows COMPUTERNAME
            try
            {
                var comp = Environment.GetEnvironmentVariable("COMPUTERNAME");
                if (!string.IsNullOrWhiteSpace(comp)) return comp;
            }
            catch { }

            return "narc-node";
        }

        private static string GetKernelInfo()
        {
            if (OperatingSystem.IsLinux())
            {
                // 1. /proc/sys/kernel/osrelease
                try
                {
                    if (File.Exists("/proc/sys/kernel/osrelease"))
                    {
                        var rel = File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
                        if (!string.IsNullOrWhiteSpace(rel))
                        {
                            string ostype = "Linux";
                            if (File.Exists("/proc/sys/kernel/ostype"))
                            {
                                var t = File.ReadAllText("/proc/sys/kernel/ostype").Trim();
                                if (!string.IsNullOrWhiteSpace(t)) ostype = t;
                            }
                            return $"{ostype} {rel}";
                        }
                    }
                }
                catch { }

                // 2. uname -sr
                try
                {
                    var u = RunBashOrDirect("uname -sr 2>/dev/null || uname -r 2>/dev/null", "uname", "-sr");
                    if (!string.IsNullOrWhiteSpace(u)) return u.Trim();
                }
                catch { }

                // 3. /proc/version
                try
                {
                    if (File.Exists("/proc/version"))
                    {
                        var v = File.ReadAllText("/proc/version").Trim();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            var match = Regex.Match(v, @"Linux version ([^\s]+)");
                            if (match.Success) return $"Linux {match.Groups[1].Value}";
                            return v.Length > 100 ? v.Substring(0, 100) : v;
                        }
                    }
                }
                catch { }
            }

            try
            {
                var desc = RuntimeInformation.OSDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc;
            }
            catch { }

            return Environment.OSVersion.VersionString;
        }

        private static string GetAgentVersion()
        {
            var envVer = Environment.GetEnvironmentVariable("AGENT_VERSION");
            if (!string.IsNullOrWhiteSpace(envVer)) return envVer;

            try
            {
                var asm = Assembly.GetEntryAssembly() ?? typeof(TelemetryPlugin).Assembly;
                var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(infoVer))
                {
                    var plusIdx = infoVer.IndexOf('+');
                    return plusIdx > 0 ? infoVer.Substring(0, plusIdx) : infoVer;
                }

                var ver = asm.GetName().Version;
                if (ver != null) return $"{ver.Major}.{ver.Minor}.{ver.Build}";
            }
            catch { }

            return "1.0.0";
        }

        private static string? RunBashOrDirect(string bashCommand, string directExecutable, string directArgs, int timeoutMs = 2000)
        {
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    using var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "/bin/sh",
                            Arguments = $"-c \"{bashCommand.Replace("\"", "\\\"")}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    proc.Start();
                    var output = proc.StandardOutput.ReadToEnd();
                    if (proc.WaitForExit(timeoutMs) && proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        return output.Trim();
                    }
                }
                catch { }
            }

            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = directExecutable,
                        Arguments = directArgs,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var output = proc.StandardOutput.ReadToEnd();
                if (proc.WaitForExit(timeoutMs) && !string.IsNullOrWhiteSpace(output))
                {
                    return output.Trim();
                }
            }
            catch { }

            return null;
        }
    }
}
