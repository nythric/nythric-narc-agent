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
using NarcAgent.Proxy.Models;
using NarcAgent.Proxy.Services;
using NarcAgent.Proxy.Services.Nginx;

namespace NarcAgent.Proxy
{
    public class ProxyPlugin : IAgentPlugin
    {
        private readonly NginxService _nginxService;
        private readonly ProxySyncService _syncService;

        private static bool _loopStarted = false;
        private static readonly object _loopLock = new();

        public ProxyPlugin(NginxService nginxService)
        {
            _nginxService = nginxService;
            _syncService = new ProxySyncService(_nginxService);
            EnsureBackgroundLoopsStarted();
        }

        public ProxyPlugin()
        {
            _nginxService = new NginxService(
                new NginxConfigService(),
                new NginxConfigGenerator(),
                new NginxProcessService(),
                new NginxSslService()
            );
            _syncService = new ProxySyncService(_nginxService);
            EnsureBackgroundLoopsStarted();
        }

        public ProxySyncService SyncService => _syncService;

        private void EnsureBackgroundLoopsStarted()
        {
            lock (_loopLock)
            {
                if (!_loopStarted)
                {
                    _loopStarted = true;
                    _ = SslRenewalLoopAsync(_sslLoopCts.Token);
                    _ = BackgroundSyncLoopAsync(_sslLoopCts.Token);
                }
            }
        }

        private static readonly CancellationTokenSource _sslLoopCts = new();

        public IReadOnlySet<string> SupportedActions => new HashSet<string>
        {
            "update_proxy",
            "get_config",
            "save_config",
            "delete_proxy",
            "toggle_config",
            "generate_ssl",
            "delete_ssl",
            "save_ssl_files",
            "renew_ssl",
            "check_ssl",
            "sync_proxies",
            "clean_orphans",
            "verify_configs"
        };

        public async Task<CommandResult> ExecuteAsync(JsonDocument command, CancellationToken ct)
        {
            var action = command.RootElement.GetProperty("action").GetString();
            return action switch
            {
                "update_proxy" => await HandleUpdateProxyAsync(command, ct),
                "get_config" => await HandleGetConfigAsync(command, ct),
                "save_config" => await HandleSaveConfigAsync(command, ct),
                "delete_proxy" => await HandleDeleteProxyAsync(command, ct),
                "toggle_config" => await HandleToggleConfigAsync(command, ct),
                "generate_ssl" => await HandleGenerateSslAsync(command, ct),
                "delete_ssl" => await HandleDeleteSslAsync(command, ct),
                "save_ssl_files" => await HandleSaveSslFilesAsync(command, ct),
                "renew_ssl" => await HandleRenewSslAsync(command, ct),
                "check_ssl" => await HandleCheckSslAsync(command, ct),
                "sync_proxies" => await HandleSyncProxiesAsync(command, ct),
                "clean_orphans" => await HandleCleanOrphansAsync(command, ct),
                "verify_configs" => await HandleVerifyConfigsAsync(command, ct),
                _ => CommandResult.Error($"Unknown action: {action}")
            };
        }

        private async Task<CommandResult> HandleUpdateProxyAsync(JsonDocument command, CancellationToken ct)
        {
            var root = command.RootElement;
            var proxyId = TryGetString(root, "id", "proxyId", "proxy_id", "filename", "file_name") ?? "";
            var domain = TryGetString(root, "domain") ?? "";
            var targetHost = TryGetString(root, "target_host", "targetHost") ?? "127.0.0.1";
            var targetPort = TryGetInt(root, "target_port", "targetPort") ?? 80;
            var enabled = TryGetBool(root, "enabled", "active");
            bool forceSsl = TryGetBool(root, "force_ssl", "forceSsl", "ssl", "is_ssl") ?? false;
            bool? sslAvailable = TryGetBool(root, "ssl_available", "sslAvailable");

            if (string.IsNullOrEmpty(proxyId) && string.IsNullOrEmpty(domain))
            {
                return CommandResult.Error("update_proxy requer 'id' ou 'domain'.");
            }

            try
            {
                bool certExists = _nginxService.HasCertificate(domain);
                bool effectiveSslAvailable = sslAvailable ?? (forceSsl || certExists);

                // Se enabled foi explicitamente enviado como false, desativa o proxy
                if (enabled.HasValue && !enabled.Value)
                {
                    await _nginxService.ToggleProxyAsync(proxyId, domain, targetHost, targetPort, false, effectiveSslAvailable, ct);
                    return CommandResult.Ack("Proxy disabled successfully.");
                }

                // Se o proxy estava desativado (com backup .bak existente), reativa-o
                var filename = await _nginxService.ResolveConfigFilenameAsync(proxyId, domain);
                if (_nginxService.ConfigService.BackupExists(filename))
                {
                    await _nginxService.ToggleProxyAsync(proxyId, domain, targetHost, targetPort, true, effectiveSslAvailable, ct);
                }

                // Edição Regex In-Place (Modo Simples): Não recria o arquivo inteiro.
                // Lê o .conf original, altera estritamente IP e Porta no location / { e salva.
                await _nginxService.UpdateProxyInPlaceAsync(proxyId, domain, targetHost, targetPort, ct);
                return CommandResult.Ack("Proxy updated successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProxyPlugin] Erro ao atualizar proxy para {domain} ({proxyId}): {ex.Message}");
                return CommandResult.Error($"Erro ao atualizar proxy: {ex.Message}");
            }
        }

