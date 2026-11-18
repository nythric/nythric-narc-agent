using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Proxy.Models;
using NarcAgent.Proxy.Services.Nginx;
using Xunit;

namespace NarcAgent.Tests
{
    public class ReconciliationAndCleanupTests
    {
        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData("\"true\"", true)]
        [InlineData("\"false\"", false)]
        [InlineData("\"1\"", true)]
        [InlineData("\"0\"", false)]
        [InlineData("\"yes\"", true)]
        public void FlexibleBoolConverter_ParsesVariousFormats(string jsonValue, bool expected)
        {
            var json = $"{{\"enabled\": {jsonValue}}}";
            var proxy = JsonSerializer.Deserialize<DatabaseProxy>(json);

            Assert.NotNull(proxy);
            Assert.Equal(expected, proxy.Enabled);
        }

        [Fact]
        public void ExtractDomainsFromConfigContent_ExtractsAllServerNames()
        {
            var config = @"
server {
    listen 80;
    server_name byc.re www.byc.re;
}

server {
    listen 443 ssl;
    server_name panel.byc.re _;
}
";
            var domains = NginxConfigService.ExtractDomainsFromConfigContent(config);

            Assert.Contains("byc.re", domains);
            Assert.Contains("www.byc.re", domains);
            Assert.Contains("panel.byc.re", domains);
            Assert.DoesNotContain("_", domains); // Wildcard should be ignored
        }

        [Fact]
        public void CreateSslBlockFromHttp_HandlesNestedBraces_WithoutCorruptingSyntax()
        {
            var config = @"server {
    listen 80;
    server_name myapp.byc.re;

    location / {
        if ($request_method = POST) {
            return 405;
        }
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
    }
}";

            var certPath = "/etc/narc/ssl/myapp.byc.re/fullchain.crt";
            var keyPath = "/etc/narc/ssl/myapp.byc.re/privkey.key";

            var result = NginxConfigModifier.InjectSslIntoConfig(config, "myapp.byc.re", certPath, keyPath);

            Assert.Contains("listen 443 ssl", result);
            Assert.Contains($"ssl_certificate {certPath};", result);
            Assert.Contains($"ssl_certificate_key {keyPath};", result);
            Assert.Contains("return 301 https://$host$request_uri;", result);
            Assert.Contains(".well-known/acme-challenge", result);

