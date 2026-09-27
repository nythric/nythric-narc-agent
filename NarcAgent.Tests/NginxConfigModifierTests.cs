using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Command;
using NarcAgent.Command.Models;
using NarcAgent.Core.Services;
using NarcAgent.Proxy;
using NarcAgent.Proxy.Services.Nginx;
using Xunit;

namespace NarcAgent.Tests
{
    public class NginxConfigModifierTests
    {
        [Fact]
        public void UpdateRootProxyPass_UpdatesHostAndPort_InRootLocationOnly()
        {
            var conf = @"server {
    listen 80;
    server_name mysite.com;

    location / {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }

    location /api {
        proxy_pass http://10.0.0.99:4000;
        proxy_set_header Host $host;
    }

    location /grafana/ {
        proxy_pass http://10.0.0.99:3001/;
    }
}";

            var result = NginxConfigModifier.UpdateRootProxyPass(conf, "192.168.1.100", 8080);

            // Verifies location / was updated
            Assert.Contains("proxy_pass http://192.168.1.100:8080;", result);
            Assert.DoesNotContain("proxy_pass http://127.0.0.1:3000;", result);

            // Verifies other locations were NOT touched
            Assert.Contains("location /api", result);
            Assert.Contains("proxy_pass http://10.0.0.99:4000;", result);
            Assert.Contains("location /grafana/", result);
            Assert.Contains("proxy_pass http://10.0.0.99:3001/;", result);

            // Verifies headers inside location / were preserved
            Assert.Contains("proxy_set_header Host $host;", result);
            Assert.Contains("proxy_set_header X-Real-IP $remote_addr;", result);
        }

        [Fact]
        public void UpdateRootProxyPass_PreservesTrailingSlash_IfPresent()
        {
            var conf = @"server {
    listen 80;
    server_name mysite.com;

    location / {
        proxy_pass http://127.0.0.1:3000/;
    }
}";

            var result = NginxConfigModifier.UpdateRootProxyPass(conf, "10.0.0.1", 5000);

            Assert.Contains("proxy_pass http://10.0.0.1:5000/;", result);
        }

        [Fact]
        public void UpdateRootProxyPass_InSslConfig_UpdatesOnly443ProxyPass()
        {
            var conf = @"server {
    listen 80;
    server_name mysite.com;

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        return 301 https://$host$request_uri;
    }
}

server {
    listen 443 ssl http2;
    server_name mysite.com;

    ssl_certificate /etc/narc/ssl/mysite.com/fullchain.crt;
    ssl_certificate_key /etc/narc/ssl/mysite.com/privkey.key;

    location / {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
    }

    location /webhook/ {
        proxy_pass http://127.0.0.1:9000/;
    }
}";

            var result = NginxConfigModifier.UpdateRootProxyPass(conf, "10.0.0.2", 8443);

            // Port 80 redirect is untouched
            Assert.Contains("return 301 https://$host$request_uri;", result);

            // Port 443 location / is updated
            Assert.Contains("proxy_pass http://10.0.0.2:8443;", result);

            // Custom location is untouched
            Assert.Contains("proxy_pass http://127.0.0.1:9000/;", result);

            // SSL certificate lines are untouched
            Assert.Contains("ssl_certificate /etc/narc/ssl/mysite.com/fullchain.crt;", result);
        }

        [Fact]
        public void InjectSslIntoConfig_InjectsCertAndKey_IntoExisting443Block()
        {
            var conf = @"server {
    listen 80;
    server_name mysite.com;

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        return 301 https://$host$request_uri;
    }
}

