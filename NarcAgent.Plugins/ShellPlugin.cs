using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Command.Interfaces;
using NarcAgent.Command.Models;

namespace NarcAgent.Plugins
{
    public class ShellPlugin : IAgentPlugin
    {
        private readonly bool _enabled;

        public ShellPlugin(bool enabled)
        {
            _enabled = enabled;
        }

        public IReadOnlySet<string> SupportedActions => new HashSet<string> { "shell_execute" };

        public async Task<CommandResult> ExecuteAsync(JsonDocument command, CancellationToken ct)
        {
            if (!_enabled)
            {
                return CommandResult.Error("shell_execute is disabled by SHELL_ENABLED configuration.");
            }

            var cmd = command.RootElement.GetProperty("command").GetString();
            if (string.IsNullOrWhiteSpace(cmd))
            {
                return CommandResult.Error("'command' field is required.");
            }

            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"-c \"{cmd}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync(ct);
                string error = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                {
                    return CommandResult.Error($"Exit code {process.ExitCode}: {error}");
                }

                return CommandResult.Content(output, "Command executed successfully.");
            }
            catch (Exception ex)
            {
                return CommandResult.Error($"Shell execution failed: {ex.Message}");
            }
        }
    }
}