            // Verifies the 443 block preserved the nested braces location
            Assert.Contains("proxy_pass http://127.0.0.1:3000;", result);
            Assert.Contains("if ($request_method = POST)", result);
        }

        [Fact]
        public void DatabaseProxy_DeserializesStandardApiJson()
        {
            var json = @"[
                {
                    ""id"": ""015f9c1a-c302-48cc-955e-2666a1427617"",
                    ""agent_id"": ""agent-123"",
                    ""domain"": ""byc.re"",
                    ""target_host"": ""127.0.0.1"",
                    ""target_port"": 3000,
                    ""enabled"": true,
                    ""force_ssl"": true,
                    ""ssl_issued"": true,
                    ""is_custom"": false
                },
                {
                    ""id"": ""9d5b4a9d-6bff-4dbd-9c2a-d523b6fa4e25"",
                    ""agent_id"": ""agent-123"",
                    ""domain"": ""link.byc.re"",
                    ""target_host"": ""127.0.0.1"",
                    ""target_port"": ""8080"",
                    ""enabled"": ""1"",
                    ""force_ssl"": ""true"",
                    ""ssl_issued"": false
                }
            ]";

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            };

            var proxies = JsonSerializer.Deserialize<List<DatabaseProxy>>(json, opts);

            Assert.NotNull(proxies);
            Assert.Equal(2, proxies.Count);
            Assert.Equal("byc.re", proxies[0].Domain);
            Assert.Equal(3000, proxies[0].TargetPort);
            Assert.True(proxies[0].Enabled);
            Assert.Equal("link.byc.re", proxies[1].Domain);
            Assert.Equal(8080, proxies[1].TargetPort);
            Assert.True(proxies[1].Enabled);
        }

        [Fact]
        public void IsNarcManagedConfig_IdentifiesCandidateFilesCorrectly()
        {
            // Narc files with prefix
            Assert.True(NginxConfigService.IsNarcManagedConfig("narc_proxy_123.conf", ""));
            Assert.True(NginxConfigService.IsNarcManagedConfig("narc_proxy_smm.nesk.lol.conf", ""));

            // Custom domain named file with Narc markers
            var narcContent = @"server {
    listen 80;
    server_name smm.nesk.lol;
    location / {
        proxy_pass http://127.0.0.1:4000;
        proxy_set_header Host $host;
    }
}";
            Assert.True(NginxConfigService.IsNarcManagedConfig("smm.nesk.lol.conf", narcContent));

            // Standard Nginx system default without proxy
            var systemDefault = @"server {
    listen 80 default_server;
    server_name _;
    root /var/www/html;
    index index.html;
}";
            Assert.False(NginxConfigService.IsNarcManagedConfig("default.conf", systemDefault));
        }

        [Theory]
        [InlineData("listen 443;", true)]
        [InlineData("listen 443 ssl;", true)]
        [InlineData("    listen 443 ssl http2;", true)]
        [InlineData("listen [::]:443 ssl http2;", true)]
        [InlineData("listen 0.0.0.0:443;", true)]
        [InlineData("# listen 443;", false)]
        [InlineData("# listen 443 ssl;", false)]
        [InlineData("    # listen 443 ssl http2;", false)]
        [InlineData("proxy_set_header X-Forwarded-Port 443;", false)]
        [InlineData("# Configuração NGINX porta 443", false)]
        [InlineData("listen 80;", false)]
        public void HasActivePort443_DistinguishesActiveFromCommented(string line, bool expected)
        {
            var config = $"server {{\n    {line}\n    server_name test.com;\n}}";
            Assert.Equal(expected, NginxConfigModifier.HasActivePort443(config));
        }

        [Fact]
        public void InjectSslIntoConfig_WhenCommentedListen443ExistsInPort80_CreatesActivePort443Block()
        {
            var configWithComment = @"server {
    listen 80;
    # listen 443 ssl http2;
    server_name link.byc.re;

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
    }
}";
            var certPath = "/etc/narc/ssl/link.byc.re/fullchain.crt";
            var keyPath = "/etc/narc/ssl/link.byc.re/privkey.key";

            var result = NginxConfigModifier.InjectSslIntoConfig(configWithComment, "link.byc.re", certPath, keyPath);

            // Deve possuir diretiva listen 443 ativa (não comentada)
            Assert.True(NginxConfigModifier.HasActivePort443(result));
            Assert.Contains($"ssl_certificate {certPath};", result);
            Assert.Contains($"ssl_certificate_key {keyPath};", result);
            Assert.Contains("return 301 https://$host$request_uri;", result);
            Assert.Contains("proxy_pass http://127.0.0.1:8080;", result);
        }

        [Fact]
        public void CompileTemplate_WhenRawConfigHasOnlyPort80_AndSslAvailable_InjectsActivePort443()
        {
            var httpOnlyTemplate = @"server {
    listen 80;
    server_name {{domain}};

    location / {
        proxy_pass http://{{target_host}}:{{target_port}};
    }
}";
            var generator = new NginxConfigGenerator();
            // sslAvailable=true deve forçar a geração de bootstrap/cert e injetar bloco 443
            var result = generator.CompileTemplate(httpOnlyTemplate, "byc.re", "127.0.0.1", 3000, true, true);

            Assert.True(NginxConfigModifier.HasActivePort443(result));
            Assert.Contains("listen 443 ssl http2;", result);
            Assert.Contains("server_name byc.re;", result);
            Assert.Contains("proxy_pass http://127.0.0.1:3000;", result);
        }

        [Fact]
        public void SanitizeConfig_RemovesDuplicateLocationRoot_FromSslBlock()
        {
            var configWithDuplicateIn443 = @"server {
    listen 80;
    server_name byc.re;

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        return 301 https://$host$request_uri;
    }
}