        private async Task<CommandResult> HandleGetConfigAsync(JsonDocument command, CancellationToken ct)
        {
            var root = command.RootElement;
            var hasId = root.TryGetProperty("id", out var idProp) && !string.IsNullOrEmpty(idProp.GetString());
            var hasDomain = root.TryGetProperty("domain", out var domainProp) && !string.IsNullOrEmpty(domainProp.GetString());

            try
            {
                if (hasId)
                {
                    var config = await _nginxService.GetConfigAsync(idProp.GetString()!, ct);
                    return CommandResult.Content(config);
                }

                if (hasDomain)
                {
                    var domain = domainProp.GetString()!;
                    var config = await _nginxService.GetConfigByDomainAsync(domain, ct);
                    if (config == null)
                        return CommandResult.Error($"Nenhuma config encontrada para o dominio: {domain}");
                    return CommandResult.Content(config);
                }

                return CommandResult.Error("get_config requer 'id' ou 'domain'.");
            }
            catch (FileNotFoundException)
            {
                return CommandResult.Error("Config não encontrada.");
            }
            catch (DirectoryNotFoundException)
            {
                return CommandResult.Error("Diretório de configurações não encontrado.");
            }
            catch (Exception ex)
            {
                return CommandResult.Error($"Erro ao ler config: {ex.Message}");
            }
        }

        private async Task<CommandResult> HandleSaveConfigAsync(JsonDocument command, CancellationToken ct)
        {
            var root = command.RootElement;
            var filename = TryGetString(root, "filename") ?? "";
            var content = TryGetString(root, "content", "raw_config", "rawConfig") ?? "";
            var domain = TryGetString(root, "domain");
            var targetHost = TryGetString(root, "target_host", "targetHost");
            var targetPort = TryGetInt(root, "target_port", "targetPort");

            if (string.IsNullOrEmpty(filename))
            {
                var proxyId = TryGetString(root, "id", "proxyId", "proxy_id");
                if (!string.IsNullOrEmpty(proxyId))
                {
                    filename = $"narc_proxy_{proxyId}.conf";
                }
            }

            if (string.IsNullOrEmpty(filename))
            {
                return CommandResult.Error("save_config requer 'filename' ou 'id'.");
            }

            try
            {
                await _nginxService.SaveRawConfigAsync(filename, content, domain, targetHost, targetPort, ct);
                return CommandResult.Ack("Config saved and NGINX reloaded successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProxyPlugin] Falha ao salvar raw config para {filename}: {ex.Message}");
                return CommandResult.Error($"Erro ao salvar configuração NGINX: {ex.Message}");
            }
        }

        private async Task<CommandResult> HandleDeleteProxyAsync(JsonDocument command, CancellationToken ct)
        {
            try
            {
                var root = command.RootElement;
                var proxyId = TryGetString(root, "id", "proxyId", "proxy_id", "filename", "file_name") ?? "";
                var domain = TryGetString(root, "domain") ?? "";
                if (string.IsNullOrEmpty(proxyId) && string.IsNullOrEmpty(domain))
                {
                    return CommandResult.Error("delete_proxy requer 'id' ou 'domain'.");
                }
                await _nginxService.DeleteProxyAsync(proxyId, domain, ct);
                return CommandResult.Ack("Proxy deleted successfully.");
            }
            catch (Exception ex)
            {
                return CommandResult.Error($"Erro ao deletar proxy: {ex.Message}");
            }
        }

