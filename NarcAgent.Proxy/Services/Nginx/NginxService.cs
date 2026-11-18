using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Proxy.Models;
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

        public NginxSslService SslService => _sslService;
        public NginxConfigService ConfigService => _configService;

        public async Task<string?> FindConfigFileForDomainAsync(string domain)
        {
            var filename = await _configService.FindFilenameByDomainAsync(domain);
            if (string.IsNullOrEmpty(filename)) return null;
            return Path.Combine(_configService.ConfigDirectory, filename);
        }

        public Task UpdateProxyAsync(string proxyId, string domain, string targetHost, int targetPort, bool enabled, bool? sslAvailable, CancellationToken ct)
            => UpdateProxyAsync(proxyId, domain, targetHost, targetPort, enabled, sslAvailable, null, ct);

        public async Task UpdateProxyAsync(string proxyId, string domain, string targetHost, int targetPort, bool enabled, bool? sslAvailable, string? rawConfig, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                // V2 behavior: if API doesn't specify ssl_available, default to true or whether cert exists
                bool certExists = !string.IsNullOrEmpty(domain) && _sslService.HasCertificate(domain);
                bool effectiveSslAvailable = sslAvailable ?? true;
                bool actualSsl = enabled && (effectiveSslAvailable || certExists);

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

                var filename = await ResolveConfigFilenameAsync(proxyId, domain);

                if (_configService.ConfigExists(filename))
                {
                    // Se existe backup .bak (proxy desativado), atualiza também o proxy_pass no .bak
                    // para que ao ser reativado, já aponte para o novo target
                    if (_configService.BackupExists(filename))
                    {
                        try
                        {
                            var bakConfig = await _configService.GetConfigAsync(filename + ".bak");
                            var updatedBak = NginxConfigModifier.UpdateRootProxyPass(bakConfig, targetHost, targetPort);
                            await _configService.SaveConfigAsync(filename + ".bak", updatedBak);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[NginxService] Aviso ao atualizar backup {filename}.bak: {ex.Message}");
                        }
                    }

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
                    var defaultFilename = filename;
                    bool certExists = !string.IsNullOrEmpty(domain) && _sslService.HasCertificate(domain);
                    var initialConfig = _configGenerator.CompileTemplate(null, domain, targetHost, targetPort, true, true);
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

                var filename = await ResolveConfigFilenameAsync(proxyId, domain);

                if (!_configService.ConfigExists(filename))
                {
                    Console.WriteLine($"[NginxService] Arquivo de configuração não encontrado para {domain} ao configurar SSL.");
                    return false;
                }

                var (certPath, keyPath) = NginxSslService.GetCertificatePaths(domain);
                certPath = certPath.Replace('\\', '/');
                keyPath = keyPath.Replace('\\', '/');

                var originalConfig = await _configService.GetConfigAsync(filename);
                var updatedConfig = NginxConfigModifier.InjectSslIntoConfig(originalConfig, domain, certPath, keyPath);

                // Garante que o bloco 443 ativo foi de fato gerado/injetado
                if (!NginxConfigModifier.HasActivePort443(updatedConfig))
                {
                    Console.WriteLine($"[NginxService] Aviso: injeção in-place não gerou porta 443 ativa para '{domain}'. Aplicando template dinâmico com SSL...");
                    updatedConfig = _configGenerator.CompileTemplate(originalConfig, domain, "127.0.0.1", 80, true, true);
                }

                updatedConfig = NginxConfigModifier.SanitizeConfig(updatedConfig);
                await _configService.SaveConfigAsync(filename, updatedConfig);

                var (success, error) = await _processService.TestConfigurationAsync(ct);
                if (!success)
                {
                    Console.WriteLine($"[NginxService] nginx -t FALHOU após injetar SSL para '{domain}'.");
                    Console.WriteLine($"[NginxService] Detalhe do erro nginx -t: {error}");
                    Console.WriteLine($"[NginxService] Revertendo '{filename}' para o estado anterior sem SSL...");
                    await _configService.SaveConfigAsync(filename, originalConfig);
                    return false;
                }

                Console.WriteLine($"[NginxService] nginx -t OK para '{domain}'. Recarregando Nginx...");
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

                var filename = await ResolveConfigFilenameAsync(proxyId, domain);
                await _configService.DeleteConfigAsync(filename);
                await _configService.DeleteBackupAsync(filename);
                if (!string.IsNullOrEmpty(proxyId) && !proxyId.Equals(filename, StringComparison.OrdinalIgnoreCase))
                {
                    var fallbackFile = proxyId.StartsWith("narc_proxy_") ? (proxyId.EndsWith(".conf") ? proxyId : $"{proxyId}.conf") : $"narc_proxy_{proxyId}.conf";
                    await _configService.DeleteConfigAsync(fallbackFile);
                    await _configService.DeleteBackupAsync(fallbackFile);
                }
                if (!string.IsNullOrEmpty(domain))
                    await _sslService.DeleteAsync(domain, ct);
                await ReloadWithRetryAsync(ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Resolve o nome do arquivo .conf correspondente a um proxy no disco.
        /// Suporta proxyId como GUID, nome do arquivo completo (narc_proxy_*.conf ou *.conf),
        /// ou busca por domínio.
        /// </summary>
        public async Task<string> ResolveConfigFilenameAsync(string? proxyId, string? domain)
        {
            _configService.EnsureDirectoryExists();

            // 1. Se proxyId já termina com .conf
            if (!string.IsNullOrWhiteSpace(proxyId))
            {
                var trimmedId = proxyId.Trim();
                if (trimmedId.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                {
                    if (_configService.ConfigExists(trimmedId) || _configService.BackupExists(trimmedId))
                    {
                        return trimmedId;
                    }
                }

                // 2. Tenta padrão narc_proxy_{id}.conf
                var standardName = trimmedId.StartsWith("narc_proxy_", StringComparison.OrdinalIgnoreCase)
                    ? (trimmedId.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) ? trimmedId : $"{trimmedId}.conf")
                    : $"narc_proxy_{trimmedId}.conf";

                if (_configService.ConfigExists(standardName) || _configService.BackupExists(standardName))
                {
                    return standardName;
                }
            }

            // 3. Busca por domínio se informado
            if (!string.IsNullOrWhiteSpace(domain))
            {
                var trimmedDomain = domain.Trim();
                var found = await _configService.FindFilenameByDomainAsync(trimmedDomain);
                if (!string.IsNullOrEmpty(found))
                {
                    return found;
                }

                var domainConf = $"narc_proxy_{trimmedDomain}.conf";
                if (_configService.ConfigExists(domainConf) || _configService.BackupExists(domainConf))
                {
                    return domainConf;
                }

                var directDomainConf = $"{trimmedDomain}.conf";
                if (_configService.ConfigExists(directDomainConf) || _configService.BackupExists(directDomainConf))
                {
                    return directDomainConf;
                }
            }

            // 4. Fallback padrão
            if (!string.IsNullOrWhiteSpace(proxyId))
            {
                var cleanId = proxyId.Trim();
                if (cleanId.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                    return cleanId;
                if (cleanId.StartsWith("narc_proxy_", StringComparison.OrdinalIgnoreCase))
                    return $"{cleanId}.conf";
                return $"narc_proxy_{cleanId}.conf";
            }

            if (!string.IsNullOrWhiteSpace(domain))
            {
                return $"narc_proxy_{domain.Trim()}.conf";
            }

            return "narc_proxy_default.conf";
        }

        public string ResolveConfigFilename(string? proxyId, string? domain)
        {
            return ResolveConfigFilenameAsync(proxyId, domain).GetAwaiter().GetResult();
        }

        public string GenerateDeactivatedConfig(string domain, bool? sslAvailable = null)
        {
            bool certExists = !string.IsNullOrEmpty(domain) && _sslService.HasCertificate(domain);
            bool effectiveSslAvailable = sslAvailable ?? certExists;
            return _configGenerator.GenerateDeactivatedConfig(domain, effectiveSslAvailable);
        }

        public async Task ToggleProxyAsync(string proxyId, string domain, string targetHost, int targetPort, bool active, bool? sslAvailable, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                _configService.EnsureDirectoryExists();
                var filename = await ResolveConfigFilenameAsync(proxyId, domain);

                // Se o domínio veio vazio, tenta extrair do arquivo de config ou backup
                if (string.IsNullOrWhiteSpace(domain))
                {
                    domain = await _configService.GetDomainFromConfigAsync(filename) ?? "";
                }

                if (active)
                {
                    // Reativar proxy:
                    // Se existe backup (.bak), restaura o arquivo original e remove o backup
                    if (_configService.BackupExists(filename))
                    {
                        await _configService.RestoreAsync(filename);
                        await _configService.DeleteBackupAsync(filename);
                        Console.WriteLine($"[NginxService] [ToggleProxy] Proxy '{filename}' reativado a partir do backup .bak.");
                    }
                    else
                    {
                        Console.WriteLine($"[NginxService] [ToggleProxy] Aviso: Backup .bak não encontrado para '{filename}'. Verificando se já está ativo.");
                    }
                }
                else
                {
                    // Desativar proxy:
                    // 1. Faz backup do arquivo original apenas se o backup ainda não existir
                    if (_configService.ConfigExists(filename) && !_configService.BackupExists(filename))
                    {
                        await _configService.BackupAsync(filename);
                        Console.WriteLine($"[NginxService] [ToggleProxy] Backup criado: '{filename}.bak'.");
                    }

                    // 2. Garante o HTML de desativação
                    _configService.EnsureMaintenanceFile();

                    // 3. Determina status do SSL
                    bool certExists = !string.IsNullOrEmpty(domain) && _sslService.HasCertificate(domain);
                    bool effectiveSslAvailable = sslAvailable ?? certExists;
                    bool includeSsl = effectiveSslAvailable || certExists;

                    // 4. Gera configuração de desativação mantendo server_name e SSL
                    var config = _configGenerator.Generate(domain, targetHost, targetPort, false, includeSsl);
                    await _configService.SaveConfigAsync(filename, config);
                    Console.WriteLine($"[NginxService] [ToggleProxy] Configuração de desativação aplicada em '{filename}' (servindo narc_deactivated.html).");
                }

                // 5. Executa reload no Nginx
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

        /// <summary>
        /// Realiza a reconciliação completa entre os proxies cadastrados no banco de dados e os arquivos
        /// no disco do Nginx:
        /// 1. Remove arquivos .conf de proxies órfãos (que não existem no banco de dados).
        /// 2. Para cada proxy legítimo no banco, garante que o arquivo existe e possui configuração SSL válida
        ///    (porta 443 com certificado e chave corretos), gerando bootstrap se necessário.
        /// 3. Valida a sintaxe com nginx -t e recarrega o Nginx.
        /// </summary>
        public async Task<ReconciliationResult> ReconcileWithDatabaseAsync(IEnumerable<DatabaseProxy> dbProxies, CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            var result = new ReconciliationResult();

            try
            {
                _configService.EnsureDirectoryExists();

                var proxyList = dbProxies?.Where(p => !string.IsNullOrWhiteSpace(p.Domain)).ToList() ?? new List<DatabaseProxy>();
                result.TotalDbProxies = proxyList.Count;

                if (proxyList.Count == 0)
                {
                    Console.WriteLine("[NginxService] [Reconcile] Nenhum proxy informado ou banco vazio. Nenhum arquivo será excluído por segurança.");
                    result.Success = true;
                    return result;
                }

                var validIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var validDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var p in proxyList)
                {
                    if (!string.IsNullOrWhiteSpace(p.Id)) validIds.Add(p.Id);
                    validDomains.Add(p.Domain.Trim().ToLowerInvariant());
                }

                // 1. Limpeza de arquivos de proxies órfãos (arquivos que não pertencem a nenhum proxy cadastrado no banco)
                var candidateFiles = _configService.GetAllCandidateProxyFiles();
                foreach (var file in candidateFiles)
                {
                    var filename = Path.GetFileName(file);
                    string content;
                    try
                    {
                        content = await File.ReadAllTextAsync(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!NginxConfigService.IsNarcManagedConfig(filename, content))
                    {
                        // Não é configuração de proxy do Narc, preserva
                        continue;
                    }

                    var domainsInFile = NginxConfigService.ExtractDomainsFromConfigContent(content);

                    // Extrai identificador do nome do arquivo
                    // e.g. narc_proxy_015f9c1a-c302-48cc-955e-2666a1427617.conf -> 015f9c1a-c302-48cc-955e-2666a1427617
                    //      narc_proxy_smm.nesk.lol.conf -> smm.nesk.lol
                    //      smm.nesk.lol.conf -> smm.nesk.lol
                    string idOrDomain = filename;
                    if (idOrDomain.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                        idOrDomain = idOrDomain[..^5];
                    if (idOrDomain.StartsWith("narc_proxy_", StringComparison.OrdinalIgnoreCase))
                        idOrDomain = idOrDomain["narc_proxy_".Length..];

                    bool matchesDbId = validIds.Contains(idOrDomain);
                    bool matchesDbDomain = validDomains.Contains(idOrDomain.ToLowerInvariant()) ||
                                           domainsInFile.Any(d => validDomains.Contains(d.ToLowerInvariant()));

                    bool isOrphan = false;

                    if (!matchesDbId && !matchesDbDomain)
                    {
                        // Não corresponde a nenhum ID e a nenhum domínio cadastrado no banco (ex: smm.nesk.lol)
                        isOrphan = true;
                    }
                    else if (matchesDbDomain)
                    {
                        // O domínio está no banco, mas pode ser um arquivo legado ou duplicado
                        var matchedProxy = proxyList.FirstOrDefault(p =>
                            string.Equals(p.Domain, idOrDomain, StringComparison.OrdinalIgnoreCase) ||
                            domainsInFile.Contains(p.Domain, StringComparer.OrdinalIgnoreCase));

                        if (matchedProxy != null && !string.IsNullOrEmpty(matchedProxy.Id))
                        {
                            var officialFilename = $"narc_proxy_{matchedProxy.Id}.conf";
                            // Se este arquivo não é o arquivo oficial e o arquivo oficial já existe, remove o duplicado
                            if (!string.Equals(filename, officialFilename, StringComparison.OrdinalIgnoreCase) &&
                                _configService.ConfigExists(officialFilename))
                            {
                                isOrphan = true;
                            }
                        }
                    }

                    if (isOrphan)
                    {
                        Console.WriteLine($"[NginxService] [Reconcile] Removendo arquivo de proxy órfão: {filename} (domínios: {string.Join(", ", domainsInFile)})");
                        await _configService.DeleteConfigAsync(filename);
                        await _configService.DeleteBackupAsync(filename);
                        result.RemovedOrphans++;
                        result.RemovedFiles.Add(filename);
                    }
                }

                // 2. Verificação e injeção de SSL para todos os proxies registrados no banco
                foreach (var proxy in proxyList)
                {
                    var domain = proxy.Domain.Trim();
                    var targetFilename = await ResolveConfigFilenameAsync(proxy.Id, domain);

                    // Garante que o certificado existe (autoassinado bootstrap provisório se ainda não tiver Let's Encrypt)
                    bool certExists = _sslService.HasCertificate(domain);
                    if (!certExists)
                    {
                        NginxSslService.EnsureBootstrapCertificate(domain);
                        certExists = _sslService.HasCertificate(domain);
                    }

                    var (certPath, keyPath) = NginxSslService.GetCertificatePaths(domain);
                    certPath = certPath.Replace('\\', '/');
                    keyPath = keyPath.Replace('\\', '/');

                    if (!proxy.Enabled)
                    {
                        // O proxy está DESATIVADO no banco de dados: respeita a regra da desativação
                        // (HTML narc_deactivated.html servido no .conf ativo e configuração original preservada em .bak)
                        _configService.EnsureMaintenanceFile();

                        if (!_configService.ConfigExists(targetFilename))
                        {
                            // Arquivo não existe no disco: cria o .conf de desativação
                            var maintConfig = _configGenerator.Generate(domain, proxy.TargetHost, proxy.TargetPort, false, certExists);
                            await _configService.SaveConfigAsync(targetFilename, maintConfig);
                            // Cria também uma cópia .bak com template padrão ativo para quando for reativado
                            var activeConfig = _configGenerator.CompileTemplate(proxy.RawConfig, domain, proxy.TargetHost, proxy.TargetPort, true, certExists);
                            await _configService.SaveConfigAsync(targetFilename + ".bak", activeConfig);
                            result.VerifiedConfigs++;
                            Console.WriteLine($"[NginxService] [Reconcile] Proxy desativado criado no disco para '{domain}' ({targetFilename}).");
                        }
                        else
                        {
                            // Arquivo existe no disco: se ainda não foi feito backup, faz backup agora
                            if (!_configService.BackupExists(targetFilename))
                            {
                                await _configService.BackupAsync(targetFilename);
                                Console.WriteLine($"[NginxService] [Reconcile] Backup '{targetFilename}.bak' criado para proxy desativado '{domain}'.");
                            }

                            // Garante que o arquivo ativo está servindo a página de manutenção
                            var currentContent = await _configService.GetConfigAsync(targetFilename);
                            if (!currentContent.Contains("narc_deactivated.html"))
                            {
                                var maintConfig = _configGenerator.Generate(domain, proxy.TargetHost, proxy.TargetPort, false, certExists);
                                await _configService.SaveConfigAsync(targetFilename, maintConfig);
                                Console.WriteLine($"[NginxService] [Reconcile] '{targetFilename}' de '{domain}' atualizado para servir manutenção.");
                            }
                            result.VerifiedConfigs++;
                        }
                        continue;
                    }

                    // Se proxy.Enabled == true (proxy ATIVO no banco):
                    // Se existia backup (.bak) de desativação anterior, restaura o arquivo original
                    if (_configService.BackupExists(targetFilename))
                    {
                        await _configService.RestoreAsync(targetFilename);
                        await _configService.DeleteBackupAsync(targetFilename);
                        Console.WriteLine($"[NginxService] [Reconcile] Proxy '{targetFilename}' de '{domain}' restaurado do backup .bak.");
                    }

                    if (!_configService.ConfigExists(targetFilename))
                    {
                        // Arquivo não existe no disco: compila template completo com SSL
                        var generatedConfig = _configGenerator.CompileTemplate(proxy.RawConfig, domain, proxy.TargetHost, proxy.TargetPort, proxy.Enabled, true);
                        await _configService.SaveConfigAsync(targetFilename, generatedConfig);
                        result.VerifiedConfigs++;
                        result.SslInjectedConfigs++;
                        Console.WriteLine($"[NginxService] [Reconcile] Configuração criada com SSL para '{domain}' ({targetFilename}).");
                    }
                    else
                    {
                        var content = await _configService.GetConfigAsync(targetFilename);
                        bool hasActive443 = NginxConfigModifier.HasActivePort443(content);
                        bool hasCert = content.Contains("ssl_certificate ") && !content.Contains("ssl_certificate ;");
                        bool hasCorrectCert = content.Contains(certPath);

                        if (!hasActive443 || !hasCert || !hasCorrectCert)
                        {
                            Console.WriteLine($"[NginxService] [Reconcile] Configuração '{targetFilename}' de '{domain}' precisa de injeção/atualização de SSL (has443={hasActive443}, hasCert={hasCert}, correctCert={hasCorrectCert}).");
                            
                            string updatedConfig = NginxConfigModifier.InjectSslIntoConfig(content, domain, certPath, keyPath);
                            if (!NginxConfigModifier.HasActivePort443(updatedConfig))
                            {
                                // Se a injeção in-place não conseguiu criar o bloco 443 ativo, recompila usando o template completo
                                updatedConfig = _configGenerator.CompileTemplate(proxy.RawConfig, domain, proxy.TargetHost, proxy.TargetPort, proxy.Enabled, true);
                            }

                            updatedConfig = NginxConfigModifier.SanitizeConfig(updatedConfig);
                            await _configService.SaveConfigAsync(targetFilename, updatedConfig);
                            result.SslInjectedConfigs++;
                            Console.WriteLine($"[NginxService] [Reconcile] SSL injetado com sucesso em '{targetFilename}' para '{domain}'.");
                        }
                        else
                        {
                            // Verifica se o arquivo precisa de sanitização (ex: duplicate locations de versões legadas)
                            var sanitized = NginxConfigModifier.SanitizeConfig(content);
                            if (!string.Equals(content, sanitized, StringComparison.Ordinal))
                            {
                                Console.WriteLine($"[NginxService] [Reconcile] '{targetFilename}' de '{domain}' corrigindo locations duplicadas / sintaxe...");
                                await _configService.SaveConfigAsync(targetFilename, sanitized);
                                result.SslInjectedConfigs++;
                            }
                        }
                        result.VerifiedConfigs++;
                    }
                }

                // 3. Validação com nginx -t e reload
                var (success, error) = await _processService.TestConfigurationAsync(ct);
                if (!success)
                {
                    Console.WriteLine($"[NginxService] [Reconcile] ERRO: nginx -t falhou após reconciliação: {error}. Tentando auto-cura...");
                    bool healed = false;
                    var confMatches = Regex.Matches(error, @"([a-zA-Z0-9_\-\.]+\.conf)");
                    foreach (Match m in confMatches)
                    {
                        var problemFile = m.Groups[1].Value;
                        if (_configService.ConfigExists(problemFile))
                        {
                            try
                            {
                                var probContent = await _configService.GetConfigAsync(problemFile);
                                var probSanitized = NginxConfigModifier.SanitizeConfig(probContent);
                                if (!string.Equals(probContent, probSanitized, StringComparison.Ordinal))
                                {
                                    Console.WriteLine($"[NginxService] [Reconcile] Auto-curando arquivo problemático '{problemFile}'...");
                                    await _configService.SaveConfigAsync(problemFile, probSanitized);
                                    healed = true;
                                }
                            }
                            catch { }
                        }
                    }

                    if (healed)
                    {
                        var retry = await _processService.TestConfigurationAsync(ct);
                        success = retry.Success;
                        error = retry.Output;
                    }

                    if (!success)
                    {
                        Console.WriteLine($"[NginxService] [Reconcile] ERRO: nginx -t falhou após reconciliação: {error}");
                        result.Errors.Add($"nginx -t falhou: {error}");
                        result.Success = false;
                        return result;
                    }
                }

                await ReloadWithRetryAsync(ct);
                result.Success = true;
                Console.WriteLine($"[NginxService] [Reconcile] Reconciliação finalizada com sucesso! Verificados: {result.VerifiedConfigs}, SSL ajustados: {result.SslInjectedConfigs}, Órfãos removidos: {result.RemovedOrphans}.");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NginxService] [Reconcile] Erro durante reconciliação: {ex.Message}");
                result.Errors.Add(ex.Message);
                result.Success = false;
                return result;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Modo local (offline/sem banco): Inspeciona todos os arquivos de configuração de proxy locais.
        /// Para cada arquivo cujo domínio possui certificado em /etc/narc/ssl, verifica se a porta 443
        /// e diretivas ssl_certificate estão devidamente configuradas; caso contrário, injeta SSL e recarrega.
        /// </summary>
        public async Task<(int verified, int fixedCount, bool success, string error)> VerifyLocalConfigsAsync(CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            int verified = 0;
            int fixedCount = 0;

            try
            {
                _configService.EnsureDirectoryExists();
                // Usa todos os arquivos candidatos a proxy (.conf exceto default.conf)
                var files = _configService.GetAllCandidateProxyFiles();

                foreach (var file in files)
                {
                    var filename = Path.GetFileName(file);
                    string content;
                    try
                    {
                        content = await _configService.GetConfigAsync(filename);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!NginxConfigService.IsNarcManagedConfig(filename, content))
                    {
                        continue;
                    }

                    var domains = NginxConfigService.ExtractDomainsFromConfigContent(content);
                    if (domains.Count == 0) continue;

                    var domain = domains[0];
                    bool certExists = _sslService.HasCertificate(domain);

                    if (!certExists)
                    {
                        NginxSslService.EnsureBootstrapCertificate(domain);
                        certExists = _sslService.HasCertificate(domain);
                    }

                    if (certExists)
                    {
                        var (certPath, keyPath) = NginxSslService.GetCertificatePaths(domain);
                        certPath = certPath.Replace('\\', '/');
                        keyPath = keyPath.Replace('\\', '/');

                        bool hasActive443 = NginxConfigModifier.HasActivePort443(content);
                        bool hasCert = content.Contains("ssl_certificate ") && !content.Contains("ssl_certificate ;");
                        bool hasCorrectCert = content.Contains(certPath);

                        if (!hasActive443 || !hasCert || !hasCorrectCert)
                        {
                            Console.WriteLine($"[NginxService] [VerifyLocal] '{filename}' ({domain}) não possui SSL ativo ou correto. Injetando...");
                            var updated = NginxConfigModifier.InjectSslIntoConfig(content, domain, certPath, keyPath);
                            if (!NginxConfigModifier.HasActivePort443(updated))
                            {
                                // Se a injeção in-place não conseguiu criar o bloco 443 ativo, compila template base com SSL
                                updated = _configGenerator.CompileTemplate(content, domain, "127.0.0.1", 80, true, true);
                            }

                            updated = NginxConfigModifier.SanitizeConfig(updated);
                            await _configService.SaveConfigAsync(filename, updated);
                            fixedCount++;
                            Console.WriteLine($"[NginxService] [VerifyLocal] SSL injetado com sucesso em '{filename}' para '{domain}'.");
                        }
                        else
                        {
                            // Verifica se o arquivo precisa de sanitização (ex: duplicate locations de versões legadas)
                            var sanitized = NginxConfigModifier.SanitizeConfig(content);
                            if (!string.Equals(content, sanitized, StringComparison.Ordinal))
                            {
                                Console.WriteLine($"[NginxService] [VerifyLocal] '{filename}' ({domain}) corrigindo locations duplicadas / sintaxe...");
                                await _configService.SaveConfigAsync(filename, sanitized);
                                fixedCount++;
                            }
                        }
                    }

                    verified++;
                }

                if (fixedCount > 0)
                {
                    var (testSuccess, error) = await _processService.TestConfigurationAsync(ct);
                    if (!testSuccess)
                    {
                        Console.WriteLine($"[NginxService] [VerifyLocal] nginx -t falhou: {error}. Tentando auto-cura via SanitizeConfig...");
                        bool healed = false;
                        var confMatches = Regex.Matches(error, @"([a-zA-Z0-9_\-\.]+\.conf)");
                        foreach (Match m in confMatches)
                        {
                            var problemFile = m.Groups[1].Value;
                            if (_configService.ConfigExists(problemFile))
                            {
                                try
                                {
                                    var probContent = await _configService.GetConfigAsync(problemFile);
                                    var probSanitized = NginxConfigModifier.SanitizeConfig(probContent);
                                    if (!string.Equals(probContent, probSanitized, StringComparison.Ordinal))
                                    {
                                        Console.WriteLine($"[NginxService] [VerifyLocal] Auto-curando arquivo problemático '{problemFile}'...");
                                        await _configService.SaveConfigAsync(problemFile, probSanitized);
                                        healed = true;
                                    }
                                }
                                catch { }
                            }
                        }

                        if (healed)
                        {
                            var retry = await _processService.TestConfigurationAsync(ct);
                            testSuccess = retry.Success;
                            error = retry.Output;
                        }

                        if (!testSuccess)
                        {
                            Console.WriteLine($"[NginxService] [VerifyLocal] ERRO: nginx -t falhou: {error}");
                            return (verified, fixedCount, false, error);
                        }
                    }

                    await ReloadWithRetryAsync(ct);
                    Console.WriteLine($"[NginxService] [VerifyLocal] Nginx recarregado com sucesso com {fixedCount} proxies atualizados para HTTPS.");
                }

                return (verified, fixedCount, true, "");
            }
            catch (Exception ex)
            {
                return (verified, fixedCount, false, ex.Message);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
