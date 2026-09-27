using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Proxy.Services.Nginx.Exceptions;

namespace NarcAgent.Proxy.Services.Nginx
{
    public class NginxProcessService
    {
        private readonly string _nginxPath;

        public NginxProcessService(string nginxPath = "/usr/sbin/nginx")
        {
            _nginxPath = nginxPath;
        }

        /// <summary>
        /// Executa nginx -t e retorna o status de sucesso e a saída de erro / diagnóstico.
        /// </summary>
        public async Task<(bool Success, string Output)> TestConfigurationAsync(CancellationToken ct)
        {
            var result = await RunProcessAsync($"{_nginxPath} -t", ct);
            return (result.ExitCode == 0, string.IsNullOrWhiteSpace(result.Stderr) ? "" : result.Stderr);
        }

        /// <summary>
        /// Testa a config (nginx -t), detecta erros de "cannot load certificate"
        /// (orphan SSL config) e lanca NginxOrphanConfigException para que
        /// NginxService possa limpar e tentar novamente.
        /// </summary>
        public async Task<bool> TestAsync(CancellationToken ct)
        {
            var result = await RunProcessAsync($"{_nginxPath} -t", ct);
            if (result.ExitCode == 0) return true;

            // Detecta "orphan config" - cert SSL sumiu mas a config ainda referencia ele
            if (result.Stderr.Contains("cannot load certificate", StringComparison.OrdinalIgnoreCase) ||
                result.Stderr.Contains("BIO_new_file() failed", StringComparison.OrdinalIgnoreCase) ||
                (result.Stderr.Contains("ssl_certificate", StringComparison.OrdinalIgnoreCase) && result.Stderr.Contains("failed", StringComparison.OrdinalIgnoreCase)))
            {
                throw new NginxOrphanConfigException("(test)", "", "Orphan SSL config detectada em nginx -t");
            }
            return false;
        }

        /// <summary>
        /// Recarrega o Nginx. Internamente faz test + reload e propaga orphan exception.
        /// </summary>
        public async Task<bool> ReloadAsync(CancellationToken ct)
        {
            // Primeiro valida a config
            await TestAsync(ct);

            // Se passou no test, recarrega
            var result = await RunProcessAsync($"{_nginxPath} -s reload", ct);
            return result.ExitCode == 0;
        }

        private async Task<ProcessResult> RunProcessAsync(string command, CancellationToken ct)
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

            process.Start();
            var errTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await errTask;
            return new ProcessResult(process.ExitCode, stderr);
        }

        private readonly struct ProcessResult
        {
            public int ExitCode { get; }
            public string Stderr { get; }
            public ProcessResult(int exitCode, string stderr)
            {
                ExitCode = exitCode;
                Stderr = stderr;
            }
        }
    }
}
