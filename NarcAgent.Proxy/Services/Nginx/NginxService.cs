using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Proxy.Services.Nginx.Exceptions;

namespace NarcAgent.Proxy.Services.Nginx
{
    public class NginxService
    {
        private readonly NginxConfigService _configService;
        private readonly NginxConfigGenerator _configGenerator;
        private readonly NginxProcessService _processService;
        private readonly NginxSslService _sslService;
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public NginxService()
            : this(new NginxConfigService(), new NginxConfigGenerator(), new NginxProcessService(), new NginxSslService())
        {
        }

        public NginxService(
            NginxConfigService configService,
            NginxConfigGenerator configGenerator,
            NginxProcessService processService,
            NginxSslService sslService)
        {
            _configService = configService;
            _configGenerator = configGenerator;
            _processService = processService;
            _sslService = sslService;
        }

        public Task UpdateProxyAsync(string proxyId, string domain, string targetHost, int targetPort, bool enabled, bool? sslAvailable, CancellationToken ct)
            => UpdateProxyAsync(proxyId, domain, targetHost, targetPort, enabled, sslAvailable, null, ct);

        public async Task UpdateProxyAsync(string proxyId, string domain, string targetHost, int targetPort, bool enabled, bool? sslAvailable, string? rawConfig, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                // V2 behavior: if API doesn't specify ssl_available, default to whether cert exists
                bool certExists = !string.IsNullOrEmpty(domain) && _sslService.HasCertificate(domain);
                bool effectiveSslAvailable = sslAvailable ?? certExists;
                bool actualSsl = enabled && effectiveSslAvailable && certExists;

                // Garante diretório e HTML de manutenção antes de qualquer operação
                _configService.EnsureDirectoryExists();
                if (!enabled) _configService.EnsureMaintenanceFile();

                var config = _configGenerator.CompileTemplate(rawConfig, domain, targetHost, targetPort, enabled, actualSsl);
                await _configService.SaveConfigAsync($"narc_proxy_{proxyId}.conf", config);
                await ReloadWithRetryAsync(ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Atualiza o proxy In-Place no modo simples sem recriar o arquivo inteiro.
        /// Lê o .conf original do disco, aplica Regex no location / para alterar
        /// estritamente o IP e a porta no proxy_pass, e salva de volta.
        /// </summary>
        public async Task UpdateProxyInPlaceAsync(string proxyId, string domain, string targetHost, int targetPort, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                _configService.EnsureDirectoryExists();

                var filename = !string.IsNullOrEmpty(proxyId) ? $"narc_proxy_{proxyId}.conf" : null;
                if (filename == null || !_configService.ConfigExists(filename))
                {
                    var found = await _configService.FindFilenameByDomainAsync(domain);
                    if (!string.IsNullOrEmpty(found))
                    {
                        filename = found;
                    }
                }

                if (filename != null && _configService.ConfigExists(filename))
                {
                    var originalConfig = await _configService.GetConfigAsync(filename);
                    var updatedConfig = NginxConfigModifier.UpdateRootProxyPass(originalConfig, targetHost, targetPort);

                    await _configService.SaveConfigAsync(filename, updatedConfig);

                    var (success, error) = await _processService.TestConfigurationAsync(ct);
                    if (!success)
                    {
                        // Reverte para a configuração anterior se nginx -t falhar
                        await _configService.SaveConfigAsync(filename, originalConfig);
                        throw new InvalidOperationException($"nginx -t falhou ao validar configuração: {error}");
                    }

                    await ReloadWithRetryAsync(ct);
                }
                else
                {
                    // Se não existia configuração no disco, gera o arquivo inicial
                    var defaultFilename = !string.IsNullOrEmpty(proxyId) ? $"narc_proxy_{proxyId}.conf" : $"narc_proxy_{domain}.conf";
                    bool certExists = !string.IsNullOrEmpty(domain) && _sslService.HasCertificate(domain);
                    var initialConfig = _configGenerator.CompileTemplate(null, domain, targetHost, targetPort, true, certExists);
                    await _configService.SaveConfigAsync(defaultFilename, initialConfig);
                    await ReloadWithRetryAsync(ct);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Injeta ou atualiza certificados SSL in-place no server block 443 do arquivo .conf
        /// correspondente ao domínio informado, validando a sintaxe e recarregando o Nginx.
        /// </summary>
        public async Task<bool> ConfigureSslInPlaceAsync(string domain, string? proxyId, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                _configService.EnsureDirectoryExists();

                var filename = !string.IsNullOrEmpty(proxyId) ? $"narc_proxy_{proxyId}.conf" : null;
                if (filename == null || !_configService.ConfigExists(filename))
                {
                    var found = await _configService.FindFilenameByDomainAsync(domain);
                    if (!string.IsNullOrEmpty(found))
                    {
                        filename = found;
                    }
                }

                if (filename == null || !_configService.ConfigExists(filename))
                {
                    Console.WriteLine($"[NginxService] Arquivo de configuração não encontrado para {domain} ao configurar SSL.");
                    return false;
                }

                var (certPath, keyPath) = NginxSslService.GetCertificatePaths(domain);
                certPath = certPath.Replace('\\', '/');
                keyPath = keyPath.Replace('\\', '/');

                var originalConfig = await _configService.GetConfigAsync(filename);
                var updatedConfig = NginxConfigModifier.InjectSslIntoConfig(originalConfig, domain, certPath, keyPath);

                await _configService.SaveConfigAsync(filename, updatedConfig);

                var (success, error) = await _processService.TestConfigurationAsync(ct);
                if (!success)
                {
                    Console.WriteLine($"[NginxService] nginx -t falhou após injetar SSL: {error}. Revertendo...");
                    await _configService.SaveConfigAsync(filename, originalConfig);
                    return false;
                }

                await ReloadWithRetryAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NginxService] Erro ao configurar SSL in-place para {domain}: {ex.Message}");
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task DeleteProxyAsync(string proxyId, string domain, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                // Fallback: se domain veio vazio, tenta recuperar do arquivo de config
                if (string.IsNullOrEmpty(domain))
                {
                    domain = await _configService.GetDomainFromConfigAsync(proxyId) ?? "";
                    if (!string.IsNullOrEmpty(domain))
                        Console.WriteLine($"[NginxService] Domínio '{domain}' recuperado da config para deleção.");
                }

                var filename = $"narc_proxy_{proxyId}.conf";
                await _configService.DeleteConfigAsync(filename);
                await _configService.DeleteBackupAsync(filename);
                if (!string.IsNullOrEmpty(domain))
                    await _sslService.DeleteAsync(domain, ct);
                await ReloadWithRetryAsync(ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task ToggleProxyAsync(string proxyId, string domain, string targetHost, int targetPort, bool active, bool? sslAvailable, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                var filename = $"narc_proxy_{proxyId}.conf";
                _configService.EnsureDirectoryExists();

                if (active)
                {
                    await _configService.RestoreAsync(filename);
                    await _configService.DeleteBackupAsync(filename);
                }
                else
                {
                    await _configService.BackupAsync(filename);
                    _configService.EnsureMaintenanceFile();
                    // V2 behavior: if API doesn't specify ssl_available, default to whether cert exists
                    bool certExists = !string.IsNullOrEmpty(domain) && _sslService.HasCertificate(domain);
                    bool effectiveSslAvailable = sslAvailable ?? certExists;
                    bool includeSsl = effectiveSslAvailable && certExists;
                    var config = _configGenerator.Generate(domain, targetHost, targetPort, false, includeSsl);
                    await _configService.SaveConfigAsync(filename, config);
                }
                await ReloadWithRetryAsync(ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string> GetConfigAsync(string proxyId, CancellationToken ct)
        {
            var filename = $"narc_proxy_{proxyId}.conf";
            return await _configService.GetConfigAsync(filename);
        }

        /// <summary>
        /// Busca a config por dominio quando o proxyId nao foi informado
        /// (fallback usado pela API quando o filename nao segue o padrao esperado).
        /// </summary>
        public async Task<string?> GetConfigByDomainAsync(string domain, CancellationToken ct)
        {
            var filename = await _configService.FindFilenameByDomainAsync(domain);
            if (filename == null) return null;
            return await _configService.GetConfigAsync(filename);
        }

        /// <summary>
        /// Atualiza o estado ativado/desativado de um proxy customizado sem sobrescrever o arquivo .conf com template.
        /// Se desativado, o arquivo .conf original é guardado como .conf.bak e uma página de manutenção é servida.
        /// Ao reativar, o arquivo original é restaurado do .bak sem alteração em seu conteúdo interno.
        /// </summary>
        public async Task UpdateCustomProxyStateAsync(string proxyId, string domain, string targetHost, int targetPort, bool enabled, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                var filename = $"narc_proxy_{proxyId}.conf";
                _configService.EnsureDirectoryExists();

                if (enabled)
                {
                    // Se existia backup (.bak) porque o proxy estava desligado, restaura o arquivo original
                    if (_configService.BackupExists(filename))
                    {
                        await _configService.RestoreAsync(filename);
                        await _configService.DeleteBackupAsync(filename);
                    }
                    // Se o arquivo .conf já está lá, preservamos seu conteúdo 100% intacto!
                }
                else
                {
                    // Desativando: se o arquivo .conf existe e ainda não tem backup, preserva o custom em .bak
                    if (_configService.ConfigExists(filename) && !_configService.BackupExists(filename))
                    {
                        await _configService.BackupAsync(filename);
                    }

                    _configService.EnsureMaintenanceFile();
                    bool certExists = !string.IsNullOrEmpty(domain) && _sslService.HasCertificate(domain);
                    var maintConfig = _configGenerator.Generate(domain, targetHost, targetPort, false, certExists);
                    await _configService.SaveConfigAsync(filename, maintConfig);
                }

                await ReloadWithRetryAsync(ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public Task SaveRawConfigAsync(string filename, string content, CancellationToken ct)
            => SaveRawConfigAsync(filename, content, null, null, null, ct);

        public async Task SaveRawConfigAsync(string filename, string content, string? domain, string? targetHost, int? targetPort, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                _configService.EnsureDirectoryExists();

                // Recupera domínio do config caso não tenha sido informado
                if (string.IsNullOrEmpty(domain))
                {
                    domain = await _configService.GetDomainFromConfigAsync(filename) ?? "localhost";
                }
                targetHost ??= "127.0.0.1";
                targetPort ??= 80;

                // Compila os placeholders do template (ex: {{target_host}}, {{target_port}}, {{domain}})
                var compiledContent = _configGenerator.ApplyPlaceholders(content, domain, targetHost, targetPort.Value);

                // Guarda backup temporário em memória caso a validação do nginx -t falhe
                string? previousContent = null;
                if (_configService.ConfigExists(filename))
                {
                    try
                    {
                        previousContent = await _configService.GetConfigAsync(filename);
                    }
                    catch { }
                }

                await _configService.SaveConfigAsync(filename, compiledContent);

                // Valida a sintaxe com nginx -t antes de recarregar
                var (success, error) = await _processService.TestConfigurationAsync(ct);
                if (!success)
                {
                    // Se o erro foi por certificado ausente em config de teste, tenta limpar orphan configs
                    if (error.Contains("cannot load certificate", StringComparison.OrdinalIgnoreCase) ||
                        error.Contains("BIO_new_file() failed", StringComparison.OrdinalIgnoreCase) ||
                        (error.Contains("ssl_certificate", StringComparison.OrdinalIgnoreCase) && error.Contains("failed", StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.WriteLine($"[NginxService] Orphan SSL detectado ao validar raw config para {filename}. Limpando...");
                        await _configService.CleanupOrphanConfigsAsync();
                        var retryTest = await _processService.TestConfigurationAsync(ct);
                        if (retryTest.Success)
                        {
                            await _processService.ReloadAsync(ct);
                            return;
                        }
                        error = retryTest.Output;
                    }

                    // Reverte o arquivo para o estado anterior caso a sintaxe seja inválida
                    if (previousContent != null)
                    {
                        await _configService.SaveConfigAsync(filename, previousContent);
                    }
                    else
                    {
                        await _configService.DeleteConfigAsync(filename);
                    }

                    throw new InvalidOperationException($"nginx -t falhou ao validar configuração: {error}");
                }

                await ReloadWithRetryAsync(ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Tenta recarregar o Nginx. Em caso de falha por "cannot load certificate"
        /// (orphan config), limpa os configs órfãos e tenta novamente.
        /// </summary>
        private async Task ReloadWithRetryAsync(CancellationToken ct)
        {
            try
            {
                await _processService.ReloadAsync(ct);
            }
            catch (NginxOrphanConfigException ex)
            {
                Console.WriteLine($"[NginxService] {ex.Message}. Limpando orphan configs...");
                await _configService.CleanupOrphanConfigsAsync();
                await _processService.ReloadAsync(ct);
            }
        }

        public async Task<bool> GenerateSslAsync(string domain, string? email, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                var challengeDir = "/var/www/html/.well-known/acme-challenge";
                if (!Directory.Exists(challengeDir))
                {
                    Directory.CreateDirectory(challengeDir);
                }

                var filename = await _configService.FindFilenameByDomainAsync(domain);
                if (!string.IsNullOrEmpty(filename))
                {
                    var existingConfig = await _configService.GetConfigAsync(filename);
                    if (!existingConfig.Contains(".well-known/acme-challenge"))
                    {
                        var updatedConfig = existingConfig.Replace(
                            "location / {",
                            "location /.well-known/acme-challenge/ {\n        root /var/www/html;\n    }\n\n    location / {");

                        if (updatedConfig != existingConfig)
                        {
                            await _configService.SaveConfigAsync(filename, updatedConfig);
                            await ReloadWithRetryAsync(ct);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NginxService] Aviso preparando nginx para desafio ACME: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }

            return await _sslService.GenerateAsync(domain, email, ct);
        }

        public async Task<bool> DeleteSslAsync(string domain, CancellationToken ct)
        {
            return await _sslService.DeleteAsync(domain, ct);
        }

        /// <summary>
        /// Roda nginx -t + reload, retornando sucesso e a stderr em caso de erro.
        /// Usado por save_ssl_files e outros caminhos que precisam de feedback
        /// sem propagar exception.
        /// </summary>
        public async Task<(bool success, string error)> TestAndReloadAsync(CancellationToken ct)
        {
            try
            {
                await _processService.ReloadAsync(ct);
                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Checks if an SSL certificate already exists for the given domain.
        /// </summary>
        public bool HasCertificate(string domain)
        {
            return !string.IsNullOrEmpty(domain) && _sslService.HasCertificate(domain);
        }

        public async Task HandleOrphanCleanupAsync(string proxyId, string domain, CancellationToken ct)
        {
            var filename = $"narc_proxy_{proxyId}.conf";
            await _configService.DeleteConfigAsync(filename);
            await _configService.CleanupOrphanConfigsAsync();
            Console.WriteLine($"[NginxService] Orphan config '{filename}' removida (proxyId={proxyId}, domain={domain}).");
        }
    }
}