        private async Task<CommandResult> HandleToggleConfigAsync(JsonDocument command, CancellationToken ct)
        {
            try
            {
                var root = command.RootElement;
                var proxyId = TryGetString(root, "id", "proxyId", "proxy_id", "filename", "file_name") ?? "";
                var domain = TryGetString(root, "domain") ?? "";
                var targetHost = TryGetString(root, "target_host", "targetHost") ?? "127.0.0.1";
                var targetPort = TryGetInt(root, "target_port", "targetPort") ?? 80;
                var active = TryGetBool(root, "active", "enabled") ?? true;
                bool forceSsl = TryGetBool(root, "force_ssl", "forceSsl", "ssl", "is_ssl") ?? false;
                bool? sslAvailable = TryGetBool(root, "ssl_available", "sslAvailable");

                bool certExists = _nginxService.HasCertificate(domain);
                bool effectiveSslAvailable = sslAvailable ?? (forceSsl || certExists);

                await _nginxService.ToggleProxyAsync(proxyId, domain, targetHost, targetPort, active, effectiveSslAvailable, ct);
                return CommandResult.Ack($"Proxy {(active ? "enabled" : "disabled")} successfully.");
            }
            catch (Exception ex)
            {
                return CommandResult.Error($"Erro ao alternar status do proxy: {ex.Message}");
            }
        }

        private async Task<CommandResult> HandleGenerateSslAsync(JsonDocument command, CancellationToken ct)
        {
            var root = command.RootElement;
            var domain = TryGetString(root, "domain") ?? "";
            var proxyId = TryGetString(root, "id", "proxyId", "proxy_id");
            var email = TryGetString(root, "email");

            if (string.IsNullOrWhiteSpace(domain))
            {
                return CommandResult.Error("generate_ssl requer 'domain'.");
            }

            Console.WriteLine($"[ProxyPlugin] Recebido comando generate_ssl para domínio: {domain}");

            try
            {
                // 1. Tenta emitir o certificado
                bool certIssued = await _nginxService.GenerateSslAsync(domain, email, ct);
                if (!certIssued)
                {
                    Console.WriteLine($"[ProxyPlugin] Falha na emissão de SSL para {domain}.");
                    await NotifySslResultAsync(domain, false, ct);
                    return CommandResult.Error($"Falha na emissão do certificado SSL para o domínio: {domain}");
                }

                // 2. Se der sucesso, abre o .conf, procura (via Regex) onde injetar o ssl_certificate
                // e ssl_certificate_key dentro do server block da porta 443, injeta (se não tiver) e salva.
                bool configured = await _nginxService.ConfigureSslInPlaceAsync(domain, proxyId, ct);
                if (!configured)
                {
                    Console.WriteLine($"[ProxyPlugin] Certificado emitido, mas falha ao configurar Nginx para {domain}.");
                    await NotifySslResultAsync(domain, false, ct);
                    return CommandResult.Error($"Certificado SSL gerado, mas ocorreu erro ao atualizar a configuração Nginx para {domain}");
                }

                // 3. Notifica a API sobre o sucesso
                await NotifySslResultAsync(domain, true, ct);

                var exp = NginxSslService.GetCertificateExpiration(domain);
                string? expStr = exp.HasValue ? exp.Value.ToString("yyyy-MM-dd HH:mm:ss") : null;
                var payloadObj = System.Text.Json.JsonSerializer.Serialize(new
                {
                    domain,
                    success = true,
                    ssl_issued = true,
                    ssl_expires_at = expStr
                });

                return new CommandResult(true, $"SSL gerado e configurado com sucesso para {domain}.", payloadObj, CommandResultKind.Ack);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProxyPlugin] Erro durante generate_ssl para {domain}: {ex.Message}");
                await NotifySslResultAsync(domain, false, ct);
                return CommandResult.Error($"Erro na geração de SSL para {domain}: {ex.Message}");
            }
        }

