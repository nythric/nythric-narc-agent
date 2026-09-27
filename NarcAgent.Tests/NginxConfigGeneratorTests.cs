using NarcAgent.Proxy.Services.Nginx;
using Xunit;

namespace NarcAgent.Tests
{
    public class NginxConfigGeneratorTests
    {
        private readonly NginxConfigGenerator _generator = new();

        [Fact]
        public void CompileTemplate_Substitutes_All_Placeholders()
        {
            var template = @"server {
    listen 80;
    server_name {{domain}};
    location / {
        proxy_pass http://{{target_host}}:{{target_port}};
    }
    location /api {
        proxy_pass http://{{target_host}}:4000;
    }
}";

            var compiled = _generator.CompileTemplate(
                template,
                domain: "meusite.com",
                targetHost: "192.168.1.50",
                targetPort: 3000,
                enabled: true,
                sslAvailable: false
            );

            Assert.Contains("server_name meusite.com;", compiled);
            Assert.Contains("proxy_pass http://192.168.1.50:3000;", compiled);
            Assert.Contains("proxy_pass http://192.168.1.50:4000;", compiled);
            Assert.DoesNotContain("{{domain}}", compiled);
            Assert.DoesNotContain("{{target_host}}", compiled);
            Assert.DoesNotContain("{{target_port}}", compiled);
        }

        [Fact]
        public void SimpleModeChange_UpdatesHostAndPort_PreservesCustomLocations()
        {
            var advancedTemplate = @"server {
    listen 80;
    server_name {{domain}};
    location / {
        proxy_pass http://{{target_host}}:{{target_port}};
    }
    location /grafana/ {
        proxy_pass http://10.0.0.99:3001/;
        proxy_set_header Host $host;
    }
    location /webhook/ {
        proxy_pass http://10.0.0.99:9000/;
    }
}";

            // Step 1: User initially sets port 3000
            var compiledV1 = _generator.CompileTemplate(advancedTemplate, "app.byc.re", "127.0.0.1", 3000, true, false);
            Assert.Contains("proxy_pass http://127.0.0.1:3000;", compiledV1);
            Assert.Contains("location /grafana/", compiledV1);
            Assert.Contains("proxy_pass http://10.0.0.99:3001/;", compiledV1);

            // Step 2: Layman goes to Simple Mode and changes only port to 8080 and host to 10.0.0.5
            var compiledV2 = _generator.CompileTemplate(advancedTemplate, "app.byc.re", "10.0.0.5", 8080, true, false);
            Assert.Contains("proxy_pass http://10.0.0.5:8080;", compiledV2);
            // Custom locations are still completely preserved!
            Assert.Contains("location /grafana/", compiledV2);
            Assert.Contains("proxy_pass http://10.0.0.99:3001/;", compiledV2);
            Assert.Contains("location /webhook/", compiledV2);
            Assert.Contains("proxy_pass http://10.0.0.99:9000/;", compiledV2);
        }

        [Fact]
        public void NullOrEmptyRawConfig_FallsBackToDefaultTemplateWithPlaceholders()
        {
            var compiled = _generator.CompileTemplate(
                null,
                domain: "default.com",
                targetHost: "127.0.0.1",
                targetPort: 5000,
                enabled: true,
                sslAvailable: false
            );

            Assert.Contains("server_name default.com;", compiled);
            Assert.Contains("proxy_pass http://127.0.0.1:5000;", compiled);
            Assert.Contains("location /.well-known/acme-challenge/", compiled);
            Assert.DoesNotContain("{{domain}}", compiled);
            Assert.DoesNotContain("{{target_host}}", compiled);
            Assert.DoesNotContain("{{target_port}}", compiled);
        }

        [Fact]
        public void DisabledProxy_GeneratesDisabledConfig()
        {
            var template = "server { server_name {{domain}}; location / { proxy_pass http://{{target_host}}:{{target_port}}; } }";

            var compiled = _generator.CompileTemplate(
                template,
                domain: "offline.com",
                targetHost: "127.0.0.1",
                targetPort: 80,
                enabled: false,
                sslAvailable: false
            );

            Assert.Contains("narc_deactivated.html =503", compiled);
            Assert.Contains("server_name offline.com;", compiled);
        }

        [Fact]
        public void VariationsWithWhitespaceAndSynonyms_AreHandled()
        {
            var template = @"server {
    server_name {{ domain }};
    proxy_pass http://{{ host }}:{{ port }};
}";

            var compiled = _generator.ApplyPlaceholders(template, "space.com", "1.2.3.4", 9999);

            Assert.Contains("server_name space.com;", compiled);
            Assert.Contains("proxy_pass http://1.2.3.4:9999;", compiled);
        }
    }
}
