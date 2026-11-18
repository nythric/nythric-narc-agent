using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using IoDirectory = System.IO.Directory;

namespace NarcAgent.Proxy.Services.Nginx
{
    public class NginxSslService
    {
        private readonly string _webRootPath;
        private readonly string _sslBasePath;

        public const string DefaultWebRoot = "/var/www/html";
        public const string DefaultSslPath = "/etc/narc/ssl";

        public NginxSslService(string webRootPath = DefaultWebRoot, string sslBasePath = DefaultSslPath)
        {
            _webRootPath = webRootPath;
            _sslBasePath = sslBasePath;
        }

        /// <summary>
        /// Checks if an SSL certificate exists for the given domain.
        /// </summary>
        public bool HasCertificate(string domain)
        {
            return CheckCertificateExists(domain, _sslBasePath);
        }

        public static bool CheckCertificateExists(string domain, string sslBasePath = DefaultSslPath)
        {
            if (string.IsNullOrWhiteSpace(domain)) return false;

            // Check /etc/narc/ssl/{domain}/
            var narcDir = Path.Combine(sslBasePath, domain);
            return File.Exists(Path.Combine(narcDir, $"{domain}.crt")) ||
                   File.Exists(Path.Combine(narcDir, "fullchain.crt")) ||
                   File.Exists(Path.Combine(narcDir, "certificate.crt"));
        }

        /// <summary>
        /// Retrieves the certificate and private key paths for the domain strictly within /etc/narc/ssl/{domain}.
        /// </summary>
        public static (string certPath, string keyPath) GetCertificatePaths(string domain, string sslBasePath = DefaultSslPath)
        {
            var narcDir = Path.Combine(sslBasePath, domain);

            var fullchainCrt = Path.Combine(narcDir, "fullchain.crt");
            var privkeyKey = Path.Combine(narcDir, "privkey.key");
            if (File.Exists(fullchainCrt) && File.Exists(privkeyKey))
            {
                return (fullchainCrt, privkeyKey);
            }

            var domainCrt = Path.Combine(narcDir, $"{domain}.crt");
            var domainKey = Path.Combine(narcDir, $"{domain}.key");
            if (File.Exists(domainCrt) && File.Exists(domainKey))
            {
                return (domainCrt, domainKey);
            }

            var certCrt = Path.Combine(narcDir, "certificate.crt");
            var privKey = Path.Combine(narcDir, "private.key");
            if (File.Exists(certCrt) && File.Exists(privKey))
            {
                return (certCrt, privKey);
            }

            // Default fallback
            return (fullchainCrt, privkeyKey);
        }

