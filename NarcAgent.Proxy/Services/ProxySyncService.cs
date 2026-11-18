using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Core.Services;
using NarcAgent.Proxy.Models;
using NarcAgent.Proxy.Services.Nginx;

namespace NarcAgent.Proxy.Services
{
    public class ProxySyncService
    {
        private readonly NginxService _nginxService;
        private readonly HttpClient _httpClient;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        public ProxySyncService(NginxService nginxService, HttpClient? httpClient = null)
        {
            _nginxService = nginxService;
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        public NginxService NginxService => _nginxService;

        /// <summary>
        /// Busca a lista autoritativa de proxies cadastrados no banco de dados para o agente atual.
        /// Se o token estiver expirado (401), renova ou autentica automaticamente e tenta novamente.
        /// </summary>
        public async Task<List<DatabaseProxy>> FetchProxiesFromApiAsync(string apiUrl, string agentId, string? token, CancellationToken ct)
        {
            var baseClean = apiUrl.TrimEnd('/');
            if (baseClean.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                baseClean = "http://" + baseClean["ws://".Length..];
            else if (baseClean.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                baseClean = "https://" + baseClean["wss://".Length..];
            else if (!baseClean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !baseClean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseClean = "https://" + baseClean;

            var endpoints = new[]
            {
                $"{baseClean}/api/admin/proxy/{agentId}/configs",
                $"{baseClean}/api/admin/proxy?agent_id={agentId}",
                $"{baseClean}/api/admin/proxy"
            };

            bool retriedAuth = false;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                foreach (var url in endpoints)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        if (!string.IsNullOrEmpty(token))
                        {
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        }

                        var response = await _httpClient.SendAsync(req, ct);
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync(ct);
                            var proxies = JsonSerializer.Deserialize<List<DatabaseProxy>>(content, JsonOpts);
                            if (proxies != null)
                            {
                                return proxies;
                            }
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !retriedAuth)
                        {
                            Console.WriteLine($"[ProxySyncService] API retornou 401 Unauthorized em {url}. Renovando autenticação...");
                            var freshToken = await AuthenticateAgentAsync(baseClean, agentId, ct);
                            if (!string.IsNullOrEmpty(freshToken))
                            {
                                token = freshToken;
                                retriedAuth = true;
                                break; // Sai do loop interno para tentar os endpoints com o novo token
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[ProxySyncService] API endpoint {url} retornou HTTP {(int)response.StatusCode}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ProxySyncService] Falha ao consultar endpoint {url}: {ex.Message}");
                    }
                }

                if (!retriedAuth) break;
            }

            return new List<DatabaseProxy>();
        }

        /// <summary>
        /// Registra/conecta com a API central para obter um token JWT válido e aprovado.
        /// </summary>
        public async Task<string?> AuthenticateAgentAsync(string apiUrl, string agentId, CancellationToken ct)
        {
            try
            {
                var baseClean = apiUrl.TrimEnd('/');
                var agentName = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "NarcAgent";

                var connectUrl = $"{baseClean}/api/agentes/conectar";
                var payload = JsonSerializer.Serialize(new { id = agentId, name = agentName });
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

                var resp = await _httpClient.PostAsync(connectUrl, content, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
                    var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
                    var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

                    if (status == "approved" && !string.IsNullOrEmpty(token))
                    {
                        DateTime? expUtc = null;
                        if (root.TryGetProperty("expires_at", out var expProp) && !string.IsNullOrEmpty(expProp.GetString()))
                        {
                            if (DateTimeOffset.TryParse(expProp.GetString(), out var dto))
                                expUtc = dto.UtcDateTime;
                        }

                        Console.WriteLine($"[ProxySyncService] Agente autenticado com sucesso! Status: {status}");
                        AgentAuthContext.Update(token, agentId, baseClean);

                        try
                        {
                            var storage = new TokenStorage();
                            await storage.SaveAsync(token, expUtc, refreshToken);
                        }
                        catch { }

                        return token;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProxySyncService] Erro ao autenticar agente: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Executa o ciclo completo de sincronização e reconciliação:
        /// 1. Obtém credenciais (token, agentId, apiUrl).
        /// 2. Busca lista de proxies no banco de dados via API.
        /// 3. Remove configs órfãos e configura/injeta SSL em todos os proxies ativos.
        /// </summary>
        public async Task<ReconciliationResult> SyncAndReconcileAsync(
            string? apiUrl = null, 
            string? agentId = null, 
            string? token = null, 
            CancellationToken ct = default)
        {
            apiUrl ??= AgentAuthContext.HttpBaseUrl
                     ?? Environment.GetEnvironmentVariable("API_CENTRAL_URL")
                     ?? "https://agent.narc.fun";

            agentId ??= AgentAuthContext.AgentId
                      ?? Environment.GetEnvironmentVariable("AGENT_ID")
                      ?? "default-agent-id";

            token ??= AgentAuthContext.CurrentToken;

            // Se ainda não temos token, tenta carregar do armazenamento local
            if (string.IsNullOrEmpty(token))
            {
                try
                {
                    var storage = new TokenStorage();
                    var stored = await storage.LoadAsync();
                    if (stored != null && !string.IsNullOrEmpty(stored.Token))
                    {
                        bool isExpired = stored.ExpiresAt.HasValue && stored.ExpiresAt.Value <= DateTime.UtcNow;
                        if (!isExpired)
                        {
                            token = stored.Token;
                        }
                    }
                }
                catch { }
            }

            // Se o token ainda estiver ausente ou expirado, autentica na API agora
            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine("[ProxySyncService] Token ausente ou expirado. Solicitando novo token à API...");
                token = await AuthenticateAgentAsync(apiUrl, agentId, ct);
            }

            Console.WriteLine($"[ProxySyncService] Iniciando sincronização com API: {apiUrl} (Agent: {agentId})...");

            var proxies = await FetchProxiesFromApiAsync(apiUrl, agentId, token, ct);

            if (proxies.Count == 0)
            {
                Console.WriteLine("[ProxySyncService] Nenhum proxy retornado pela API ou falha de autenticação. Executando verificação local...");
                var (v, f, ok, err) = await _nginxService.VerifyLocalConfigsAsync(ct);
                await ReportSslStatusToApiAsync(apiUrl, agentId, token, ct);
                return new ReconciliationResult
                {
                    TotalDbProxies = 0,
                    VerifiedConfigs = v,
                    SslInjectedConfigs = f,
                    Success = ok,
                    Errors = string.IsNullOrEmpty(err) ? new List<string>() : new List<string> { err }
                };
            }

            Console.WriteLine($"[ProxySyncService] {proxies.Count} proxies retornados pelo banco de dados. Reconciliando com Nginx...");
            var reconResult = await _nginxService.ReconcileWithDatabaseAsync(proxies, ct);
            await ReportSslStatusToApiAsync(apiUrl, agentId, token, ct);
            return reconResult;
        }

        /// <summary>
        /// Coleta todos os domínios com certificado SSL no disco local (/etc/narc/ssl/{domain})
        /// e envia um relatório em lote para a API sincronizar o banco de dados (ssl_issued=1, ssl_expires_at, raw_config).
        /// </summary>
        public async Task<int> ReportSslStatusToApiAsync(
            string? apiUrl = null,
            string? agentId = null,
            string? token = null,
            CancellationToken ct = default)
        {
            try
            {
                apiUrl ??= AgentAuthContext.HttpBaseUrl
                         ?? Environment.GetEnvironmentVariable("API_CENTRAL_URL")
                         ?? "https://agent.narc.fun";

                agentId ??= AgentAuthContext.AgentId
                          ?? Environment.GetEnvironmentVariable("AGENT_ID")
                          ?? "default-agent-id";

                token ??= AgentAuthContext.CurrentToken;

                var baseClean = apiUrl.TrimEnd('/');
                if (baseClean.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                    baseClean = "http://" + baseClean["ws://".Length..];
                else if (baseClean.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                    baseClean = "https://" + baseClean["wss://".Length..];
                else if (!baseClean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !baseClean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    baseClean = "https://" + baseClean;

                var domains = _nginxService.SslService.GetAllManagedDomains();
                if (domains.Count == 0) return 0;

                var items = new List<object>();
                foreach (var domain in domains)
                {
                    var exp = NginxSslService.GetCertificateExpiration(domain);
                    string? expStr = exp.HasValue ? exp.Value.ToString("yyyy-MM-dd HH:mm:ss") : null;
                    var confPath = await _nginxService.FindConfigFileForDomainAsync(domain);
                    string? rawConfig = null;
                    if (!string.IsNullOrEmpty(confPath) && File.Exists(confPath))
                    {
                        try { rawConfig = File.ReadAllText(confPath); } catch { }
                    }

                    items.Add(new
                    {
                        domain,
                        success = true,
                        ssl_issued = true,
                        ssl_expires_at = expStr,
                        raw_config = rawConfig,
                        message = "Certificado verificado no disco local"
                    });
                }

                var endpoints = new[]
                {
                    $"{baseClean}/api/admin/ssl/{agentId}/sync",
                    $"{baseClean}/api/ssl/{agentId}/sync",
                    $"{baseClean}/api/admin/ssl/{agentId}/result"
                };

                var jsonPayload = JsonSerializer.Serialize(items);

                foreach (var url in endpoints)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json")
                        };
                        if (!string.IsNullOrEmpty(token))
                        {
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        }

                        var resp = await _httpClient.SendAsync(req, ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"[ProxySyncService] Relatório SSL de {items.Count} domínio(s) sincronizado com sucesso na API ({url}).");
                            return items.Count;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ProxySyncService] Falha ao enviar sync de SSL para {url}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProxySyncService] Erro ao sincronizar status SSL com a API: {ex.Message}");
            }

            return 0;
        }
    }
}
