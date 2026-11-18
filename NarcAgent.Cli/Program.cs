using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNetEnv;
using NarcAgent.Proxy.Services;
using NarcAgent.Proxy.Services.Nginx;

namespace NarcAgent.Cli
{
    class Program
    {
        static void PrintCliHelp()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  NARC AGENT CLI - GERENCIAMENTO E SSL");
            Console.WriteLine("==================================================");
            Console.WriteLine("Uso:");
            Console.WriteLine("  ./NarcAgent.Cli --generate-ssl <dominio> [opções]");
            Console.WriteLine("  ./NarcAgent.Cli --sync");
            Console.WriteLine("  ./NarcAgent.Cli <dominio> [opções]");
            Console.WriteLine();
            Console.WriteLine("Opções disponíveis:");
            Console.WriteLine("  --sync                           Sincroniza com o banco de dados via API: remove órfãos e injeta SSL");
            Console.WriteLine("  --clean-orphans                  Remove arquivos .conf que não constam no banco de dados");
            Console.WriteLine("  --verify-configs                 Verifica localmente todos os .conf e injeta SSL onde houver certificados");
            Console.WriteLine("  --generate-ssl, -ssl <dominio>   Gera certificado SSL via Certes (ACME v2) e injeta no .conf");
            Console.WriteLine("  --inject-ssl <dominio>           Injeta cert SSL existente no .conf sem renegerar (útil após falha de injeção)");
            Console.WriteLine("  --show-conf <dominio>            Exibe o conteúdo atual do arquivo .conf do domínio");
            Console.WriteLine("  --renew-ssl [dominio]            Verifica e renova certificados que expiram em <= 30 dias (ou um domínio específico)");
            Console.WriteLine("  --check-ssl                      Inspeciona e exibe a validade de todos os certificados em /etc/narc/ssl");
            Console.WriteLine("  --force                          Força renovação imediata mesmo que não esteja expirando (usado com --renew-ssl)");
            Console.WriteLine("  --domain, -d <dominio>           Especifica o domínio alvo");
            Console.WriteLine("  --email, -m <email>              E-mail para registro no Let's Encrypt (opcional)");
            Console.WriteLine("  --id, --proxy-id <id>            ID específico do proxy/arquivo conf (opcional)");
            Console.WriteLine("  --help, -h                       Exibe esta mensagem de ajuda");
            Console.WriteLine();
            Console.WriteLine("Exemplos:");
            Console.WriteLine("  ./NarcAgent.Cli --generate-ssl api.byc.re");
            Console.WriteLine("  ./NarcAgent.Cli --inject-ssl byc.re");
            Console.WriteLine("  ./NarcAgent.Cli --show-conf byc.re");
            Console.WriteLine("  ./NarcAgent.Cli --check-ssl");
            Console.WriteLine("  ./NarcAgent.Cli --renew-ssl");
            Console.WriteLine("  ./NarcAgent.Cli --renew-ssl api.byc.re --force");
            Console.WriteLine("==================================================");
        }

        static async Task RunGenerateSslCliAsync(string? sslDomain, string? sslEmail, string? sslProxyId)
        {
            if (string.IsNullOrWhiteSpace(sslDomain))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[CLI ERRO] Nenhum domínio informado para geração de certificado SSL.");
                Console.ResetColor();
                Console.WriteLine();
                PrintCliHelp();
                Environment.Exit(1);
                return;
            }

