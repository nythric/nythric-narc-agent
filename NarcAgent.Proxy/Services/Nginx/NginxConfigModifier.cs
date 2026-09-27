using System;
using System.Text;
using System.Text.RegularExpressions;

namespace NarcAgent.Proxy.Services.Nginx
{
    public static class NginxConfigModifier
    {
        /// <summary>
        /// Realiza a alteração In-Place de IP e Porta na diretiva proxy_pass especificamente
        /// dentro do bloco "location / {" (ou "location = / {"), preservando 100% de quaisquer
        /// outras rotas customizadas (ex: location /api, location /grafana) e outros parâmetros.
        /// </summary>
        public static string UpdateRootProxyPass(string configContent, string targetHost, int targetPort)
        {
            if (string.IsNullOrWhiteSpace(configContent)) return configContent;

            // Busca especificamente declarações de 'location / {' ou 'location = / {'
            var locationRegex = new Regex(@"\blocation\s+(?:=\s+)?/\s*\{");
            var matches = locationRegex.Matches(configContent);
            if (matches.Count == 0) return configContent;

            var sb = new StringBuilder();
            int lastIndex = 0;

            foreach (Match match in matches)
            {
                if (match.Index < lastIndex) continue;

                sb.Append(configContent.Substring(lastIndex, match.Index - lastIndex));

                int openBraceIndex = configContent.IndexOf('{', match.Index);
                int depth = 1;
                int closeBraceIndex = -1;

                for (int i = openBraceIndex + 1; i < configContent.Length; i++)
                {
                    if (configContent[i] == '{')
                    {
                        depth++;
                    }
                    else if (configContent[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closeBraceIndex = i;
                            break;
                        }
                    }
                }

                if (closeBraceIndex == -1)
                {
                    sb.Append(configContent.Substring(match.Index));
                    lastIndex = configContent.Length;
                    break;
                }

                string block = configContent.Substring(match.Index, closeBraceIndex - match.Index + 1);

                // Expressão Regular buscando especificamente a linha proxy_pass http://...
                // Substitui apenas o IP e a porta, preservando eventual barra ou rota final
                var proxyPassRegex = new Regex(@"(\bproxy_pass\s+http://)[^/;\s]+(/?[^;]*;)", RegexOptions.Multiline);
                string updatedBlock = proxyPassRegex.Replace(block, m =>
                {
                    string trailing = m.Groups[2].Value; // e.g. ";" ou "/;"
                    return $"{m.Groups[1].Value}{targetHost}:{targetPort}{trailing}";
                });

                sb.Append(updatedBlock);
                lastIndex = closeBraceIndex + 1;
            }

            if (lastIndex < configContent.Length)
            {
                sb.Append(configContent.Substring(lastIndex));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Procura (via Regex) onde injetar o ssl_certificate e ssl_certificate_key dentro
        /// do server block da porta 443. Se já tiver caminhos de certificado, substitui;
        /// se não tiver, injeta preservando o restante da configuração.
        /// Caso não exista bloco 443, cria-o a partir do bloco 80 existente.
        /// </summary>
        public static string InjectSslIntoConfig(string configContent, string domain, string certPath, string keyPath)
        {
            if (string.IsNullOrWhiteSpace(configContent)) return configContent;

            // Garante barras normais (forward slashes) para caminhos do Nginx
            certPath = certPath.Replace('\\', '/');
            keyPath = keyPath.Replace('\\', '/');

            var serverRegex = new Regex(@"\bserver\s*\{");
            var matches = serverRegex.Matches(configContent);
            if (matches.Count == 0) return configContent;

            int sslBlockIndex = -1;
            string? sslBlockContent = null;
            int sslCloseBrace = -1;

            for (int mIndex = 0; mIndex < matches.Count; mIndex++)
            {
                var match = matches[mIndex];
                int openBrace = configContent.IndexOf('{', match.Index);
                int depth = 1;
                int closeBrace = -1;

                for (int i = openBrace + 1; i < configContent.Length; i++)
                {
                    if (configContent[i] == '{') depth++;
                    else if (configContent[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closeBrace = i;
                            break;
                        }
                    }
                }

                if (closeBrace != -1)
                {
                    string block = configContent.Substring(match.Index, closeBrace - match.Index + 1);
                    if (Regex.IsMatch(block, @"\blisten\s+[^;]*\b443\b"))
                    {
                        sslBlockIndex = match.Index;
                        sslBlockContent = block;
                        sslCloseBrace = closeBrace;
                        break;
                    }
                }
            }

            if (sslBlockContent != null)
            {
                // Já existe um bloco de porta 443
                string updatedBlock = sslBlockContent;

                // Garante diretiva ssl no listen 443
                updatedBlock = Regex.Replace(updatedBlock, @"(\blisten\s+[^;]*\b443\b)([^;]*;)", m =>
                {
                    if (!m.Value.Contains("ssl"))
                    {
                        return $"{m.Groups[1].Value} ssl http2{m.Groups[2].Value}";
                    }
                    return m.Value;
                });

                bool hasCert = Regex.IsMatch(updatedBlock, @"\bssl_certificate\s+[^;]+;");
                bool hasKey = Regex.IsMatch(updatedBlock, @"\bssl_certificate_key\s+[^;]+;");

                if (hasCert && hasKey)
                {
                    updatedBlock = Regex.Replace(updatedBlock, @"(\bssl_certificate\s+)[^;]+;", $"$1{certPath};");
                    updatedBlock = Regex.Replace(updatedBlock, @"(\bssl_certificate_key\s+)[^;]+;", $"$1{keyPath};");
                }
                else if (hasCert && !hasKey)
                {
                    updatedBlock = Regex.Replace(updatedBlock, @"(\bssl_certificate\s+)[^;]+;", $"$1{certPath};\n    ssl_certificate_key {keyPath};");
                }
                else
                {
                    // Injeta ssl_certificate e ssl_certificate_key
                    var sbSsl = new StringBuilder();
                    sbSsl.AppendLine($"    ssl_certificate {certPath};");
                    sbSsl.AppendLine($"    ssl_certificate_key {keyPath};");
                    if (!Regex.IsMatch(updatedBlock, @"\bssl_protocols\b"))
                    {
                        sbSsl.AppendLine("    ssl_protocols TLSv1.2 TLSv1.3;");
                    }
                    if (!Regex.IsMatch(updatedBlock, @"\bssl_ciphers\b"))
                    {
                        sbSsl.AppendLine("    ssl_ciphers HIGH:!aNULL:!MD5;");
                    }

                    var serverNameMatch = Regex.Match(updatedBlock, @"(\bserver_name\s+[^;]+;[^\n]*\r?\n)");
                    if (serverNameMatch.Success)
                    {
                        int insertPos = serverNameMatch.Index + serverNameMatch.Length;
                        updatedBlock = updatedBlock.Insert(insertPos, "\n" + sbSsl);
                    }
                    else
                    {
                        var listenMatch = Regex.Match(updatedBlock, @"(\blisten\s+[^;]*\b443\b[^;]*;[^\n]*\r?\n)");
                        if (listenMatch.Success)
                        {
                            int insertPos = listenMatch.Index + listenMatch.Length;
                            updatedBlock = updatedBlock.Insert(insertPos, "\n" + sbSsl);
                        }
                        else
                        {
                            int open = updatedBlock.IndexOf('{');
                            updatedBlock = updatedBlock.Insert(open + 1, "\n" + sbSsl);
                        }
                    }
                }

                return configContent.Substring(0, sslBlockIndex) + updatedBlock + configContent.Substring(sslCloseBrace + 1);
            }
            else
            {
                // Não existe bloco 443: cria o bloco 443 a partir do bloco 80
                return CreateSslBlockFromHttp(configContent, domain, certPath, keyPath);
            }
        }

        private static string CreateSslBlockFromHttp(string configContent, string domain, string certPath, string keyPath)
        {
            var serverRegex = new Regex(@"\bserver\s*\{");
            var match = serverRegex.Match(configContent);
            if (!match.Success) return configContent;

            int openBrace = configContent.IndexOf('{', match.Index);
            int depth = 1;
            int closeBrace = -1;

            for (int i = openBrace + 1; i < configContent.Length; i++)
            {
                if (configContent[i] == '{') depth++;
                else if (configContent[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBrace = i;
                        break;
                    }
                }
            }

            if (closeBrace == -1) return configContent;

            string port80Block = configContent.Substring(match.Index, closeBrace - match.Index + 1);

            // Extrai as locations do bloco 80 (exceto acme-challenge)
            var locationRegex = new Regex(@"\blocation\s+([^{]+)\{");
            var locMatches = locationRegex.Matches(port80Block);
            var locationsSb = new StringBuilder();

            foreach (Match locMatch in locMatches)
            {
                string locPath = locMatch.Groups[1].Value.Trim();
                if (locPath.Contains("acme-challenge")) continue;

                int locOpen = port80Block.IndexOf('{', locMatch.Index);
                int locDepth = 1;
                int locClose = -1;
                for (int j = locOpen + 1; j < port80Block.Length; j++)
                {
                    if (port80Block[j] == '{') locDepth++;
                    else if (port80Block[j] == '}')
                    {
                        locDepth--;
                        if (locDepth == 0) { locClose = j; break; }
                    }
                }

                if (locClose != -1)
                {
                    string locBlock = port80Block.Substring(locMatch.Index, locClose - locMatch.Index + 1);
                    locationsSb.AppendLine("    " + locBlock.Trim().Replace("\n", "\n    "));
                    locationsSb.AppendLine();
                }
            }

            // Bloco 443
            var sb443 = new StringBuilder();
            sb443.AppendLine();
            sb443.AppendLine("server {");
            sb443.AppendLine("    listen 443 ssl http2;");
            sb443.AppendLine($"    server_name {domain};");
            sb443.AppendLine();
            sb443.AppendLine($"    ssl_certificate {certPath};");
            sb443.AppendLine($"    ssl_certificate_key {keyPath};");
            sb443.AppendLine("    ssl_protocols TLSv1.2 TLSv1.3;");
            sb443.AppendLine("    ssl_ciphers HIGH:!aNULL:!MD5;");
            sb443.AppendLine();
            if (locationsSb.Length > 0)
            {
                sb443.Append(locationsSb);
            }
            else
            {
                sb443.AppendLine("    location / {");
                sb443.AppendLine("        proxy_pass http://127.0.0.1:80;");
                sb443.AppendLine("    }");
            }
            sb443.AppendLine("}");

            // Ajusta o bloco 80 para redirecionar para https mantendo acme-challenge
            string updated80Block = port80Block;

            // Remove locations customizadas da porta 80 e deixa redirect na location /
            if (!updated80Block.Contains(".well-known/acme-challenge"))
            {
                updated80Block = Regex.Replace(updated80Block, @"(\bserver_name\s+[^;]+;[^\n]*\r?\n)",
                    "$1\n    location /.well-known/acme-challenge/ {\n        root /var/www/html;\n    }\n");
            }

            // Atualiza location / para return 301
            var rootLocRegex = new Regex(@"(\blocation\s+(?:=\s+)?/\s*\{)([\s\S]*?)(\})");
            if (rootLocRegex.IsMatch(updated80Block))
            {
                updated80Block = rootLocRegex.Replace(updated80Block,
                    "location / {\n        return 301 https://$host$request_uri;\n    }");
            }

            return configContent.Substring(0, match.Index) + updated80Block + configContent.Substring(closeBrace + 1) + sb443.ToString();
        }
    }
}