        private async Task<CommandResult> HandleDeleteSslAsync(JsonDocument command, CancellationToken ct)
        {
            try
            {
                var domain = TryGetString(command.RootElement, "domain") ?? "";
                if (string.IsNullOrEmpty(domain))
                {
                    return CommandResult.Error("delete_ssl requer 'domain'.");
                }
                var success = await _nginxService.DeleteSslAsync(domain, ct);
                return success
                    ? CommandResult.Ack($"SSL deleted for {domain}.")
                    : CommandResult.Error($"Failed to delete SSL for {domain}.");
            }
            catch (Exception ex)
            {
                return CommandResult.Error($"Erro ao deletar SSL: {ex.Message}");
            }
        }

        /// <summary>
        /// POST de retorno para a API indicando se a geracao de SSL deu certo.
        /// O painel depende disso pra atualizar o badge ssl_issued.
        /// </summary>
        private async Task NotifySslResultAsync(string domain, bool success, CancellationToken ct)
        {
            try
            {
                var apiUrl = Environment.GetEnvironmentVariable("API_CENTRAL_URL")
                    ?.Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                    .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                    .TrimEnd('/') ?? "";
                if (string.IsNullOrEmpty(apiUrl)) apiUrl = "https://agent.narc.fun";
                if (!apiUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !apiUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    apiUrl = "https://" + apiUrl;
                }

                var agentId = Environment.GetEnvironmentVariable("AGENT_ID") ?? "";
                // O token dinamico de autenticacao (obtido no /conectar e renovado periodicamente)
                // fica em AgentAuthContext, nao em env var - AGENT_TOKEN nunca era definida,
                // entao esse callback voltava 401 silenciosamente (HttpClient nao lanca em non-2xx).
                var token = NarcAgent.Core.Services.AgentAuthContext.CurrentToken;

                var exp = NginxSslService.GetCertificateExpiration(domain);
                string? expStr = exp.HasValue ? exp.Value.ToString("yyyy-MM-dd HH:mm:ss") : null;

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var body = new
                {
                    domain,
                    success,
                    ssl_issued = success,
                    ssl_expires_at = expStr,
                    message = success ? "SSL OK" : "SSL falhou"
                };
                var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/admin/ssl/{agentId}/result")
                {
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(body),
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                else
                    Console.WriteLine("[ProxyPlugin] Aviso: nenhum token disponivel para notificar resultado de SSL.");

                var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var responseBody = await resp.Content.ReadAsStringAsync(ct);
                    Console.WriteLine($"[ProxyPlugin] API rejeitou notificacao de SSL ({(int)resp.StatusCode}): {responseBody}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProxyPlugin] Falha ao notificar API sobre SSL: {ex.Message}");
            }
        }

        private async Task<CommandResult> HandleSaveSslFilesAsync(JsonDocument command, CancellationToken ct)
        {
            var domain = command.RootElement.GetProperty("domain").GetString()!;
            var cert = command.RootElement.GetProperty("cert").GetString()!;
            var key = command.RootElement.GetProperty("key").GetString()!;

            // Validacao basica: precisa ter BEGIN/END dos PEMs
            if (!cert.Contains("BEGIN CERTIFICATE") || !key.Contains("PRIVATE KEY"))
            {
                return CommandResult.Error($"Certificado invalido para {domain}: PEM malformado");
            }

            try
            {
                // Salva em /etc/narc/ssl/{domain}
                var narcDir = $"/etc/narc/ssl/{domain}";
                System.IO.Directory.CreateDirectory(narcDir);
                var narcCertPath = System.IO.Path.Combine(narcDir, $"{domain}.crt");
                var narcKeyPath = System.IO.Path.Combine(narcDir, $"{domain}.key");
                var narcFullchainPath = System.IO.Path.Combine(narcDir, "fullchain.crt");
                var narcPrivkeyPath = System.IO.Path.Combine(narcDir, "privkey.key");

                await System.IO.File.WriteAllTextAsync(narcCertPath, cert, ct);
                await System.IO.File.WriteAllTextAsync(narcKeyPath, key, ct);
                await System.IO.File.WriteAllTextAsync(narcFullchainPath, cert, ct);
                await System.IO.File.WriteAllTextAsync(narcPrivkeyPath, key, ct);
                TryChmod(narcCertPath, "644");
                TryChmod(narcKeyPath, "600");
                TryChmod(narcFullchainPath, "644");
                TryChmod(narcPrivkeyPath, "600");

                // Testa config antes de aceitar (evita nginx quebrar)
                var (reloadSuccess, errorMsg) = await _nginxService.TestAndReloadAsync(ct);
                if (!reloadSuccess)
                {
                    return CommandResult.Error($"SSL salvo mas nginx falhou ao validar: {errorMsg}. Verifique a config e faca reload manual.");
                }

                return CommandResult.Ack($"SSL files salvos e nginx reloaded para {domain}");
            }
            catch (Exception ex)
            {
                return CommandResult.Error($"Erro ao salvar SSL: {ex.Message}");
            }
        }

        private async Task<CommandResult> HandleRenewSslAsync(JsonDocument command, CancellationToken ct)
        {
            var root = command.RootElement;
            var domain = TryGetString(root, "domain");

            if (!string.IsNullOrEmpty(domain))
            {
                Console.WriteLine($"[ProxyPlugin] Recebida solicitação manual de renovação para {domain}...");
                var email = TryGetString(root, "email");
                bool success = await _nginxService.GenerateSslAsync(domain, email, ct);
                if (success)
                {
                    await _nginxService.ConfigureSslInPlaceAsync(domain, null, ct);
                    await NotifySslResultAsync(domain, true, ct);
                    return CommandResult.Ack($"Certificado SSL para {domain} renovado com sucesso!");
                }
                return CommandResult.Error($"Falha ao renovar certificado SSL para {domain}");
            }

            int renewed = await CheckAndRenewCertificatesAsync(ct);
            return CommandResult.Ack($"Verificação de renovação concluída. Total de certificados renovados: {renewed}");
        }

        private Task<CommandResult> HandleCheckSslAsync(JsonDocument command, CancellationToken ct)
        {
            var domains = _nginxService.SslService.GetAllManagedDomains();
            var list = new List<object>();

            foreach (var d in domains)
            {
                var exp = NginxSslService.GetCertificateExpiration(d);
                var daysRemaining = exp.HasValue ? Math.Round((exp.Value - DateTime.UtcNow).TotalDays, 1) : 0;
                var needsRenewal = _nginxService.SslService.NeedsRenewal(d, 30);

                list.Add(new
                {
                    domain = d,
                    expires_at = exp?.ToString("o"),
                    days_remaining = daysRemaining,
                    needs_renewal = needsRenewal
                });
            }

            return Task.FromResult(CommandResult.Content(JsonSerializer.Serialize(list)));
        }

        private async Task<CommandResult> HandleSyncProxiesAsync(JsonDocument command, CancellationToken ct)
        {
            var res = await _syncService.SyncAndReconcileAsync(ct: ct);
            return res.Success
                ? CommandResult.Ack($"Reconciliação concluída: {res.VerifiedConfigs} verificados, {res.SslInjectedConfigs} SSL ajustados, {res.RemovedOrphans} órfãos removidos.")
                : CommandResult.Error($"Reconciliação falhou: {string.Join("; ", res.Errors)}");
        }

        private async Task<CommandResult> HandleCleanOrphansAsync(JsonDocument command, CancellationToken ct)
        {
            var res = await _syncService.SyncAndReconcileAsync(ct: ct);
            return CommandResult.Ack($"Limpeza concluída. {res.RemovedOrphans} arquivos órfãos removidos.");
        }

        private async Task<CommandResult> HandleVerifyConfigsAsync(JsonDocument command, CancellationToken ct)
        {
            var (verified, fixedCount, success, error) = await _nginxService.VerifyLocalConfigsAsync(ct);
            return success
                ? CommandResult.Ack($"Verificação local concluída: {verified} configs inspecionados, {fixedCount} SSL injetados.")
                : CommandResult.Error($"Falha na verificação: {error}");
        }

        private async Task BackgroundSyncLoopAsync(CancellationToken ct)
        {
            // Aguarda 15 segundos para a conexão e autenticação se estabilizarem
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (TaskCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Console.WriteLine("[Sync Worker] Executando verificação e sincronização periódica de proxies...");
                    var res = await _syncService.SyncAndReconcileAsync(ct: ct);
                    if (res.Success)
                    {
                        Console.WriteLine($"[Sync Worker] Sincronização concluída: {res.VerifiedConfigs} válidos, {res.SslInjectedConfigs} SSL injetados, {res.RemovedOrphans} órfãos removidos.");
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Sync Worker] Erro durante sincronização periódica: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), ct);
                }
                catch (TaskCanceledException) { break; }
            }
        }

        private async Task SslRenewalLoopAsync(CancellationToken ct)
        {
            // Aguarda 2 minutos após o boot para o agente estabilizar
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), ct);
            }
            catch (TaskCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Console.WriteLine("[SSL Renewal Worker] Verificando certificados prestes a expirar em /etc/narc/ssl...");
                    await CheckAndRenewCertificatesAsync(ct);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SSL Renewal Worker] Erro no ciclo periódico: {ex.Message}");
                }

                var hours = 12;
                if (int.TryParse(Environment.GetEnvironmentVariable("SSL_RENEWAL_INTERVAL_HOURS"), out var customH) && customH > 0)
                {
                    hours = customH;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromHours(hours), ct);
                }
                catch (TaskCanceledException) { break; }
            }
        }

        public async Task<int> CheckAndRenewCertificatesAsync(CancellationToken ct)
        {
            var domains = _nginxService.SslService.GetAllManagedDomains();
            int renewedCount = 0;

            foreach (var domain in domains)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var exp = NginxSslService.GetCertificateExpiration(domain);
                    if (!exp.HasValue) continue;

                    var daysRemaining = (exp.Value - DateTime.UtcNow).TotalDays;
                    Console.WriteLine($"[SSL Renewal Worker] Domínio '{domain}': expira em {exp.Value:yyyy-MM-dd HH:mm:ss} UTC ({daysRemaining:F1} dias restantes).");

                    if (_nginxService.SslService.NeedsRenewal(domain, 30))
                    {
                        Console.WriteLine($"[SSL Renewal Worker] Domínio '{domain}' próximo do vencimento (<= 30 dias). Renovando automaticamente...");
                        bool success = await _nginxService.GenerateSslAsync(domain, null, ct);
                        if (success)
                        {
                            await _nginxService.ConfigureSslInPlaceAsync(domain, null, ct);
                            await NotifySslResultAsync(domain, true, ct);
                            renewedCount++;
                            Console.WriteLine($"[SSL Renewal Worker] Domínio '{domain}' renovado com sucesso!");
                        }
                        else
                        {
                            Console.WriteLine($"[SSL Renewal Worker] Falha ao renovar domínio '{domain}'.");
                            await NotifySslResultAsync(domain, false, ct);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SSL Renewal Worker] Erro ao processar '{domain}': {ex.Message}");
                }
            }

            return renewedCount;
        }

        /// <summary>
        /// Aplica chmod em path. Usa /bin/chmod via shell (compativel com Linux;
        /// no Windows apenas nao faz nada, o agent roda em VPS Linux).
        /// </summary>
        private static void TryChmod(string path, string permissions)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return;
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/chmod",
                    Arguments = $"{permissions} {path}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                p?.WaitForExit(2000);
            }
            catch { /* silencioso: falha de chmod nao bloqueia operacao */ }
        }

        private static bool? TryGetBool(JsonElement element, params string[] propertyNames)
        {
            foreach (var propName in propertyNames)
            {
                if (element.TryGetProperty(propName, out var prop))
                {
                    switch (prop.ValueKind)
                    {
                        case JsonValueKind.True:
                            return true;
                        case JsonValueKind.False:
                            return false;
                        case JsonValueKind.Number:
                            return prop.GetInt32() != 0;
                        case JsonValueKind.String:
                            var str = prop.GetString()?.Trim().ToLowerInvariant();
                            if (str == "true" || str == "1" || str == "yes" || str == "t") return true;
                            if (str == "false" || str == "0" || str == "no" || str == "f") return false;
                            break;
                    }
                }
            }
            return null;
        }

        private static string? TryGetString(JsonElement element, params string[] propertyNames)
        {
            foreach (var propName in propertyNames)
            {
                if (element.TryGetProperty(propName, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.String)
                    {
                        return prop.GetString();
                    }
                    if (prop.ValueKind != JsonValueKind.Null && prop.ValueKind != JsonValueKind.Undefined)
                    {
                        return prop.ToString();
                    }
                }
            }
            return null;
        }

        private static int? TryGetInt(JsonElement element, params string[] propertyNames)
        {
            foreach (var propName in propertyNames)
            {
                if (element.TryGetProperty(propName, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32();
                    if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var val)) return val;
                }
            }
            return null;
        }
    }
}
