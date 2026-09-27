using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Command.Interfaces;
using NarcAgent.Command.Models;

namespace NarcAgent.Command
{
    public class CommandRouter
    {
        private readonly Dictionary<string, IAgentPlugin> _plugins = new();

        public void RegisterPlugin(IAgentPlugin plugin)
        {
            foreach (var action in plugin.SupportedActions)
            {
                _plugins[action] = plugin;
            }
        }

        public async Task<CommandResult> RouteAsync(JsonDocument command, CancellationToken ct)
        {
            if (!command.RootElement.TryGetProperty("action", out var actionElement))
            {
                return CommandResult.Error("Missing 'action' field in command.");
            }

            var action = actionElement.GetString();
            if (string.IsNullOrWhiteSpace(action))
            {
                return CommandResult.Error("'action' field is empty.");
            }

            if (!_plugins.TryGetValue(action, out var plugin))
            {
                return CommandResult.Error($"No plugin registered for action: {action}");
            }

            try
            {
                return await plugin.ExecuteAsync(command, ct);
            }
            catch (Exception ex)
            {
                return CommandResult.PluginError(action, ex.Message, ex.ToString());
            }
        }
    }
}
