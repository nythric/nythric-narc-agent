using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NeskAgent.Command.Interfaces;
using NeskAgent.Command.Models;
using NeskAgent.Proxy.Services.Nginx;

namespace NeskAgent.Proxy
{
    public class ProxyPlugin : IAgentPlugin
    {
        private readonly NginxService _nginxService;

        public ProxyPlugin(NginxService nginxService)
        {
            _nginxService = nginxService;
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
            "save_ssl_files"
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
                _ => CommandResult.Error($"Unknown action: {action}")
            };
        }

        private async Task<CommandResult> HandleUpdateProxyAsync(JsonDocument command, CancellationToken ct)
        {
            var root = command.RootElement;
            var proxyId = root.GetProperty("id").GetString()!;
            var domain = root.GetProperty("domain").GetString()!;
            var targetHost = root.GetProperty("target_host").GetString()!;
            var targetPort = root.GetProperty("target_port").GetInt32();
            var enabled = root.GetProperty("enabled").GetBoolean();
            bool? sslAvailable = root.TryGetProperty("ssl_available", out var ssl) ? ssl.GetBoolean() : null;

            await _nginxService.UpdateProxyAsync(proxyId, domain, targetHost, targetPort, enabled, sslAvailable, ct);
            return CommandResult.Ack("Proxy updated successfully.");
        }

        private async Task<CommandResult> HandleGetConfigAsync(JsonDocument command, CancellationToken ct)
        {
            var proxyId = command.RootElement.GetProperty("id").GetString()!;
            var config = await _nginxService.GetConfigAsync(proxyId, ct);
            return CommandResult.Content(config);
        }

        private async Task<CommandResult> HandleSaveConfigAsync(JsonDocument command, CancellationToken ct)
        {
            var filename = command.RootElement.GetProperty("filename").GetString()!;
            var content = command.RootElement.GetProperty("content").GetString()!;
            await _nginxService.SaveRawConfigAsync(filename, content, ct);
            return CommandResult.Ack("Config saved successfully.");
        }

        private async Task<CommandResult> HandleDeleteProxyAsync(JsonDocument command, CancellationToken ct)
        {
            var proxyId = command.RootElement.GetProperty("id").GetString()!;
            var domain = command.RootElement.GetProperty("domain").GetString()!;
            await _nginxService.DeleteProxyAsync(proxyId, domain, ct);
            return CommandResult.Ack("Proxy deleted successfully.");
        }

        private async Task<CommandResult> HandleToggleConfigAsync(JsonDocument command, CancellationToken ct)
        {
            var root = command.RootElement;
            var proxyId = root.GetProperty("id").GetString()!;
            var domain = root.GetProperty("domain").GetString()!;
            var targetHost = root.GetProperty("target_host").GetString()!;
            var targetPort = root.GetProperty("target_port").GetInt32();
            var active = root.GetProperty("active").GetBoolean();
            bool? sslAvailable = root.TryGetProperty("ssl_available", out var ssl) ? ssl.GetBoolean() : null;

            await _nginxService.ToggleProxyAsync(proxyId, domain, targetHost, targetPort, active, sslAvailable, ct);
            return CommandResult.Ack($"Proxy {(active ? "enabled" : "disabled")} successfully.");
        }

        private async Task<CommandResult> HandleGenerateSslAsync(JsonDocument command, CancellationToken ct)
        {
            var domain = command.RootElement.GetProperty("domain").GetString()!;
            var email = command.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;

            Console.WriteLine($"[ProxyPlugin] Recebido generate_ssl para dominio: {domain}");

            // Usa o CancellationTokenSource estatico (nao o ct da requisicao) para
            // que o certbot nao seja cancelado quando a resposta Async for enviada
            // e o token de cancelamento da requisicao for descartado.
            _ = Task.Run(async () =>
            {
                try
                {
                    var success = await _nginxService.GenerateSslAsync(domain, email, _sslLoopCts.Token);
                    Console.WriteLine($"[ProxyPlugin] Resultado do certbot para {domain}: {(success ? "OK" : "FALHOU")}");

                    // Notifica a API sobre o resultado, para ela marcar ssl_issued
                    // (e reenviar update_proxy com ssl_available=true)
                    await NotifySslResultAsync(domain, success, _sslLoopCts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ProxyPlugin] Erro no generate_ssl em background para {domain}: {ex.Message}");
                    await NotifySslResultAsync(domain, false, _sslLoopCts.Token);
                }
            }, _sslLoopCts.Token);

            return CommandResult.Async($"SSL generation started for {domain}.");
        }

        private async Task<CommandResult> HandleDeleteSslAsync(JsonDocument command, CancellationToken ct)
        {
            var domain = command.RootElement.GetProperty("domain").GetString()!;
            var success = await _nginxService.DeleteSslAsync(domain, ct);
            return success
                ? CommandResult.Ack($"SSL deleted for {domain}.")
                : CommandResult.Error($"Failed to delete SSL for {domain}.");
        }

        /// <summary>
        /// POST de retorno para a API indicando se o certbot deu certo.
        /// O painel depende disso pra atualizar o badge ssl_issued.
        /// </summary>
        private async Task NotifySslResultAsync(string domain, bool success, CancellationToken ct)
        {
            try
            {
                var apiUrl = Environment.GetEnvironmentVariable("API_CENTRAL_URL")
                    ?.Replace("wss://", "https://").Replace("ws://", "http://")
                    .TrimEnd('/') ?? "";
                if (string.IsNullOrEmpty(apiUrl)) return;

                var agentId = Environment.GetEnvironmentVariable("AGENT_ID") ?? "";
                var token = Environment.GetEnvironmentVariable("AGENT_TOKEN"); // opcional

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var body = new { domain, success, message = success ? "Certbot OK" : "Certbot falhou" };
                var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/admin/ssl/{agentId}/result")
                {
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(body),
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                await client.SendAsync(req, ct);
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
                var certPath = $"/etc/letsencrypt/live/{domain}/fullchain.pem";
                var keyPath = $"/etc/letsencrypt/live/{domain}/privkey.pem";

                // Garante diretorio
                System.IO.Directory.CreateDirectory($"/etc/letsencrypt/live/{domain}");

                // Backup dos existentes (se houver)
                if (System.IO.File.Exists(certPath))
                {
                    var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    System.IO.File.Copy(certPath, $"{certPath}.{ts}.bak", overwrite: true);
                    System.IO.File.Copy(keyPath, $"{keyPath}.{ts}.bak", overwrite: true);
                }

                await System.IO.File.WriteAllTextAsync(certPath, cert, ct);
                await System.IO.File.WriteAllTextAsync(keyPath, key, ct);
                TryChmod(certPath, "644");
                TryChmod(keyPath, "640");

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
    }
}
