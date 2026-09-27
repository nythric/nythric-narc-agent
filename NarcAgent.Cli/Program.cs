using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNetEnv;
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
            Console.WriteLine("  ./NarcAgent.Cli <dominio> [opções]");
            Console.WriteLine();
            Console.WriteLine("Opções disponíveis:");
            Console.WriteLine("  --generate-ssl, -ssl <dominio>   Gera certificado SSL via Certes (ACME v2) e injeta no .conf");
            Console.WriteLine("  --domain, -d <dominio>           Especifica o domínio alvo");
            Console.WriteLine("  --email, -m <email>              E-mail para registro no Let's Encrypt (opcional)");
            Console.WriteLine("  --id, --proxy-id <id>            ID específico do proxy/arquivo conf (opcional)");
            Console.WriteLine("  --help, -h                       Exibe esta mensagem de ajuda");
            Console.WriteLine();
            Console.WriteLine("Exemplos:");
            Console.WriteLine("  ./NarcAgent.Cli --generate-ssl api.byc.re");
            Console.WriteLine("  ./NarcAgent.Cli --generate-ssl api.byc.re --email admin@byc.re");
            Console.WriteLine("  ./NarcAgent.Cli api.byc.re --email admin@byc.re");
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

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg.Equals("--generate-ssl", StringComparison.OrdinalIgnoreCase) ||
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

            await RunGenerateSslCliAsync(sslDomain, sslEmail, sslProxyId);
        }
    }
}
