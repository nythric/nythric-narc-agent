using System;
using System.IO;
using System.Text;

namespace NarcAgent.Proxy.Services.Nginx
{
    public class NginxConfigGenerator
    {
        /// <summary>
        /// Compiles the NGINX configuration based on the raw template string (raw_config)
        /// received from the API, substituting dynamic placeholders with actual values:
        /// {{target_host}}, {{target_port}}, {{domain}}, {{ssl_certificate}}, {{ssl_certificate_key}}.
        /// </summary>
        public string CompileTemplate(string? rawConfig, string domain, string targetHost, int targetPort, bool enabled, bool sslAvailable)
        {
            if (!enabled)
            {
                return GenerateDisabledConfig(domain, sslAvailable);
            }

            bool certExists = !string.IsNullOrEmpty(domain) && HasCertificate(domain);

            // Se o rawConfig estiver vazio ou em branco, gera o template base com placeholders
            string template = rawConfig ?? string.Empty;
            if (string.IsNullOrWhiteSpace(template))
            {
                template = GetDefaultTemplate(sslAvailable && certExists);
            }

            // Se o template contém diretivas SSL mas o certificado ainda não existe no disco,
            // gera temporariamente a configuração bootstrap HTTP para que o desafio ACME possa ser cumprido.
            if (!certExists && (template.Contains("ssl_certificate", StringComparison.OrdinalIgnoreCase) || template.Contains("listen 443", StringComparison.OrdinalIgnoreCase)))
            {
                return GenerateHttpBootstrapConfig(domain, targetHost, targetPort);
            }

            return ApplyPlaceholders(template, domain, targetHost, targetPort);
        }

        /// <summary>
        /// Substitui placeholders como {{target_host}}, {{target_port}}, {{domain}}, etc.
        /// </summary>
        public string ApplyPlaceholders(string template, string domain, string targetHost, int targetPort)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;

            var (certPath, keyPath) = NginxSslService.GetCertificatePaths(domain);

            var result = template
                .Replace("{{target_host}}", targetHost)
                .Replace("{{target_port}}", targetPort.ToString())
                .Replace("{{domain}}", domain)
                .Replace("{{ssl_certificate}}", certPath)
                .Replace("{{ssl_certificate_key}}", keyPath)
                // Suporte a variações com espaços ou sinônimos
                .Replace("{{ target_host }}", targetHost)
                .Replace("{{ target_port }}", targetPort.ToString())
                .Replace("{{ domain }}", domain)
                .Replace("{{ host }}", targetHost)
                .Replace("{{host}}", targetHost)
                .Replace("{{ port }}", targetPort.ToString())
                .Replace("{{port}}", targetPort.ToString())
                .Replace("{{ ssl_certificate }}", certPath)
                .Replace("{{ ssl_certificate_key }}", keyPath);

            return result;
        }

        /// <summary>
        /// Backward compatibility overload
        /// </summary>
        public string Generate(string domain, string targetHost, int targetPort, bool enabled, bool sslAvailable, string? rawConfig = null)
        {
            return CompileTemplate(rawConfig, domain, targetHost, targetPort, enabled, sslAvailable);
        }

        public static string GetDefaultTemplate(bool ssl = false)
        {
            if (ssl)
            {
                return @"# ==============================================================================
# Configuração NGINX com SSL (Template Dinâmico)
# Variáveis disponíveis: {{domain}}, {{target_host}}, {{target_port}}
# ==============================================================================

server {
    listen 80;
    server_name {{domain}};

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        return 301 https://$host$request_uri;
    }
}

server {
    listen 443 ssl http2;
    server_name {{domain}};

    ssl_certificate /etc/letsencrypt/live/{{domain}}/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/{{domain}}/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;

    location / {
        proxy_pass http://{{target_host}}:{{target_port}};
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection ""upgrade"";
    }
}
";
            }

            return @"# ==============================================================================
# Configuração NGINX (Template Dinâmico)
# Variáveis disponíveis: {{domain}}, {{target_host}}, {{target_port}}
# ==============================================================================

server {
    listen 80;
    server_name {{domain}};

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    location / {
        proxy_pass http://{{target_host}}:{{target_port}};
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection ""upgrade"";
    }
}
";
        }

        private static bool HasCertificate(string domain)
        {
            return NginxSslService.CheckCertificateExists(domain);
        }

        private string GenerateHttpBootstrapConfig(string domain, string targetHost, int targetPort)
        {
            var sb = new StringBuilder();
            sb.AppendLine("server {");
            sb.AppendLine("    listen 80;");
            sb.AppendLine($"    server_name {domain};");
            sb.AppendLine();
            sb.AppendLine("    location /.well-known/acme-challenge/ {");
            sb.AppendLine("        root /var/www/html;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    location / {");
            sb.AppendLine($"        proxy_pass http://{targetHost}:{targetPort};");
            sb.AppendLine("        proxy_set_header Host $host;");
            sb.AppendLine("        proxy_set_header X-Real-IP $remote_addr;");
            sb.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
            sb.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");
            sb.AppendLine("        proxy_http_version 1.1;");
            sb.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
            sb.AppendLine("        proxy_set_header Connection \"upgrade\";");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenerateDisabledConfig(string domain, bool sslAvailable)
        {
            var sb = new StringBuilder();
            
            bool hasCert = !string.IsNullOrEmpty(domain) && HasCertificate(domain);

            if (sslAvailable && hasCert)
            {
                var (certPath, keyPath) = NginxSslService.GetCertificatePaths(domain);
                sb.AppendLine("server {");
                sb.AppendLine("    listen 443 ssl http2;");
                sb.AppendLine($"    server_name {domain};");
                sb.AppendLine();
                sb.AppendLine($"    ssl_certificate {certPath};");
                sb.AppendLine($"    ssl_certificate_key {keyPath};");
                sb.AppendLine();
                sb.AppendLine("    location / {");
                sb.AppendLine("        root /var/www/html;");
                sb.AppendLine("        try_files /narc_deactivated.html =503;");
                sb.AppendLine("        add_header Cache-Control \"no-store, no-cache, must-revalidate\";");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();
            }

            sb.AppendLine("server {");
            sb.AppendLine("    listen 80;");
            sb.AppendLine($"    server_name {domain};");
            sb.AppendLine();
            sb.AppendLine("    location /.well-known/acme-challenge/ {");
            sb.AppendLine("        root /var/www/html;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    location / {");
            sb.AppendLine("        root /var/www/html;");
            sb.AppendLine("        try_files /narc_deactivated.html =503;");
            sb.AppendLine("        add_header Cache-Control \"no-store, no-cache, must-revalidate\";");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }
}
