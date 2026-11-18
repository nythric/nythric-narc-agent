using System.Threading;

namespace NarcAgent.Core.Services
{
    /// <summary>
    /// Guarda o token de autenticacao atual do agente em memoria compartilhada,
    /// atualizado pelo AgentCore sempre que conecta ou renova o token.
    /// Plugins (ex: ProxyPlugin) usam isso para autenticar chamadas HTTP de volta
    /// para a API central, em vez de depender de uma env var (AGENT_TOKEN) que
    /// nunca era definida pelo fluxo dinamico de autenticacao.
    /// </summary>
    public static class AgentAuthContext
    {
        private static string? _currentToken;
        private static string? _agentId;
        private static string? _httpBaseUrl;

        public static string? CurrentToken => Volatile.Read(ref _currentToken);
        public static string? AgentId => Volatile.Read(ref _agentId);
        public static string? HttpBaseUrl => Volatile.Read(ref _httpBaseUrl);

        public static void Update(string? token, string? agentId, string? httpBaseUrl = null)
        {
            Volatile.Write(ref _currentToken, token);
            Volatile.Write(ref _agentId, agentId);
            if (!string.IsNullOrEmpty(httpBaseUrl))
            {
                Volatile.Write(ref _httpBaseUrl, httpBaseUrl);
            }
        }
    }
}
