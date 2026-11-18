using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Proxy;
using NarcAgent.Proxy.Services.Nginx;
using Xunit;

namespace NarcAgent.Tests
{
    public class ToggleProxyTests
    {
        [Fact]
        public async Task ResolveConfigFilename_ResolvesVariousFormats()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "nginx_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var configService = new NginxConfigService(tempDir);
                var generator = new NginxConfigGenerator();
                var processService = new NginxProcessService();
                var sslService = new NginxSslService();
                var nginxService = new NginxService(configService, generator, processService, sslService);

                // 1. GUID simples
                var fn1 = await nginxService.ResolveConfigFilenameAsync("abc-123", "example.com");
                Assert.Equal("narc_proxy_abc-123.conf", fn1);

                // 2. Já formatado com narc_proxy_ e .conf
                var fn2 = await nginxService.ResolveConfigFilenameAsync("narc_proxy_xyz-789.conf", "example.com");
                Assert.Equal("narc_proxy_xyz-789.conf", fn2);

                // 3. Arquivo já existente no disco encontrado pelo domínio
                var existingFile = Path.Combine(tempDir, "narc_proxy_custom.conf");
                await File.WriteAllTextAsync(existingFile, "server { server_name mydomain.com; }");

                var fn3 = await nginxService.ResolveConfigFilenameAsync("", "mydomain.com");
                Assert.Equal("narc_proxy_custom.conf", fn3);

                // 4. Arquivo com .bak existente encontrado pelo domínio
                var existingBak = Path.Combine(tempDir, "narc_proxy_deact.conf.bak");
                await File.WriteAllTextAsync(existingBak, "server { server_name deactdomain.com; }");

                var fn4 = await nginxService.ResolveConfigFilenameAsync("", "deactdomain.com");
                Assert.Equal("narc_proxy_deact.conf", fn4);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void GenerateDeactivatedConfig_ServesMaintenanceHtml()
        {
            var generator = new NginxConfigGenerator();
            var deactivated = generator.GenerateDeactivatedConfig("testsite.com", false);

            Assert.Contains("server_name testsite.com;", deactivated);
            Assert.Contains("try_files /narc_deactivated.html =503;", deactivated);
            Assert.DoesNotContain("proxy_pass", deactivated);
        }
    }
}