server {
    listen 443 ssl http2;
    server_name byc.re;

    ssl_certificate /etc/narc/ssl/byc.re/fullchain.crt;
    ssl_certificate_key /etc/narc/ssl/byc.re/privkey.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;

    location / {
        return 301 https://$host$request_uri;
    }

    location / {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}";

            var sanitized = NginxConfigModifier.SanitizeConfig(configWithDuplicateIn443);

            // No bloco 443 não deve haver redirect https:// e deve haver exatamente uma location / com proxy_pass
            var rootLocationMatches = System.Text.RegularExpressions.Regex.Matches(sanitized, @"\blocation\s+/\s*\{");
            // 1 no bloco 80 e 1 no bloco 443 = total 2 em todo o arquivo
            Assert.Equal(2, rootLocationMatches.Count);

            // Bloco 443 deve conter o proxy_pass
            Assert.Contains("proxy_pass http://127.0.0.1:3000;", sanitized);

            // Não deve haver loop de redirect no bloco 443
            var serverBlocks = NginxConfigModifier.ExtractServerBlocks(sanitized);
            var block443 = serverBlocks.First(b => b.IsPort443);
            Assert.DoesNotContain("return 301", block443.Content);
            Assert.Contains("proxy_pass http://127.0.0.1:3000;", block443.Content);
        }

        [Fact]
        public void SanitizeConfig_CleansPort80RedirectBlock_RemovingLeftoverProxyPass()
        {
            var configWithDuplicateIn80 = @"server {
    listen 80;
    server_name byc.re;

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        return 301 https://$host$request_uri;
    }

    location / {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
    }
}";

            var sanitized = NginxConfigModifier.SanitizeConfig(configWithDuplicateIn80);

            // O bloco 80 deve conter acme-challenge e return 301, sem proxy_pass
            Assert.Contains("location /.well-known/acme-challenge/", sanitized);
            Assert.Contains("return 301 https://$host$request_uri;", sanitized);
            Assert.DoesNotContain("proxy_pass http://127.0.0.1:3000;", sanitized);

            var rootLocationMatches = System.Text.RegularExpressions.Regex.Matches(sanitized, @"\blocation\s+/\s*\{");
            Assert.Single(rootLocationMatches);
        }

        [Fact]
        public void InjectSslIntoConfig_OnConfigWithDuplicateLocations_ProducesCleanValidNginxConfig()
        {
            // Simula o arquivo corrompido que existia no disco do usuário com duplicações
            var corruptedInput = @"server {
    listen 80;
    server_name byc.re;

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        return 301 https://$host$request_uri;
    }

    location / {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
    }
}";
            var cert = "/etc/narc/ssl/byc.re/fullchain.crt";
            var key = "/etc/narc/ssl/byc.re/privkey.key";

            var result = NginxConfigModifier.InjectSslIntoConfig(corruptedInput, "byc.re", cert, key);

            // Validações estruturais:
            Assert.True(NginxConfigModifier.HasActivePort443(result));

            var serverBlocks = NginxConfigModifier.ExtractServerBlocks(result);
            Assert.Equal(2, serverBlocks.Count);

            var b80 = serverBlocks.First(b => b.IsPort80);
            var b443 = serverBlocks.First(b => b.IsPort443);

            // Bloco 80: tem apenas acme-challenge e return 301
            var locs80 = NginxConfigModifier.ExtractLocations(b80.Content);
            Assert.Equal(2, locs80.Count);
            Assert.Contains(locs80, l => l.PathKey.Contains("acme-challenge"));
            Assert.Contains(locs80, l => l.PathKey == "/" && l.Body.Contains("return 301"));

            // Bloco 443: tem apenas uma location / com proxy_pass
            var locs443 = NginxConfigModifier.ExtractLocations(b443.Content);
            Assert.Single(locs443);
            Assert.Equal("/", locs443[0].PathKey);
            Assert.Contains("proxy_pass http://127.0.0.1:3000;", locs443[0].Body);
            Assert.DoesNotContain("return 301", locs443[0].Body);
        }

        [Fact]
        public async Task ProxySyncService_ReportSslStatusToApiAsync_ReturnsZeroWhenNoCertificates()
        {
            var tempConf = Path.Combine(Path.GetTempPath(), "narc_test_conf_" + Guid.NewGuid());
            Directory.CreateDirectory(tempConf);

            try
            {
                var cfg = new NginxConfigService(tempConf);
                var gen = new NginxConfigGenerator();
                var proc = new NginxProcessService();
                var ssl = new NginxSslService();
                var svc = new NginxService(cfg, gen, proc, ssl);
                var sync = new NarcAgent.Proxy.Services.ProxySyncService(svc);

                // Deve retornar 0 graciosamente sem lançar exceção
                var reported = await sync.ReportSslStatusToApiAsync(apiUrl: "http://127.0.0.1:9999", agentId: "test-agent");
                Assert.Equal(0, reported);
            }
            finally
            {
                if (Directory.Exists(tempConf)) Directory.Delete(tempConf, true);
            }
        }
    }
}
