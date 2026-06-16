using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NeskAgent.Proxy.Services.Nginx
{
    public class NginxSslService
    {
        /// <summary>
        /// Checks if a Let's Encrypt certificate exists for the given domain.
        /// </summary>
        public bool HasCertificate(string domain)
        {
            return File.Exists($"/etc/letsencrypt/live/{domain}/fullchain.pem");
        }

        public async Task<bool> GenerateAsync(string domain, string? email = null, CancellationToken ct = default)
        {
            // Se tem email valido, usa --email (registro normal). Caso contrario usa
            // --register-unsafely-without-email (gambiarra para dominios de teste).
            string emailArg;
            if (!string.IsNullOrWhiteSpace(email) && email.Contains('@'))
            {
                emailArg = $"--email {email}";
            }
            else
            {
                emailArg = "--register-unsafely-without-email";
            }

            // --nginx edita a config do nginx automaticamente
            // --non-interactive = sem prompt
            // --agree-tos = aceita ToS
            // --no-redirect = nao tenta forcar HTTPS (deixa o painel decidir)
            // Usa sudo pois o certbot precisa escrever em /etc/letsencrypt
            var command = $"sudo certbot --nginx -d {domain} --cert-name {domain} --non-interactive --agree-tos {emailArg} --no-redirect";
            return await RunCertbotAsync(command, domain, ct);
        }

        public async Task<bool> DeleteAsync(string domain, CancellationToken ct = default)
        {
            var command = $"sudo certbot delete --cert-name {domain} --non-interactive";
            return await RunCertbotAsync(command, domain, ct);
        }

        private async Task<bool> RunCertbotAsync(string command, string domain, CancellationToken ct = default)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SSL] [{domain}] Falha ao iniciar certbot: {ex.Message}");
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SSL] [{domain}] Erro aguardando certbot: {ex.Message}");
                try { process.Kill(true); } catch { }
                return false;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine($"[SSL] [{domain}] STDOUT: {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.WriteLine($"[SSL] [{domain}] STDERR: {stderr.Trim()}");

            if (process.ExitCode == 0)
            {
                Console.WriteLine($"[SSL] [{domain}] Certbot executado com sucesso (exit=0)");
                return true;
            }

            Console.WriteLine($"[SSL] [{domain}] Certbot falhou (exit={process.ExitCode})");
            return false;
        }
    }
}