        /// <summary>
        /// Lê a data de expiração (NotAfter) do certificado no disco via X509Certificate2.
        /// </summary>
        public static DateTime? GetCertificateExpiration(string domain, string sslBasePath = DefaultSslPath)
        {
            try
            {
                var (certPath, _) = GetCertificatePaths(domain, sslBasePath);
                if (!File.Exists(certPath)) return null;

                var pemContent = File.ReadAllText(certPath);
                using var cert = X509Certificate2.CreateFromPem(pemContent);
                return cert.NotAfter.ToUniversalTime();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SSL] [{domain}] Erro ao ler expiração do certificado: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Verifica se o certificado precisa ser renovado (expira em <= daysBefore dias ou já expirou).
        /// </summary>
        public bool NeedsRenewal(string domain, int daysBefore = 30)
        {
            var exp = GetCertificateExpiration(domain, _sslBasePath);
            if (!exp.HasValue) return true; // Ausente ou ilegível: precisa renovar
            return DateTime.UtcNow.AddDays(daysBefore) >= exp.Value;
        }

        /// <summary>
        /// Retorna todos os domínios gerenciados que possuem certificados no diretório base.
        /// </summary>
        public List<string> GetAllManagedDomains()
        {
            var list = new List<string>();
            if (IoDirectory.Exists(_sslBasePath))
            {
                foreach (var dir in IoDirectory.GetDirectories(_sslBasePath))
                {
                    var domain = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(domain) && CheckCertificateExists(domain, _sslBasePath))
                    {
                        list.Add(domain);
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Gera previamente um certificado autoassinado temporário na porta 443 caso o certificado
        /// oficial ainda não exista. Isso evita que a Cloudflare receba "525 SSL Handshake Failed"
        /// no modo "Completa" (Full) enquanto o Let's Encrypt cumpre o desafio ACME na porta 80.
        /// </summary>
        public static void EnsureBootstrapCertificate(string domain, string sslBasePath = DefaultSslPath)
        {
            if (string.IsNullOrWhiteSpace(domain)) return;

            var narcDir = Path.Combine(sslBasePath, domain);
            if (!IoDirectory.Exists(narcDir))
            {
                IoDirectory.CreateDirectory(narcDir);
                TryChmod(narcDir, "755");
            }

            var fullchainCrt = Path.Combine(narcDir, "fullchain.crt");
            var privkeyKey = Path.Combine(narcDir, "privkey.key");
            var domainCrt = Path.Combine(narcDir, $"{domain}.crt");
            var domainKey = Path.Combine(narcDir, $"{domain}.key");

            if (File.Exists(fullchainCrt) && File.Exists(privkeyKey)) return;
            if (File.Exists(domainCrt) && File.Exists(domainKey)) return;

            try
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest($"CN={domain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddDnsName(domain);
                request.CertificateExtensions.Add(sanBuilder.Build());
                var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
                var notAfter = DateTimeOffset.UtcNow.AddDays(30);
                using var cert = request.CreateSelfSigned(notBefore, notAfter);

                var certPem = cert.ExportCertificatePem();
                var keyPem = rsa.ExportPkcs8PrivateKeyPem();

                File.WriteAllText(fullchainCrt, certPem);
                File.WriteAllText(privkeyKey, keyPem);
                File.WriteAllText(domainCrt, certPem);
                File.WriteAllText(domainKey, keyPem);

                TryChmod(fullchainCrt, "644");
                TryChmod(privkeyKey, "600");
                TryChmod(domainCrt, "644");
                TryChmod(domainKey, "600");

                Console.WriteLine($"[SSL] [{domain}] Certificado bootstrap autoassinado provisório gerado para port 443 (Cloudflare Full Mode compatível).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SSL] [{domain}] Aviso: não foi possível gerar bootstrap self-signed cert: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates an SSL certificate natively using ACME v2 (Certes) with HTTP-01 webroot challenge.
        /// </summary>
        public async Task<bool> GenerateAsync(string domain, string? email = null, CancellationToken ct = default)
        {
            var challengeFiles = new List<string>();

            try
            {
                Console.WriteLine($"[SSL] [{domain}] Iniciando geracao nativa de certificado SSL via Certes (ACME v2)...");

                var acme = await GetOrCreateAcmeContextAsync(email, ct);

                // Normaliza o dominio e cria a lista de identifiers
                var domainList = domain.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (domainList.Length == 0)
                {
                    Console.WriteLine($"[SSL] [{domain}] Dominio invalido.");
                    return false;
                }

                // Cria o pedido de certificado (Order)
                var order = await acme.NewOrder(domainList);

                // Obtem autorizacoes e processa desafios HTTP-01
                var authorizations = await order.Authorizations();
                var challengeDir = Path.Combine(_webRootPath, ".well-known", "acme-challenge");

                if (!IoDirectory.Exists(challengeDir))
                {
                    IoDirectory.CreateDirectory(challengeDir);
                }
                TryChmod(challengeDir, "755");

                foreach (var authz in authorizations)
                {
                    ct.ThrowIfCancellationRequested();

                    var authzRes = await authz.Resource();
                    if (authzRes.Status == AuthorizationStatus.Valid)
                    {
                        Console.WriteLine($"[SSL] [{domain}] Autorizacao para {authzRes.Identifier?.Value ?? domain} ja e valida.");
                        continue;
                    }

                    var httpChallenge = await authz.Http();
                    if (httpChallenge == null)
                    {
                        throw new InvalidOperationException($"Desafio HTTP-01 indisponivel para {authzRes.Identifier?.Value ?? domain}");
                    }

                    var token = httpChallenge.Token;
                    var keyAuthz = httpChallenge.KeyAuthz;
                    var challengeFilePath = Path.Combine(challengeDir, token);

                    await File.WriteAllTextAsync(challengeFilePath, keyAuthz, ct);
                    TryChmod(challengeFilePath, "644");
                    challengeFiles.Add(challengeFilePath);

                    Console.WriteLine($"[SSL] [{domain}] Desafio HTTP-01 gravado em {challengeFilePath}");

                    // Solicita validacao ao Let's Encrypt
                    var challenge = await httpChallenge.Validate();

                    // Aguarda resultado da validacao com polling
                    int retries = 0;
                    const int maxRetries = 30; // 30 * 2s = 60s
                    while ((challenge.Status == ChallengeStatus.Pending || challenge.Status == ChallengeStatus.Processing) && retries < maxRetries)
                    {
                        await Task.Delay(2000, ct);
                        challenge = await httpChallenge.Resource();
                        retries++;
                    }

                    if (challenge.Status != ChallengeStatus.Valid)
                    {
                        var errorDetail = challenge.Error?.Detail ?? $"Status={challenge.Status}";
                        throw new InvalidOperationException($"Desafio HTTP-01 falhou para {authzRes.Identifier?.Value ?? domain}: {errorDetail}");
                    }

                    Console.WriteLine($"[SSL] [{domain}] Desafio HTTP-01 validado com sucesso.");
                }

                // Gera nova chave privada e finaliza a ordem baixando o certificado
                Console.WriteLine($"[SSL] [{domain}] Gerando chave privada RSA e finalizando certificado...");
                var privateKey = KeyFactory.NewKey(KeyAlgorithm.RS256, 2048);
                var certChain = await order.Generate(new CsrInfo
                {
                    CommonName = domainList[0]
                }, privateKey);

                // Monta os certificados da cadeia em formato PEM
                var fullChainBuilder = new StringBuilder();
                fullChainBuilder.AppendLine(certChain.Certificate.ToPem().Trim());
                if (certChain.Issuers != null)
                {
                    foreach (var issuer in certChain.Issuers)
                    {
                        fullChainBuilder.AppendLine(issuer.ToPem().Trim());
                    }
                }
                var fullChainPem = fullChainBuilder.ToString().Trim();
                var privateKeyPem = privateKey.ToPem().Trim();

                // Salva em /etc/narc/ssl/{domain}
                var targetDir = Path.Combine(_sslBasePath, domain);
                if (!IoDirectory.Exists(targetDir))
                {
                    IoDirectory.CreateDirectory(targetDir);
                }
                TryChmod(targetDir, "755");

                var crtFile = Path.Combine(targetDir, $"{domain}.crt");
                var keyFile = Path.Combine(targetDir, $"{domain}.key");
                var fullchainFile = Path.Combine(targetDir, "fullchain.crt");
                var privkeyFile = Path.Combine(targetDir, "privkey.key");
                var certFile = Path.Combine(targetDir, "certificate.crt");
                var privateKeyFile = Path.Combine(targetDir, "private.key");

                await File.WriteAllTextAsync(crtFile, fullChainPem, ct);
                await File.WriteAllTextAsync(keyFile, privateKeyPem, ct);
                await File.WriteAllTextAsync(fullchainFile, fullChainPem, ct);
                await File.WriteAllTextAsync(privkeyFile, privateKeyPem, ct);
                await File.WriteAllTextAsync(certFile, fullChainPem, ct);
                await File.WriteAllTextAsync(privateKeyFile, privateKeyPem, ct);

                TryChmod(crtFile, "644");
                TryChmod(keyFile, "600");
                TryChmod(fullchainFile, "644");
                TryChmod(privkeyFile, "600");
                TryChmod(certFile, "644");
                TryChmod(privateKeyFile, "600");

                Console.WriteLine($"[SSL] [{domain}] Certificado gerado e instalado com sucesso em {targetDir}!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SSL] [{domain}] Falha ao gerar certificado SSL: {ex.Message}");
                return false;
            }
            finally
            {
                // Limpeza estrita dos arquivos de desafio temporarios
                foreach (var file in challengeFiles)
                {
                    try
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                            Console.WriteLine($"[SSL] [{domain}] Arquivo temporario de validacao removido: {file}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SSL] [{domain}] Erro ao remover arquivo temporario {file}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Remove certificados de forma nativa sem depender de CLI externa.
        /// </summary>
        public Task<bool> DeleteAsync(string domain, CancellationToken ct = default)
        {
            try
            {
                var targetDir = Path.Combine(_sslBasePath, domain);
                if (IoDirectory.Exists(targetDir))
                {
                    IoDirectory.Delete(targetDir, true);
                    Console.WriteLine($"[SSL] [{domain}] Diretorio de certificados {targetDir} removido.");
                }

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SSL] [{domain}] Falha ao remover certificados: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        private async Task<AcmeContext> GetOrCreateAcmeContextAsync(string? email, CancellationToken ct)
        {
            var serverUri = GetAcmeServerUri();
            var accountKeyFile = Path.Combine(_sslBasePath, "account.key");

            if (File.Exists(accountKeyFile))
            {
                try
                {
                    var keyPem = await File.ReadAllTextAsync(accountKeyFile, ct);
                    var accountKey = KeyFactory.FromPem(keyPem);
                    var acme = new AcmeContext(serverUri, accountKey);
                    await acme.Account();
                    return acme;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SSL] Conta ACME existente invalida, registrando nova: {ex.Message}");
                }
            }

            var newAcme = new AcmeContext(serverUri);
            if (!string.IsNullOrWhiteSpace(email) && email.Contains('@'))
            {
                await newAcme.NewAccount(email.Trim(), termsOfServiceAgreed: true);
            }
            else
            {
                await newAcme.NewAccount(new List<string>(), termsOfServiceAgreed: true);
            }

            try
            {
                if (!IoDirectory.Exists(_sslBasePath))
                {
                    IoDirectory.CreateDirectory(_sslBasePath);
                }
                await File.WriteAllTextAsync(accountKeyFile, newAcme.AccountKey.ToPem(), ct);
                TryChmod(accountKeyFile, "600");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SSL] Aviso: nao foi possivel persistir account.key: {ex.Message}");
            }

            return newAcme;
        }

        private static Uri GetAcmeServerUri()
        {
            var envServer = Environment.GetEnvironmentVariable("ACME_SERVER");
            if (string.Equals(envServer, "staging", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(envServer, "test", StringComparison.OrdinalIgnoreCase))
            {
                return WellKnownServers.LetsEncryptStagingV2;
            }

            if (!string.IsNullOrWhiteSpace(envServer) && Uri.TryCreate(envServer, UriKind.Absolute, out var customUri))
            {
                return customUri;
            }

            return WellKnownServers.LetsEncryptV2;
        }

        private static void TryChmod(string path, string permissions)
        {
            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    if (!File.Exists(path) && !IoDirectory.Exists(path)) return;
                    using var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/chmod",
                        Arguments = $"{permissions} \"{path}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    p?.WaitForExit(2000);
                }
            }
            catch { }
        }
    }
}