server {
    listen 443 ssl http2;
    server_name mysite.com;

    location / {
        proxy_pass http://127.0.0.1:3000;
    }
}";

            var certPath = "/etc/narc/ssl/mysite.com/fullchain.crt";
            var keyPath = "/etc/narc/ssl/mysite.com/privkey.key";

            var result = NginxConfigModifier.InjectSslIntoConfig(conf, "mysite.com", certPath, keyPath);

            Assert.Contains($"ssl_certificate {certPath};", result);
            Assert.Contains($"ssl_certificate_key {keyPath};", result);
            Assert.Contains("ssl_protocols TLSv1.2 TLSv1.3;", result);
            Assert.Contains("proxy_pass http://127.0.0.1:3000;", result);
        }

        [Fact]
        public void InjectSslIntoConfig_ReplacesExistingPaths_In443Block()
        {
            var conf = @"server {
    listen 443 ssl http2;
    server_name mysite.com;

    ssl_certificate /old/path/cert.pem;
    ssl_certificate_key /old/path/key.pem;

    location / {
        proxy_pass http://127.0.0.1:3000;
    }
}";

            var newCert = "/etc/narc/ssl/mysite.com/fullchain.crt";
            var newKey = "/etc/narc/ssl/mysite.com/privkey.key";

            var result = NginxConfigModifier.InjectSslIntoConfig(conf, "mysite.com", newCert, newKey);

            Assert.Contains($"ssl_certificate {newCert};", result);
            Assert.Contains($"ssl_certificate_key {newKey};", result);
            Assert.DoesNotContain("/old/path/cert.pem", result);
            Assert.DoesNotContain("/old/path/key.pem", result);
        }

        [Fact]
        public void InjectSslIntoConfig_Creates443Block_WhenOnly80Exists()
        {
            var conf = @"server {
    listen 80;
    server_name plain.com;

    location / {
        proxy_pass http://127.0.0.1:4000;
        proxy_set_header Host $host;
    }

    location /docs {
        proxy_pass http://127.0.0.1:4001;
    }
}";

            var certPath = "/etc/narc/ssl/plain.com/fullchain.crt";
            var keyPath = "/etc/narc/ssl/plain.com/privkey.key";

            var result = NginxConfigModifier.InjectSslIntoConfig(conf, "plain.com", certPath, keyPath);

            // Verifies 443 block was generated
            Assert.Contains("listen 443 ssl http2;", result);
            Assert.Contains($"ssl_certificate {certPath};", result);
            Assert.Contains($"ssl_certificate_key {keyPath};", result);

            // Verifies custom locations were copied to 443
            Assert.Contains("location /docs", result);

            // Verifies port 80 now redirects
            Assert.Contains("return 301 https://$host$request_uri;", result);
            Assert.Contains(".well-known/acme-challenge", result);
        }

        [Fact]
        public void BuildEnvelope_EnsuresSuccessAndMessage_OnAllKinds()
        {
            // Ack
            var ack = CommandResult.Ack("Operation successful");
            Assert.True(ack.Success);
            Assert.Equal("Operation successful", ack.Message);

            // Error
            var err = CommandResult.Error("Something went wrong");
            Assert.False(err.Success);
            Assert.Equal("Something went wrong", err.Message);

            // PluginError
            var perr = CommandResult.PluginError("test_action", "Error detail", "Stack trace");
            Assert.False(perr.Success);
            Assert.Contains("Error detail", perr.Message);
        }

        [Fact]
        public void UpdateRootProxyPass_HandlesNestedBracesInsideLocationRoot()
        {
            var conf = @"server {
    listen 80;
    server_name nested.com;

    location / {
        if ($request_method = POST) {
            return 405;
        }
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
    }
}";

            var result = NginxConfigModifier.UpdateRootProxyPass(conf, "10.0.0.15", 9000);

            Assert.Contains("proxy_pass http://10.0.0.15:9000;", result);
            Assert.Contains("return 405;", result);
            Assert.Contains("proxy_set_header Host $host;", result);
        }

        [Fact]
        public void UpdateRootProxyPass_HandlesExactLocationEqualsSlash()
        {
            var conf = @"server {
    listen 80;
    server_name exact.com;

    location = / {
        proxy_pass http://127.0.0.1:3000;
    }
}";

            var result = NginxConfigModifier.UpdateRootProxyPass(conf, "10.0.0.8", 7777);

            Assert.Contains("proxy_pass http://10.0.0.8:7777;", result);
        }

        [Fact]
        public void UpdateRootProxyPass_HandlesPlaceholdersInTemplate()
        {
            var conf = @"server {
    listen 80;
    server_name templated.com;

    location / {
        proxy_pass http://{{target_host}}:{{target_port}};
    }
}";

            var result = NginxConfigModifier.UpdateRootProxyPass(conf, "172.16.0.5", 4444);

            Assert.Contains("proxy_pass http://172.16.0.5:4444;", result);
        }

        [Fact]
        public void InjectSslIntoConfig_NormalizesWindowsBackslashesToForwardSlashes()
        {
            var conf = @"server {
    listen 443 ssl http2;
    server_name win.com;

    location / {
        proxy_pass http://127.0.0.1:3000;
    }
}";

            var certPath = @"C:\etc\narc\ssl\win.com\fullchain.crt";
            var keyPath = @"C:\etc\narc\ssl\win.com\privkey.key";

            var result = NginxConfigModifier.InjectSslIntoConfig(conf, "win.com", certPath, keyPath);

            Assert.Contains("ssl_certificate C:/etc/narc/ssl/win.com/fullchain.crt;", result);
            Assert.Contains("ssl_certificate_key C:/etc/narc/ssl/win.com/privkey.key;", result);
            Assert.DoesNotContain(@"\", result);
        }

        [Fact]
        public void ProxyPlugin_SupportsUpdateProxyAndGenerateSslActions()
        {
            var plugin = new ProxyPlugin();
            Assert.Contains("update_proxy", plugin.SupportedActions);
            Assert.Contains("generate_ssl", plugin.SupportedActions);
            Assert.Contains("get_config", plugin.SupportedActions);
            Assert.Contains("save_config", plugin.SupportedActions);
            Assert.Contains("delete_proxy", plugin.SupportedActions);
            Assert.Contains("toggle_config", plugin.SupportedActions);
        }
    }
}
