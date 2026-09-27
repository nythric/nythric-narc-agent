using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NarcAgent.Proxy.Services.Nginx
{
    public class NginxConfigService
    {
        private readonly string _configPath;
        private const string MaintenanceFilePath = "/var/www/html/narc_deactivated.html";

        public NginxConfigService(string configPath = "/etc/nginx/conf.d")
        {
            _configPath = configPath;
        }

        public void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_configPath))
            {
                Directory.CreateDirectory(_configPath);
            }
        }

        /// <summary>
        /// Cria o HTML estatico "Proxy desativado" usado pelas configs port 80/443
        /// quando o proxy esta desligado. Idempotente: so escreve se nao existir.
        /// </summary>
        public void EnsureMaintenanceFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(MaintenanceFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(MaintenanceFilePath))
                {
                    File.WriteAllText(MaintenanceFilePath, MaintenanceHtml);
                    TryChmod(MaintenanceFilePath, "644");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NginxConfigService] Erro ao criar maintenance file: {ex.Message}");
            }
        }

        // IMPORTANTE: em C# verbatim string (@"..."), aspas duplas sao escapadas como "".
        private const string MaintenanceHtml = @"<!doctype html>
<html lang=""pt-BR"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Proxy desativado | Narc Agent</title>
<style>
:root{--bg:#0D1117;--panel:#1A1F27;--border:#2A303C;--text:#E2E8F0;--muted:rgba(226,232,240,.65);--primary:#3A5FD0;--bad:#E94560;--radius:10px}
*{box-sizing:border-box}
html,body{height:100%}
body{margin:0;font-family:-apple-system,BlinkMacSystemFont,""Segoe UI"",Roboto,Helvetica,Arial,sans-serif;background:var(--bg);color:var(--text);display:flex;align-items:center;justify-content:center;min-height:100vh;padding:24px}
.card{background:var(--panel);border:1px solid var(--border);border-radius:var(--radius);padding:42px 48px;max-width:560px;text-align:center}
h1{margin:0 0 12px;font-size:32px;font-weight:400;letter-spacing:-.02em}
.badge{display:inline-block;padding:6px 12px;border-radius:999px;background:rgba(233,69,96,.12);color:var(--bad);font-size:13px;font-weight:600;margin-bottom:18px}
p{color:var(--muted);line-height:1.6;margin:8px 0}
code{background:rgba(255,255,255,.05);padding:2px 8px;border-radius:6px;color:var(--text);font-size:14px}
</style>
</head>
<body>
<div class=""card"">
<div class=""badge"">PROXY DESATIVADO</div>
<h1>Este site esta temporariamente fora do ar</h1>
<p>O proxy foi desativado pelo administrador no painel Narc.</p>
<p>Dominio: <code id=""host""></code></p>
</div>
<script>document.getElementById(""host"").textContent=location.hostname;</script>
</body>
</html>";

        private static void TryChmod(string path, string permissions)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"{permissions} {path}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                p?.WaitForExit();
            }
            catch { }
        }

        public async Task SaveConfigAsync(string filename, string content)
        {
            var path = Path.Combine(_configPath, filename);
            await File.WriteAllTextAsync(path, content);
            TryChmod(path, "644");
        }

        public async Task<string> GetConfigAsync(string filename)
        {
            var path = Path.Combine(_configPath, filename);
            return await File.ReadAllTextAsync(path);
        }

        public Task DeleteConfigAsync(string filename)
        {
            var path = Path.Combine(_configPath, filename);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return Task.CompletedTask;
        }

        public async Task BackupAsync(string filename)
        {
            var path = Path.Combine(_configPath, filename);
            var backupPath = path + ".bak";
            if (File.Exists(path))
            {
                await Task.Run(() => File.Copy(path, backupPath, overwrite: true));
            }
        }

        public async Task RestoreAsync(string filename)
        {
            var path = Path.Combine(_configPath, filename);
            var backupPath = path + ".bak";
            if (File.Exists(backupPath))
            {
                await Task.Run(() => File.Copy(backupPath, path, overwrite: true));
            }
        }

        public Task DeleteBackupAsync(string filename)
        {
            var backupPath = Path.Combine(_configPath, filename + ".bak");
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            return Task.CompletedTask;
        }

        public bool ConfigExists(string filename)
        {
            return File.Exists(Path.Combine(_configPath, filename));
        }

        public bool BackupExists(string filename)
        {
            return File.Exists(Path.Combine(_configPath, filename + ".bak"));
        }

        /// <summary>
        /// Busca o filename de um proxy a partir do dominio (server_name), escaneando
        /// os configs existentes. Usado quando a API manda get_config apenas com "domain"
        /// (fallback quando o filename pedido nao segue o padrao narc_proxy_{id}.conf).
        /// </summary>
        public async Task<string?> FindFilenameByDomainAsync(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain) || !Directory.Exists(_configPath))
                return null;

            // 1. Procura em narc_proxy_*.conf por server_name
            foreach (var file in Directory.GetFiles(_configPath, "narc_proxy_*.conf"))
            {
                if (await MatchesDomainAsync(file, domain))
                    return Path.GetFileName(file);
            }

            // 2. Procura em qualquer *.conf por server_name (ex: dominios manuais ou legados)
            foreach (var file in Directory.GetFiles(_configPath, "*.conf"))
            {
                if (await MatchesDomainAsync(file, domain))
                    return Path.GetFileName(file);
            }

            // 3. Fallback: correspondência direta por nome de arquivo comum
            var directCandidates = new[]
            {
                $"narc_proxy_{domain}.conf",
                $"{domain}.conf"
            };

            foreach (var candidate in directCandidates)
            {
                if (File.Exists(Path.Combine(_configPath, candidate)))
                    return candidate;
            }

            return null;
        }

        private static async Task<bool> MatchesDomainAsync(string file, string domain)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                var matches = Regex.Matches(content, @"\bserver_name\s+([^;]+);");
                foreach (Match match in matches)
                {
                    var names = match.Groups[1].Value.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (Array.Exists(names, n => string.Equals(n, domain, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Recupera o dominio (server_name) parseado de um arquivo de config existente.
        /// Usado como fallback quando o painel manda delete_proxy sem o campo domain.
        /// </summary>
        public async Task<string?> GetDomainFromConfigAsync(string proxyId)
        {
            var filename = proxyId.StartsWith("narc_proxy_") ? proxyId : $"narc_proxy_{proxyId}";
            if (!filename.EndsWith(".conf")) filename += ".conf";

            var path = Path.Combine(_configPath, filename);
            if (!File.Exists(path)) return null;

            try
            {
                var content = await File.ReadAllTextAsync(path);
                var match = Regex.Match(content, @"server_name\s+(.+?);");
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim().Split(' ', StringSplitOptions.None)[0];
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NginxConfigService] Falha ao ler dominio: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Remove configs cujo ssl_certificate aponta para um arquivo inexistente.
        /// Chamado automaticamente apos falha de reload (NginxOrphanConfigException).
        /// </summary>
        public async Task CleanupOrphanConfigsAsync()
        {
            try
            {
                if (!Directory.Exists(_configPath)) return;

                var files = Directory.GetFiles(_configPath, "*.conf");
                foreach (var file in files)
                {
                    string content;
                    try { content = await File.ReadAllTextAsync(file); }
                    catch { continue; }

                    var match = Regex.Match(content, @"ssl_certificate\s+(.+?);");
                    if (!match.Success) continue;

                    var certPath = match.Groups[1].Value.Trim();
                    if (!File.Exists(certPath))
                    {
                        Console.WriteLine($"[NginxConfigService] Removendo orphan config {Path.GetFileName(file)} (cert ausente: {certPath})");
                        File.Delete(file);
                        var backup = file + ".bak";
                        if (File.Exists(backup)) File.Delete(backup);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NginxConfigService] Erro limpando orphan configs: {ex.Message}");
            }
        }
    }
}