            // Normaliza domínio (remove espaços, http/https, trailing slash)
            sslDomain = sslDomain.Trim();
            if (sslDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                sslDomain = sslDomain["https://".Length..];
            else if (sslDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                sslDomain = sslDomain["http://".Length..];
            sslDomain = sslDomain.TrimEnd('/');

            // Fallback para e-mail caso não passado por CLI
            sslEmail ??= Environment.GetEnvironmentVariable("ACME_EMAIL")
                         ?? Environment.GetEnvironmentVariable("SSL_EMAIL")
                         ?? Environment.GetEnvironmentVariable("ADMIN_EMAIL");

            Console.WriteLine("==================================================");
            Console.WriteLine("  NARC AGENT - CLI");
            Console.WriteLine("  Geração de SSL e Injeção In-Place no Nginx");
            Console.WriteLine("==================================================");
            Console.WriteLine($"  Domínio:    {sslDomain}");
            if (!string.IsNullOrEmpty(sslEmail))
                Console.WriteLine($"  E-mail:     {sslEmail}");
            if (!string.IsNullOrEmpty(sslProxyId))
                Console.WriteLine($"  Proxy ID:   {sslProxyId}");
            Console.WriteLine("==================================================");

            try
            {
                Console.WriteLine("[CLI] 1/3: Instanciando NginxService e módulo ACME (Certes)...");
                var configService = new NginxConfigService();
                var configGenerator = new NginxConfigGenerator();
                var processService = new NginxProcessService();
                var sslService = new NginxSslService();
                var nginxService = new NginxService(configService, configGenerator, processService, sslService);

                // Localiza arquivo de configuração Nginx correspondente
                Console.WriteLine("[CLI] Verificando arquivo de configuração Nginx...");
                string? targetConf = !string.IsNullOrEmpty(sslProxyId) ? $"narc_proxy_{sslProxyId}.conf" : null;
                if (targetConf == null || !configService.ConfigExists(targetConf))
                {
                    targetConf = await configService.FindFilenameByDomainAsync(sslDomain);
                }

                if (!string.IsNullOrEmpty(targetConf))
                {
                    Console.WriteLine($"[CLI] Arquivo de configuração Nginx identificado: {targetConf}");
                }
                else
                {
                    Console.WriteLine($"[CLI] Aviso: Nenhum arquivo .conf específico encontrado preliminarmente para '{sslDomain}'.");
                    Console.WriteLine("[CLI] A rotina tentará gerar o certificado e localizar o arquivo durante a injeção.");
                }

                // 2. Emissão do certificado nativamente via Certes (ACME v2)
                Console.WriteLine($"[CLI] 2/3: Executando rotina de geração de certificado SSL para '{sslDomain}'...");
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                bool certIssued = await nginxService.GenerateSslAsync(sslDomain, sslEmail, cts.Token);

                if (!certIssued)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[CLI ERRO] Falha na emissão do certificado SSL para o domínio '{sslDomain}'.");
                    Console.WriteLine("[CLI ERRO] Certifique-se de que o DNS aponta para este servidor e que a porta 80 está acessível externamente.");
                    Console.ResetColor();
                    Environment.Exit(1);
                    return;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[CLI SUCESSO] Certificado SSL emitido com sucesso para '{sslDomain}'!");
                Console.ResetColor();

                // 3. Injeção in-place no arquivo .conf e reload do Nginx
                Console.WriteLine($"[CLI] 3/3: Injetando certificados SSL in-place no arquivo .conf e recarregando Nginx...");
                bool configured = await nginxService.ConfigureSslInPlaceAsync(sslDomain, sslProxyId, cts.Token);

                if (!configured)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[CLI ERRO] Certificado SSL gerado, mas ocorreu um erro ao atualizar o arquivo .conf ou o comando 'nginx -t' falhou.");
                    Console.ResetColor();
                    Environment.Exit(1);
                    return;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("==================================================");
                Console.WriteLine($"[CLI SUCESSO] Certificado SSL instalado com sucesso!");
                Console.WriteLine($"[CLI SUCESSO] Domínio '{sslDomain}' configurado com HTTPS no Nginx.");
                Console.WriteLine("==================================================");
                Console.ResetColor();

                try
                {
                    var syncService = new ProxySyncService(nginxService);
                    await syncService.ReportSslStatusToApiAsync(ct: cts.Token);
                }
                catch { }

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[CLI ERRO FATAL] Erro durante a execução: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// Injeta certificado SSL existente no .conf sem renegerar (útil quando cert já foi emitido mas injeção falhou).
        /// </summary>
        static async Task RunInjectSslCliAsync(string? domain, string? proxyId)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[CLI ERRO] Nenhum domínio informado para --inject-ssl.");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            domain = domain.Trim().TrimEnd('/');
            if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) domain = domain[8..];
            else if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) domain = domain[7..];

            Console.WriteLine("==================================================");
            Console.WriteLine("  NARC AGENT - CLI");
            Console.WriteLine("  Injeção de SSL In-Place no Nginx");
            Console.WriteLine("==================================================");
            Console.WriteLine($"  Domínio: {domain}");
            Console.WriteLine("==================================================");

            var configService = new NginxConfigService();
            var configGenerator = new NginxConfigGenerator();
            var processService = new NginxProcessService();
            var sslService = new NginxSslService();
            var nginxService = new NginxService(configService, configGenerator, processService, sslService);

            // Verifica se o certificado existe
            if (!sslService.HasCertificate(domain))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[CLI ERRO] Nenhum certificado SSL encontrado em /etc/narc/ssl/{domain}/");
                Console.WriteLine("[CLI ERRO] Execute --generate-ssl primeiro para emitir o certificado.");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            var (certPath, keyPath) = NginxSslService.GetCertificatePaths(domain);
            Console.WriteLine($"[CLI] Certificado encontrado:");
            Console.WriteLine($"[CLI]   cert: {certPath}");
            Console.WriteLine($"[CLI]   key:  {keyPath}");
            Console.WriteLine();

            // Localiza o .conf
            string? targetConf = !string.IsNullOrEmpty(proxyId) ? $"narc_proxy_{proxyId}.conf" : null;
            if (targetConf == null || !configService.ConfigExists(targetConf))
                targetConf = await configService.FindFilenameByDomainAsync(domain);

            if (string.IsNullOrEmpty(targetConf))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[CLI ERRO] Nenhum arquivo .conf encontrado para '{domain}' em {configService.ConfigDirectory}");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            Console.WriteLine($"[CLI] Arquivo .conf identificado: {targetConf}");
            Console.WriteLine("[CLI] Injetando SSL in-place...");
            Console.WriteLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            bool ok = await nginxService.ConfigureSslInPlaceAsync(domain, proxyId, cts.Token);

            if (ok)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("==================================================");
                Console.WriteLine($"[CLI SUCESSO] SSL injetado e Nginx recarregado para '{domain}'!");
                Console.WriteLine("==================================================");
                Console.ResetColor();
                Environment.Exit(0);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[CLI ERRO] Falha ao injetar SSL. Verifique os logs acima para o erro do nginx -t.");
                Console.ResetColor();
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// Exibe o conteúdo atual do arquivo .conf de um domínio no console.
        /// </summary>
        static async Task RunShowConfCliAsync(string? domain, string? proxyId)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[CLI ERRO] Nenhum domínio informado para --show-conf.");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            domain = domain.Trim().TrimEnd('/');
            if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) domain = domain[8..];
            else if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) domain = domain[7..];

            var configService = new NginxConfigService();

            string? targetConf = !string.IsNullOrEmpty(proxyId) ? $"narc_proxy_{proxyId}.conf" : null;
            if (targetConf == null || !configService.ConfigExists(targetConf))
                targetConf = await configService.FindFilenameByDomainAsync(domain);

            if (string.IsNullOrEmpty(targetConf))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[CLI] Nenhum arquivo .conf encontrado para '{domain}' em {configService.ConfigDirectory}");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            var content = await configService.GetConfigAsync(targetConf);
            var fullPath = Path.Combine(configService.ConfigDirectory, targetConf);

            Console.WriteLine("==================================================");
            Console.WriteLine($"  ARQUIVO: {fullPath}");
            Console.WriteLine("==================================================");
            Console.WriteLine(content);
            Console.WriteLine("==================================================");

            // Verificações rápidas
            bool has443 = NginxConfigModifier.HasActivePort443(content);
            bool hasCert = content.Contains("ssl_certificate ") && !content.Contains("ssl_certificate ;");
            bool hasKey = content.Contains("ssl_certificate_key ") && !content.Contains("ssl_certificate_key ;");
            Console.WriteLine($"  listen 443 (ativo): {(has443 ? "✓ SIM" : "✗ NÃO")}");
            Console.WriteLine($"  ssl_certificate: {(hasCert ? "✓ SIM" : "✗ NÃO")}");
            Console.WriteLine($"  ssl_certificate_key: {(hasKey ? "✓ SIM" : "✗ NÃO")}");
            Console.WriteLine("==================================================");
        }

        static async Task RunSyncCliAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  NARC AGENT - SINCRONIZAÇÃO E RECONCILIAÇÃO");
            Console.WriteLine("==================================================");

            var configService = new NginxConfigService();
            var configGenerator = new NginxConfigGenerator();
            var processService = new NginxProcessService();
            var sslService = new NginxSslService();
            var nginxService = new NginxService(configService, configGenerator, processService, sslService);
            var syncService = new ProxySyncService(nginxService);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var res = await syncService.SyncAndReconcileAsync(ct: cts.Token);

            Console.WriteLine("==================================================");
            if (res.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[CLI SUCESSO] Sincronização e reconciliação concluídas com sucesso!");
                Console.ResetColor();
                Console.WriteLine($"  Proxies no banco:     {res.TotalDbProxies}");
                Console.WriteLine($"  Configs verificados:  {res.VerifiedConfigs}");
                Console.WriteLine($"  SSL ajustados:        {res.SslInjectedConfigs}");
                Console.WriteLine($"  Órfãos removidos:     {res.RemovedOrphans}");
                if (res.RemovedFiles.Count > 0)
                {
                    Console.WriteLine("  Arquivos removidos:");
                    foreach (var f in res.RemovedFiles)
                    {
                        Console.WriteLine($"    - {f}");
                    }
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[CLI ERRO] Sincronização falhou.");
                foreach (var err in res.Errors)
                {
                    Console.WriteLine($"  - {err}");
                }
                Console.ResetColor();
                Environment.Exit(1);
            }
            Console.WriteLine("==================================================");
        }

        static async Task RunVerifyLocalConfigsCliAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  NARC AGENT - VERIFICAÇÃO LOCAL DE CONFIGS");
            Console.WriteLine("==================================================");

            var configService = new NginxConfigService();
            var configGenerator = new NginxConfigGenerator();
            var processService = new NginxProcessService();
            var sslService = new NginxSslService();
            var nginxService = new NginxService(configService, configGenerator, processService, sslService);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var (verified, fixedCount, success, error) = await nginxService.VerifyLocalConfigsAsync(cts.Token);

            Console.WriteLine("==================================================");
            if (success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[CLI SUCESSO] Verificação concluída!");
                Console.ResetColor();
                Console.WriteLine($"  Configs verificados: {verified}");
                Console.WriteLine($"  SSL injetados:       {fixedCount}");

                try
                {
                    var syncService = new ProxySyncService(nginxService);
                    await syncService.ReportSslStatusToApiAsync(ct: cts.Token);
                }
                catch { }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[CLI ERRO] Falha na validação: {error}");
                Console.ResetColor();
                Environment.Exit(1);
            }
            Console.WriteLine("==================================================");
        }

        static async Task RunCheckSslCliAsync()
        {
            var sslService = new NginxSslService();
            var domains = sslService.GetAllManagedDomains();

            Console.WriteLine("==================================================");
            Console.WriteLine("  NARC AGENT - STATUS DOS CERTIFICADOS SSL");
            Console.WriteLine("==================================================");

            if (domains.Count == 0)
            {
                Console.WriteLine("  Nenhum certificado SSL encontrado em /etc/narc/ssl.");
                Console.WriteLine("==================================================");
                return;
            }

            Console.WriteLine($"{"DOMÍNIO",-30} {"EXPIRAÇÃO (UTC)",-22} {"RESTANTE",-12} {"STATUS"}");
            Console.WriteLine(new string('-', 75));

            foreach (var d in domains)
            {
                var exp = NginxSslService.GetCertificateExpiration(d);
                if (!exp.HasValue)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"{d,-30} {"(ilegível)",-22} {"-",-12} [ERRO]");
                    Console.ResetColor();
                    continue;
                }

                var days = (exp.Value - DateTime.UtcNow).TotalDays;
                var needsRenew = sslService.NeedsRenewal(d, 30);

                if (days < 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"{d,-30} {exp.Value:yyyy-MM-dd HH:mm:ss}   {days,6:F1} dias   [VENCIDO]");
                }
                else if (needsRenew)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"{d,-30} {exp.Value:yyyy-MM-dd HH:mm:ss}   {days,6:F1} dias   [RENOVAR]");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"{d,-30} {exp.Value:yyyy-MM-dd HH:mm:ss}   {days,6:F1} dias   [VÁLIDO]");
                }
                Console.ResetColor();
            }

            Console.WriteLine("==================================================");

            try
            {
                var configService = new NginxConfigService();
                var configGenerator = new NginxConfigGenerator();
                var processService = new NginxProcessService();
                var nginxService = new NginxService(configService, configGenerator, processService, sslService);
                var syncService = new ProxySyncService(nginxService);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await syncService.ReportSslStatusToApiAsync(ct: cts.Token);
            }
            catch { }
        }

        static async Task RunRenewSslCliAsync(string? specificDomain, string? sslEmail, bool force)
        {
            var configService = new NginxConfigService();
            var configGenerator = new NginxConfigGenerator();
            var processService = new NginxProcessService();
            var sslService = new NginxSslService();
            var nginxService = new NginxService(configService, configGenerator, processService, sslService);

            sslEmail ??= Environment.GetEnvironmentVariable("ACME_EMAIL")
                         ?? Environment.GetEnvironmentVariable("SSL_EMAIL")
                         ?? Environment.GetEnvironmentVariable("ADMIN_EMAIL");

            Console.WriteLine("==================================================");
            Console.WriteLine("  NARC AGENT - RENOVAÇÃO DE CERTIFICADOS SSL");
            Console.WriteLine("==================================================");

            var targets = !string.IsNullOrEmpty(specificDomain)
                ? new System.Collections.Generic.List<string> { specificDomain }
                : sslService.GetAllManagedDomains();

            if (targets.Count == 0)
            {
                Console.WriteLine("Nenhum domínio com certificado encontrado para renovar.");
                return;
            }

            int renewed = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            foreach (var domain in targets)
            {
                bool needs = sslService.NeedsRenewal(domain, 30);
                var exp = NginxSslService.GetCertificateExpiration(domain);
                var days = exp.HasValue ? (exp.Value - DateTime.UtcNow).TotalDays : 0;

                Console.WriteLine($"\n[RENOVAÇÃO] Analisando '{domain}' (Expira: {(exp.HasValue ? exp.Value.ToString("yyyy-MM-dd") : "Desconhecido")}, {days:F1} dias)...");

                if (!force && !needs)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  Certificado ainda válido por {days:F1} dias (> 30 dias). Pulando. (Use --force para renovar)");
                    Console.ResetColor();
                    continue;
                }

                Console.WriteLine($"  Emitindo novo certificado via Certes (ACME v2)...");
                bool success = await nginxService.GenerateSslAsync(domain, sslEmail, cts.Token);
                if (!success)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  [FALHA] Falha ao renovar certificado para '{domain}'.");
                    Console.ResetColor();
                    continue;
                }

                Console.WriteLine($"  Atualizando Nginx com o novo certificado...");
                bool configured = await nginxService.ConfigureSslInPlaceAsync(domain, null, cts.Token);
                if (configured)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  [SUCESSO] Certificado de '{domain}' renovado e Nginx recarregado!");
                    Console.ResetColor();
                    renewed++;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  [ERRO] Certificado gerado, mas erro ao recarregar configuração Nginx para '{domain}'.");
                    Console.ResetColor();
                }
            }

            Console.WriteLine("\n==================================================");
            Console.WriteLine($"Total de certificados renovados com sucesso: {renewed}");
            Console.WriteLine("==================================================");
        }

        static async Task Main(string[] args)
        {
            Env.TraversePath().Load();

            if (args.Length == 0)
            {
                PrintCliHelp();
                return;
            }

            string? sslDomain = null;
            string? sslEmail = null;
            string? sslProxyId = null;
            bool checkSslMode = false;
            bool renewSslMode = false;
            bool forceRenew = false;
            bool injectSslMode = false;
            bool showConfMode = false;
            bool syncMode = false;
            bool verifyConfigsMode = false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg.Equals("--sync", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--clean-orphans", StringComparison.OrdinalIgnoreCase))
                {
                    syncMode = true;
                }
                else if (arg.Equals("--verify-configs", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--verify", StringComparison.OrdinalIgnoreCase))
                {
                    verifyConfigsMode = true;
                }
                else if (arg.Equals("--check-ssl", StringComparison.OrdinalIgnoreCase))
                {
                    checkSslMode = true;
                }
                else if (arg.Equals("--inject-ssl", StringComparison.OrdinalIgnoreCase))
                {
                    injectSslMode = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        sslDomain = args[i + 1].Trim();
                        i++;
                    }
                }
                else if (arg.StartsWith("--inject-ssl=", StringComparison.OrdinalIgnoreCase))
                {
                    injectSslMode = true;
                    sslDomain = arg["--inject-ssl=".Length..].Trim();
                }
                else if (arg.Equals("--show-conf", StringComparison.OrdinalIgnoreCase))
                {
                    showConfMode = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        sslDomain = args[i + 1].Trim();
                        i++;
                    }
                }
                else if (arg.StartsWith("--show-conf=", StringComparison.OrdinalIgnoreCase))
                {
                    showConfMode = true;
                    sslDomain = arg["--show-conf=".Length..].Trim();
                }
                else if (arg.Equals("--renew-ssl", StringComparison.OrdinalIgnoreCase))
                {
                    renewSslMode = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        sslDomain = args[i + 1].Trim();
                        i++;
                    }
                }
                else if (arg.StartsWith("--renew-ssl=", StringComparison.OrdinalIgnoreCase))
                {
                    renewSslMode = true;
                    sslDomain = arg["--renew-ssl=".Length..].Trim();
                }
                else if (arg.Equals("--force", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-f", StringComparison.OrdinalIgnoreCase))
                {
                    forceRenew = true;
                }
                else if (arg.Equals("--generate-ssl", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-ssl", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        sslDomain = args[i + 1].Trim();
                        i++;
                    }
                }
                else if (arg.StartsWith("--generate-ssl=", StringComparison.OrdinalIgnoreCase))
                {
                    sslDomain = arg["--generate-ssl=".Length..].Trim();
                }
                else if (arg.Equals("--domain", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-d", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        sslDomain = args[i + 1].Trim();
                        i++;
                    }
                }
                else if (arg.StartsWith("--domain=", StringComparison.OrdinalIgnoreCase))
                {
                    sslDomain = arg["--domain=".Length..].Trim();
                }
                else if (arg.Equals("--email", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-m", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        sslEmail = args[i + 1].Trim();
                        i++;
                    }
                }
                else if (arg.StartsWith("--email=", StringComparison.OrdinalIgnoreCase))
                {
                    sslEmail = arg["--email=".Length..].Trim();
                }
                else if (arg.Equals("--proxy-id", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--id", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        sslProxyId = args[i + 1].Trim();
                        i++;
                    }
                }
                else if (arg.StartsWith("--proxy-id=", StringComparison.OrdinalIgnoreCase))
                {
                    sslProxyId = arg["--proxy-id=".Length..].Trim();
                }
                else if (arg.StartsWith("--id=", StringComparison.OrdinalIgnoreCase))
                {
                    sslProxyId = arg["--id=".Length..].Trim();
                }
                else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintCliHelp();
                    return;
                }
                else if (!arg.StartsWith("-") && string.IsNullOrEmpty(sslDomain))
                {
                    sslDomain = arg.Trim();
                }
            }

            if (syncMode)
            {
                await RunSyncCliAsync();
                return;
            }

            if (verifyConfigsMode)
            {
                await RunVerifyLocalConfigsCliAsync();
                return;
            }

            if (checkSslMode)
            {
                await RunCheckSslCliAsync();
                return;
            }

            if (showConfMode)
            {
                await RunShowConfCliAsync(sslDomain, sslProxyId);
                return;
            }

            if (injectSslMode)
            {
                await RunInjectSslCliAsync(sslDomain, sslProxyId);
                return;
            }

            if (renewSslMode)
            {
                await RunRenewSslCliAsync(sslDomain, sslEmail, forceRenew);
                return;
            }

            await RunGenerateSslCliAsync(sslDomain, sslEmail, sslProxyId);
        }
    }
}
