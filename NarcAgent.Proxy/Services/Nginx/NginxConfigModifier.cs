using System;
using System.Collections.Generic;
using System.Linq;
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

            return SanitizeConfig(sb.ToString());
        }

        /// <summary>
        /// Verifica se a configuração possui uma diretiva ativa (não comentada) de escuta na porta 443.
        /// Ignora linhas comentadas com '#' ou qualquer comentário inline.
        /// </summary>
        public static bool HasActivePort443(string configContent)
        {
            if (string.IsNullOrWhiteSpace(configContent)) return false;
            return Regex.IsMatch(configContent, @"^[ \t]*listen\s+[^;#\r\n]*\b443\b", RegexOptions.Multiline);
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

            var serverRegex = new Regex(@"^[ \t]*server\s*\{", RegexOptions.Multiline);
            var matches = serverRegex.Matches(configContent);
            if (matches.Count == 0)
            {
                serverRegex = new Regex(@"\bserver\s*\{");
                matches = serverRegex.Matches(configContent);
                if (matches.Count == 0) return configContent;
            }

            int sslBlockIndex = -1;
            string? sslBlockContent = null;
            int sslCloseBrace = -1;

            // 1. Procura primeiro por um bloco que já tenha listen 443 ATIVO
            for (int mIndex = 0; mIndex < matches.Count; mIndex++)
            {
                var match = matches[mIndex];
                int openBrace = configContent.IndexOf('{', match.Index);
                if (openBrace == -1) continue;
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
                    if (HasActivePort443(block))
                    {
                        sslBlockIndex = match.Index;
                        sslBlockContent = block;
                        sslCloseBrace = closeBrace;
                        break;
                    }
                }
            }

            // 2. Se não encontrou bloco com 443 ativo, verifica se existe um bloco dedicado com 443 comentado
            if (sslBlockContent == null && matches.Count > 1)
            {
                for (int mIndex = 0; mIndex < matches.Count; mIndex++)
                {
                    var match = matches[mIndex];
                    int openBrace = configContent.IndexOf('{', match.Index);
                    if (openBrace == -1) continue;
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
                        bool hasActive80 = Regex.IsMatch(block, @"^[ \t]*listen\s+[^;#\r\n]*\b80\b", RegexOptions.Multiline);
                        bool hasCommented443 = Regex.IsMatch(block, @"^[ \t]*#[ \t]*listen\s+[^;\r\n]*\b443\b", RegexOptions.Multiline);

                        if (!hasActive80 && hasCommented443)
                        {
                            // Descomenta o listen 443
                            block = Regex.Replace(block, @"^[ \t]*#[ \t]*(listen\s+[^;\r\n]*\b443\b[^;]*;)", "    $1", RegexOptions.Multiline);
                            sslBlockIndex = match.Index;
                            sslBlockContent = block;
                            sslCloseBrace = closeBrace;
                            break;
                        }
                    }
                }
            }

            if (sslBlockContent != null)
            {
                // Já existe um bloco de porta 443 ativo (ou descomentado)
                string updatedBlock = sslBlockContent;

                // Garante diretiva ssl no listen 443
                updatedBlock = Regex.Replace(updatedBlock, @"(^[ \t]*listen\s+[^;#\r\n]*\b443\b)([^;\r\n]*;)", m =>
                {
                    if (!m.Value.Contains("ssl"))
                    {
                        return $"{m.Groups[1].Value} ssl http2{m.Groups[2].Value}";
                    }
                    return m.Value;
                }, RegexOptions.Multiline);

                bool hasCert = Regex.IsMatch(updatedBlock, @"^[ \t]*ssl_certificate\s+[^;]+;", RegexOptions.Multiline);
                bool hasKey = Regex.IsMatch(updatedBlock, @"^[ \t]*ssl_certificate_key\s+[^;]+;", RegexOptions.Multiline);

                if (hasCert && hasKey)
                {
                    updatedBlock = Regex.Replace(updatedBlock, @"(^[ \t]*ssl_certificate\s+)[^;]+;", $"$1{certPath};", RegexOptions.Multiline);
                    updatedBlock = Regex.Replace(updatedBlock, @"(^[ \t]*ssl_certificate_key\s+)[^;]+;", $"$1{keyPath};", RegexOptions.Multiline);
                }
                else if (hasCert && !hasKey)
                {
                    updatedBlock = Regex.Replace(updatedBlock, @"(^[ \t]*ssl_certificate\s+)[^;]+;", $"$1{certPath};\n    ssl_certificate_key {keyPath};", RegexOptions.Multiline);
                }
                else
                {
                    // Injeta ssl_certificate e ssl_certificate_key
                    var sbSsl = new StringBuilder();
                    sbSsl.AppendLine($"    ssl_certificate {certPath};");
                    sbSsl.AppendLine($"    ssl_certificate_key {keyPath};");
                    if (!Regex.IsMatch(updatedBlock, @"^[ \t]*ssl_protocols\b", RegexOptions.Multiline))
                    {
                        sbSsl.AppendLine("    ssl_protocols TLSv1.2 TLSv1.3;");
                    }
                    if (!Regex.IsMatch(updatedBlock, @"^[ \t]*ssl_ciphers\b", RegexOptions.Multiline))
                    {
                        sbSsl.AppendLine("    ssl_ciphers HIGH:!aNULL:!MD5;");
                    }

                    var serverNameMatch = Regex.Match(updatedBlock, @"(^[ \t]*server_name\s+[^;]+;[^\n]*\r?\n)", RegexOptions.Multiline);
                    if (serverNameMatch.Success)
                    {
                        int insertPos = serverNameMatch.Index + serverNameMatch.Length;
                        updatedBlock = updatedBlock.Insert(insertPos, "\n" + sbSsl);
                    }
                    else
                    {
                        var listenMatch = Regex.Match(updatedBlock, @"(^[ \t]*listen\s+[^;#\r\n]*\b443\b[^;]*;[^\n]*\r?\n)", RegexOptions.Multiline);
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

                return SanitizeConfig(configContent.Substring(0, sslBlockIndex) + updatedBlock + configContent.Substring(sslCloseBrace + 1));
            }
            else
            {
                // Não existe bloco 443: cria o bloco 443 a partir do bloco 80
                return CreateSslBlockFromHttp(configContent, domain, certPath, keyPath);
            }
        }

        public class NginxLocation
        {
            public string PathKey { get; set; } = string.Empty;
            public string RawDeclaration { get; set; } = string.Empty;
            public string Body { get; set; } = string.Empty;
            public string FullBlock { get; set; } = string.Empty;
            public int StartIndex { get; set; }
            public int Length { get; set; }
        }

        public class NginxServerBlock
        {
            public int StartIndex { get; set; }
            public int Length { get; set; }
            public string Content { get; set; } = string.Empty;
            public bool IsPort443 { get; set; }
            public bool IsPort80 { get; set; }
            public bool IsRedirectBlock { get; set; }
            public bool HasProxyPass { get; set; }
            public bool HasCertificates { get; set; }
        }

        /// <summary>
        /// Extrai todos os blocos de location declarados dentro de um bloco server.
        /// Suporta contagem segura de profundidade de chaves { }.
        /// </summary>
        public static List<NginxLocation> ExtractLocations(string serverBlock)
        {
            var list = new List<NginxLocation>();
            if (string.IsNullOrWhiteSpace(serverBlock)) return list;

            var locRegex = new Regex(@"^[ \t]*location\s+([^{]+)\{", RegexOptions.Multiline);
            var matches = locRegex.Matches(serverBlock);
            if (matches.Count == 0)
            {
                locRegex = new Regex(@"\blocation\s+([^{]+)\{");
                matches = locRegex.Matches(serverBlock);
            }

            int currentPos = 0;
            foreach (Match m in matches)
            {
                if (m.Index < currentPos) continue;
                int openBrace = serverBlock.IndexOf('{', m.Index);
                if (openBrace == -1) continue;

                int depth = 1;
                int closeBrace = -1;
                for (int i = openBrace + 1; i < serverBlock.Length; i++)
                {
                    if (serverBlock[i] == '{') depth++;
                    else if (serverBlock[i] == '}')
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
                    string rawDecl = m.Groups[1].Value.Trim();
                    string pathKey = Regex.Replace(rawDecl, @"\s+", " ").Trim();
                    string body = serverBlock.Substring(openBrace + 1, closeBrace - openBrace - 1);
                    int len = closeBrace - m.Index + 1;
                    string full = serverBlock.Substring(m.Index, len);

                    list.Add(new NginxLocation
                    {
                        PathKey = pathKey,
                        RawDeclaration = rawDecl,
                        Body = body,
                        FullBlock = full,
                        StartIndex = m.Index,
                        Length = len
                    });

                    currentPos = closeBrace + 1;
                }
            }

            return list;
        }

        /// <summary>
        /// Extrai todos os blocos server { ... } do arquivo Nginx com contagem de chaves balanceadas.
        /// </summary>
        public static List<NginxServerBlock> ExtractServerBlocks(string configContent)
        {
            var list = new List<NginxServerBlock>();
            if (string.IsNullOrWhiteSpace(configContent)) return list;

            var serverRegex = new Regex(@"^[ \t]*server\s*\{", RegexOptions.Multiline);
            var matches = serverRegex.Matches(configContent);
            if (matches.Count == 0)
            {
                serverRegex = new Regex(@"\bserver\s*\{");
                matches = serverRegex.Matches(configContent);
            }

            int currentPos = 0;
            foreach (Match m in matches)
            {
                if (m.Index < currentPos) continue;
                int openBrace = configContent.IndexOf('{', m.Index);
                if (openBrace == -1) continue;

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
                    int len = closeBrace - m.Index + 1;
                    string block = configContent.Substring(m.Index, len);
                    bool is443 = HasActivePort443(block);
                    bool is80 = Regex.IsMatch(block, @"^[ \t]*listen\s+[^;#\r\n]*\b80\b", RegexOptions.Multiline);
                    bool isRedirect = Regex.IsMatch(block, @"\breturn\s+(?:301|302)\s+https?://", RegexOptions.IgnoreCase);
                    bool hasProxyPass = block.Contains("proxy_pass ");
                    bool hasCerts = Regex.IsMatch(block, @"^[ \t]*ssl_certificate\s+[^;\s]+;", RegexOptions.Multiline);

                    list.Add(new NginxServerBlock
                    {
                        StartIndex = m.Index,
                        Length = len,
                        Content = block,
                        IsPort443 = is443,
                        IsPort80 = is80,
                        IsRedirectBlock = isRedirect,
                        HasProxyPass = hasProxyPass,
                        HasCertificates = hasCerts
                    });

                    currentPos = closeBrace + 1;
                }
            }

            return list;
        }

        /// <summary>
        /// Sanitiza um bloco server individual:
        /// - No bloco 443 (SSL): descarta redirects para https (evita loop e duplicações) e de-duplica locations por path.
        /// - No bloco 80 de redirect: preserva acme-challenge e exatamente um return 301, removendo proxy_pass e outras locations redundantes.
        /// - No bloco HTTP normal: de-duplica locations mantendo a de maior completude/proxy_pass.
        /// </summary>
        public static string SanitizeServerBlock(string serverBlock)
        {
            if (string.IsNullOrWhiteSpace(serverBlock)) return serverBlock;

            bool is443 = HasActivePort443(serverBlock);
            bool is80 = Regex.IsMatch(serverBlock, @"^[ \t]*listen\s+[^;#\r\n]*\b80\b", RegexOptions.Multiline);
            bool isRedirect = Regex.IsMatch(serverBlock, @"\breturn\s+(?:301|302)\s+https?://", RegexOptions.IgnoreCase);

            var locations = ExtractLocations(serverBlock);
            if (locations.Count == 0) return serverBlock;

            var discardedLocations = new List<NginxLocation>();

            if (is443)
            {
                // Bloco 443 (HTTPS)
                // 1. Descarta qualquer location com redirect para https (loop infinito de redirecionamento)
                var validLocations = new List<NginxLocation>();
                foreach (var loc in locations)
                {
                    if (Regex.IsMatch(loc.Body, @"\breturn\s+(?:301|302)\s+https?://", RegexOptions.IgnoreCase))
                    {
                        discardedLocations.Add(loc);
                    }
                    else
                    {
                        validLocations.Add(loc);
                    }
                }

                // 2. Agrupa por PathKey para de-duplicar (ex: múltiplos "location /")
                var groups = validLocations.GroupBy(l => l.PathKey, StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    if (group.Count() > 1)
                    {
                        var best = group.OrderByDescending(l =>
                        {
                            int score = 0;
                            if (l.Body.Contains("proxy_pass"))
                            {
                                score += 1000;
                                if (!l.Body.Contains("127.0.0.1:80;")) score += 500;
                            }
                            if (l.Body.Contains("proxy_set_header")) score += 200;
                            score += l.Body.Length;
                            return score;
                        }).First();

                        foreach (var loc in group)
                        {
                            if (!ReferenceEquals(loc, best))
                            {
                                discardedLocations.Add(loc);
                            }
                        }
                    }
                }
            }
            else if (is80 && isRedirect)
            {
                // Bloco 80 que redireciona para HTTPS
                // APENAS acme-challenge e exatamente um return 301 devem existir!
                bool keptAcme = false;
                bool keptRedirect = false;

                foreach (var loc in locations)
                {
                    if (loc.PathKey.Contains("acme-challenge", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!keptAcme) keptAcme = true;
                        else discardedLocations.Add(loc);
                    }
                    else if (Regex.IsMatch(loc.Body, @"\breturn\s+(?:301|302)\s+https?://", RegexOptions.IgnoreCase))
                    {
                        if (!keptRedirect) keptRedirect = true;
                        else discardedLocations.Add(loc);
                    }
                    else
                    {
                        // Qualquer outra location (como proxy_pass residual) é descartada do bloco 80
                        discardedLocations.Add(loc);
                    }
                }
            }
            else
            {
                // Bloco genérico / HTTP normal
                var groups = locations.GroupBy(l => l.PathKey, StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    if (group.Count() > 1)
                    {
                        var best = group.OrderByDescending(l =>
                        {
                            int score = 0;
                            if (l.Body.Contains("proxy_pass")) score += 1000;
                            if (l.Body.Contains("proxy_set_header")) score += 200;
                            score += l.Body.Length;
                            return score;
                        }).First();

                        foreach (var loc in group)
                        {
                            if (!ReferenceEquals(loc, best))
                            {
                                discardedLocations.Add(loc);
                            }
                        }
                    }
                }
            }

            if (discardedLocations.Count == 0) return serverBlock;

            // Remove os blocos descartados de trás para frente para manter os índices válidos
            string updated = serverBlock;
            foreach (var loc in discardedLocations.OrderByDescending(l => l.StartIndex))
            {
                updated = updated.Remove(loc.StartIndex, loc.Length);
            }

            // Limpa quebras de linha triplas decorrentes da remoção
            updated = Regex.Replace(updated, @"(\r?\n[ \t]*){3,}", "\n\n");

            return updated;
        }

        /// <summary>
        /// Sanitiza o arquivo Nginx completo:
        /// - De-duplica locations conflitantes em cada bloco server.
        /// - Remove blocos server duplicados que escutam na mesma porta.
        /// - Garante conformidade estrita com o nginx -t.
        /// </summary>
        public static string SanitizeConfig(string configContent)
        {
            if (string.IsNullOrWhiteSpace(configContent)) return configContent;

            var serverBlocks = ExtractServerBlocks(configContent);
            if (serverBlocks.Count == 0) return configContent;

            var blocks443 = serverBlocks.Where(b => b.IsPort443).ToList();
            NginxServerBlock? best443 = null;
            if (blocks443.Count > 1)
            {
                best443 = blocks443.OrderByDescending(b =>
                {
                    int score = 0;
                    if (b.HasCertificates) score += 1000;
                    if (b.HasProxyPass) score += 500;
                    score += b.Content.Length;
                    return score;
                }).First();
            }
            else if (blocks443.Count == 1)
            {
                best443 = blocks443[0];
            }

            var blocks80 = serverBlocks.Where(b => b.IsPort80).ToList();
            NginxServerBlock? best80 = null;
            if (blocks80.Count > 1)
            {
                best80 = blocks80.OrderByDescending(b =>
                {
                    int score = 0;
                    if (b.IsRedirectBlock) score += 1000;
                    if (b.HasProxyPass) score += 500;
                    score += b.Content.Length;
                    return score;
                }).First();
            }
            else if (blocks80.Count == 1)
            {
                best80 = blocks80[0];
            }

            var otherBlocks = serverBlocks.Where(b => !b.IsPort443 && !b.IsPort80).ToList();

            // Se havia blocos 443 ou 80 duplicados em nível de arquivo, reconstrói com os melhores
            if (blocks443.Count > 1 || blocks80.Count > 1)
            {
                var sb = new StringBuilder();
                if (best80 != null)
                {
                    sb.AppendLine(SanitizeServerBlock(best80.Content).Trim());
                    sb.AppendLine();
                }
                if (best443 != null)
                {
                    sb.AppendLine(SanitizeServerBlock(best443.Content).Trim());
                    sb.AppendLine();
                }
                foreach (var other in otherBlocks)
                {
                    sb.AppendLine(SanitizeServerBlock(other.Content).Trim());
                    sb.AppendLine();
                }
                return sb.ToString().Trim() + "\n";
            }
            else
            {
                // Processa cada server block individualmente de trás para frente
                string result = configContent;
                foreach (var b in serverBlocks.OrderByDescending(x => x.StartIndex))
                {
                    string sanitizedBlock = SanitizeServerBlock(b.Content);
                    if (!string.Equals(b.Content, sanitizedBlock, StringComparison.Ordinal))
                    {
                        result = result.Substring(0, b.StartIndex) + sanitizedBlock + result.Substring(b.StartIndex + b.Length);
                    }
                }
                return result;
            }
        }

        private static string CreateSslBlockFromHttp(string configContent, string domain, string certPath, string keyPath)
        {
            var serverRegex = new Regex(@"^[ \t]*server\s*\{", RegexOptions.Multiline);
            var match = serverRegex.Match(configContent);
            if (!match.Success)
            {
                serverRegex = new Regex(@"\bserver\s*\{");
                match = serverRegex.Match(configContent);
                if (!match.Success) return configContent;
            }

            int openBrace = configContent.IndexOf('{', match.Index);
            if (openBrace == -1) return configContent;
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

            // Extrai as locations do bloco 80
            var locations = ExtractLocations(port80Block);
            var unique443Locations = new Dictionary<string, NginxLocation>(StringComparer.OrdinalIgnoreCase);

            foreach (var loc in locations)
            {
                if (loc.PathKey.Contains("acme-challenge", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(loc.Body, @"\breturn\s+(?:301|302)\s+https?://", RegexOptions.IgnoreCase)) continue;

                if (!unique443Locations.TryGetValue(loc.PathKey, out var existing))
                {
                    unique443Locations[loc.PathKey] = loc;
                }
                else
                {
                    if (!existing.Body.Contains("proxy_pass") && loc.Body.Contains("proxy_pass"))
                    {
                        unique443Locations[loc.PathKey] = loc;
                    }
                    else if (loc.Body.Length > existing.Body.Length)
                    {
                        unique443Locations[loc.PathKey] = loc;
                    }
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

            if (unique443Locations.Count > 0)
            {
                foreach (var loc in unique443Locations.Values)
                {
                    sb443.AppendLine("    " + loc.FullBlock.Trim().Replace("\n", "\n    "));
                    sb443.AppendLine();
                }

                if (!unique443Locations.ContainsKey("/") && !unique443Locations.ContainsKey("= /"))
                {
                    sb443.AppendLine("    location / {");
                    sb443.AppendLine("        proxy_pass http://127.0.0.1:80;");
                    sb443.AppendLine("        proxy_set_header Host $host;");
                    sb443.AppendLine("        proxy_set_header X-Real-IP $remote_addr;");
                    sb443.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
                    sb443.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");
                    sb443.AppendLine("        proxy_http_version 1.1;");
                    sb443.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
                    sb443.AppendLine("        proxy_set_header Connection \"upgrade\";");
                    sb443.AppendLine("    }");
                }
            }
            else
            {
                sb443.AppendLine("    location / {");
                sb443.AppendLine("        proxy_pass http://127.0.0.1:80;");
                sb443.AppendLine("        proxy_set_header Host $host;");
                sb443.AppendLine("        proxy_set_header X-Real-IP $remote_addr;");
                sb443.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
                sb443.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");
                sb443.AppendLine("        proxy_http_version 1.1;");
                sb443.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
                sb443.AppendLine("        proxy_set_header Connection \"upgrade\";");
                sb443.AppendLine("    }");
            }
            sb443.AppendLine("}");

            // Ajusta o bloco 80 para redirecionar para https mantendo acme-challenge e return 301
            string updated80Block = port80Block;

            // Limpa diretivas SSL ou listen 443 comentados que possam existir no bloco 80
            updated80Block = Regex.Replace(updated80Block, @"^[ \t]*#[ \t]*listen\s+[^;\r\n]*\b443\b[^;\r\n]*;?[^\n]*\r?\n", "", RegexOptions.Multiline);
            updated80Block = Regex.Replace(updated80Block, @"^[ \t]*ssl_certificate\s+[^;]+;[^\n]*\r?\n", "", RegexOptions.Multiline);
            updated80Block = Regex.Replace(updated80Block, @"^[ \t]*ssl_certificate_key\s+[^;]+;[^\n]*\r?\n", "", RegexOptions.Multiline);
            updated80Block = Regex.Replace(updated80Block, @"^[ \t]*ssl_protocols\s+[^;]+;[^\n]*\r?\n", "", RegexOptions.Multiline);
            updated80Block = Regex.Replace(updated80Block, @"^[ \t]*ssl_ciphers\s+[^;]+;[^\n]*\r?\n", "", RegexOptions.Multiline);

            // No bloco 80 de redirect, removemos TODAS as locations existentes para evitar duplicações
            var locs80 = ExtractLocations(updated80Block);
            for (int i = locs80.Count - 1; i >= 0; i--)
            {
                updated80Block = updated80Block.Remove(locs80[i].StartIndex, locs80[i].Length);
            }

            // Injeta acme-challenge e return 301 limpos
            int close80 = updated80Block.LastIndexOf('}');
            if (close80 != -1)
            {
                var sb80 = new StringBuilder();
                sb80.AppendLine();
                sb80.AppendLine("    location /.well-known/acme-challenge/ {");
                sb80.AppendLine("        root /var/www/html;");
                sb80.AppendLine("    }");
                sb80.AppendLine();
                sb80.AppendLine("    location / {");
                sb80.AppendLine("        return 301 https://$host$request_uri;");
                sb80.AppendLine("    }");
                sb80.AppendLine();
                updated80Block = updated80Block.Insert(close80, sb80.ToString());
            }

            string combined = configContent.Substring(0, match.Index) + updated80Block.Trim() + "\n\n" + sb443.ToString().Trim() + "\n";
            return SanitizeConfig(combined);
        }
    }
}
