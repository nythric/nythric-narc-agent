using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Command.Models;

namespace NarcAgent.Command.Interfaces
{
    public interface IAgentPlugin
    {
        IReadOnlySet<string> SupportedActions { get; }
        Task<CommandResult> ExecuteAsync(JsonDocument command, CancellationToken ct);
    }
}
