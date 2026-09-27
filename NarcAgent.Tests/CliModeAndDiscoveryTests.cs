using System;
using System.IO;
using System.Threading.Tasks;
using NarcAgent.Proxy.Services.Nginx;
using Xunit;

namespace NarcAgent.Tests
{
    public class CliModeAndDiscoveryTests : IDisposable
    {
        private readonly string _tempDir;

        public CliModeAndDiscoveryTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "NarcAgentTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public async Task FindFilenameByDomainAsync_FindsNarcProxyConf_ByServerName()
        {
            var configService = new NginxConfigService(_tempDir);
            var confContent = @"server {
    listen 80;
    server_name api.byc.re;

    location / {
        proxy_pass http://127.0.0.1:5000;
    }
}";
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "narc_proxy_99.conf"), confContent);

            var filename = await configService.FindFilenameByDomainAsync("api.byc.re");

            Assert.Equal("narc_proxy_99.conf", filename);
        }

        [Fact]
        public async Task FindFilenameByDomainAsync_FindsCustomConf_WhenNotPrefixedWithNarcProxy()
        {
            var configService = new NginxConfigService(_tempDir);
            var confContent = @"server {
    listen 80;
    server_name api.byc.re;

    location / {
        proxy_pass http://127.0.0.1:8080;
    }
}";
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "api.byc.re.conf"), confContent);

            var filename = await configService.FindFilenameByDomainAsync("api.byc.re");

            Assert.Equal("api.byc.re.conf", filename);
        }

        [Fact]
        public async Task FindFilenameByDomainAsync_FindsMultiDomainServerName()
        {
            var configService = new NginxConfigService(_tempDir);
            var confContent = @"server {
    listen 80;
    server_name byc.re   api.byc.re   cdn.byc.re;

    location / {
        proxy_pass http://127.0.0.1:3000;
    }
}";
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "narc_proxy_10.conf"), confContent);

            var filename = await configService.FindFilenameByDomainAsync("api.byc.re");

            Assert.Equal("narc_proxy_10.conf", filename);
        }

        [Fact]
        public async Task FindFilenameByDomainAsync_ReturnsNull_WhenDomainDoesNotExist()
        {
            var configService = new NginxConfigService(_tempDir);
            var confContent = @"server {
    listen 80;
    server_name otherdomain.com;
}";
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "narc_proxy_1.conf"), confContent);

            var filename = await configService.FindFilenameByDomainAsync("api.byc.re");

            Assert.Null(filename);
        }

        [Fact]
        public void InjectSslIntoConfig_TransformsHttpConfigToHttps_ForApiBycRe()
        {
            var httpConf = @"server {
    listen 80;
    server_name api.byc.re;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }
}";
            var certPath = "/etc/narc/ssl/api.byc.re/fullchain.crt";
            var keyPath = "/etc/narc/ssl/api.byc.re/privkey.key";

            var updated = NginxConfigModifier.InjectSslIntoConfig(httpConf, "api.byc.re", certPath, keyPath);

            // Verifies 443 server block was created with SSL directives
            Assert.Contains("listen 443 ssl http2;", updated);
            Assert.Contains("server_name api.byc.re;", updated);
            Assert.Contains($"ssl_certificate {certPath};", updated);
            Assert.Contains($"ssl_certificate_key {keyPath};", updated);
            Assert.Contains("proxy_pass http://127.0.0.1:5000;", updated);

            // Verifies port 80 now redirects with 301 to https
            Assert.Contains("return 301 https://$host$request_uri;", updated);
            Assert.Contains(".well-known/acme-challenge", updated);
        }
    }
}
