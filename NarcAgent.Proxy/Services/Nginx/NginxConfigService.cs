using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NarcAgent.Proxy.Services.Nginx
{
    public class NginxConfigService
    {
        private readonly string _configPath;
        private const string MaintenanceFilePath = "/var/www/html/narc_deactivated.html";

        public NginxConfigService(string? configPath = null)
        {
            var envPath = Environment.GetEnvironmentVariable("NGINX_CONFIG_PATH")
                          ?? Environment.GetEnvironmentVariable("NGINX_CONF_DIR");
            
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                _configPath = envPath.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(configPath))
            {
                _configPath = configPath;
            }
            else
            {
                _configPath = ResolveDefaultConfigPath();
            }
        }

        private static string ResolveDefaultConfigPath()
        {
            if (OperatingSystem.IsWindows())
            {
                return "/etc/nginx/conf.d";
            }

            try
            {
                if (File.Exists("/etc/nginx/nginx.conf"))
                {
                    var nginxConf = File.ReadAllText("/etc/nginx/nginx.conf");
                    bool includesConfD = nginxConf.Contains("conf.d");
                    bool includesSitesEnabled = nginxConf.Contains("sites-enabled");

                    if (includesSitesEnabled && !includesConfD && Directory.Exists("/etc/nginx/sites-enabled"))
                    {
                        Console.WriteLine("[NginxConfigService] Detectado que o nginx.conf inclui apenas 'sites-enabled'. Usando /etc/nginx/sites-enabled como pasta de proxies.");
                        return "/etc/nginx/sites-enabled";
                    }
                }

                if (Directory.Exists("/etc/nginx/sites-enabled") && !Directory.Exists("/etc/nginx/conf.d"))
                {
                    return "/etc/nginx/sites-enabled";
                }
            }
            catch { }

            return "/etc/nginx/conf.d";
        }

        public void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_configPath))
            {
                Directory.CreateDirectory(_configPath);
            }
        }

        /// <summary>
        /// Retorna o caminho absoluto do diretório de configurações Nginx usado por este serviço.
        /// </summary>
        public string ConfigDirectory => _configPath;

        /// <summary>
        /// Cria o HTML estatico "Proxy desativado" usado pelas configs port 80/443
        /// quando o proxy esta desligado. Idempotente: so escreve se nao existir ou se o conteudo mudou.
        /// </summary>
        public void EnsureMaintenanceFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(MaintenanceFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(MaintenanceFilePath) || File.ReadAllText(MaintenanceFilePath) != MaintenanceHtml)
                {
                    File.WriteAllText(MaintenanceFilePath, MaintenanceHtml);
                    TryChmod(MaintenanceFilePath, "644");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NginxConfigService] Erro ao criar maintenance file: {ex.Message}");
            }
        }

        // IMPORTANTE: em C# verbatim string (@"..."), aspas duplas sao escapadas como "".
        private const string MaintenanceHtml = @"<!doctype html>
<html lang=""pt-BR"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1, viewport-fit=cover"">
<meta name=""robots"" content=""noindex, nofollow"">
<meta name=""narc-status"" content=""deactivated"">
<title>Erro 503 | Acesso bloqueado | Narc Agent</title>
<link rel=""preconnect"" href=""https://fonts.googleapis.com"">
<link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
<link href=""https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap"" rel=""stylesheet"">
<style>
:root{
  --bg:#0a0a0b; --sidebar:#0f0f11; --card:#141416; --surface:#1c1c1f; --border:#27272a; --muted:#71717a;
  --primary:#7c3aed; --primary-h:#6d28d9;
  --green:#10b981; --green-dim:rgba(16,185,129,.1);
  --danger:#ef4444; --danger-dim:rgba(239,68,68,.1);
  --shadow-modal:0 25px 50px -12px rgba(0,0,0,.7),0 0 0 1px rgba(255,255,255,.05);
  --text:#f4f4f5; --soft:#a1a1aa;
  --sans:'Inter',system-ui,-apple-system,sans-serif;
  --mono:'JetBrains Mono',Menlo,monospace;
  --spring:cubic-bezier(.16,1,.3,1);
}
*{box-sizing:border-box}
html{-webkit-text-size-adjust:100%}
html,body{min-height:100%}
body{margin:0;background:var(--bg);color:var(--text);font:400 15px/1.7 var(--sans);-webkit-font-smoothing:antialiased;overflow-x:hidden}

@keyframes up{from{opacity:0;transform:translateY(14px)}to{opacity:1;transform:none}}
@keyframes fade{from{opacity:0}to{opacity:1}}
@keyframes draw{from{transform:scaleX(0)}to{transform:scaleX(1)}}
@keyframes flowX{to{background-position:12px 0}}
@keyframes flowY{to{background-position:0 12px}}
@keyframes okpulse{0%,100%{opacity:1}50%{opacity:.35}}
@keyframes blink{50%{opacity:0}}
@keyframes meter{from{transform:scaleX(0)}to{transform:scaleX(1)}}

/* ===== Ciclo de 7 s: GET vai do navegador até a Rede Narc, é barrado, e o 503 volta ===== */


.rv{opacity:0;animation:up .6s var(--spring) forwards;animation-delay:var(--d,0s)}
.site{min-height:100vh;display:flex;flex-direction:column}
main{flex:1}
.wrap{max-width:1120px;margin:0 auto;padding:0 max(32px,env(safe-area-inset-left))}

h1{margin:0;font-size:clamp(2rem,1.35rem + 2.6vw,3rem);font-weight:600;letter-spacing:-.035em;line-height:1.1;max-width:720px;text-wrap:balance}
h2{margin:0 0 14px;font-size:20px;font-weight:600;letter-spacing:-.015em;line-height:1.3}
h3{margin:0 0 4px;font-size:14px;font-weight:600;line-height:1.5}
p{margin:0 0 12px;font-size:15px;line-height:1.7;color:var(--soft);max-width:62ch}
.s{color:var(--text);font-weight:500}

.hero{padding:88px 0 40px}
.code{display:inline-flex;align-items:center;gap:8px;margin-bottom:20px;padding:4px 12px;border-radius:999px;background:var(--danger-dim);border:1px solid rgba(239,68,68,.25);color:var(--danger);font-size:13px;font-weight:600;line-height:1.5}
.code i{width:6px;height:6px;border-radius:50%;background:currentColor}
.sub{margin:20px 0 0;max-width:60ch;font-size:clamp(16px,15px + .35vw,18px);line-height:1.65;color:var(--soft)}

.console{border:1px solid var(--border);border-radius:1rem;background:var(--card);box-shadow:var(--shadow-modal);overflow:hidden}
.bar{display:flex;align-items:center;gap:14px;padding:12px 20px;border-bottom:1px solid var(--border);background:var(--surface)}
.dots{display:flex;gap:6px}
.dots b{width:10px;height:10px;border-radius:50%;background:var(--border)}
.bar .path{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:13px;line-height:1.5;color:var(--muted)}
.bar .path span{font-family:var(--mono);font-size:12.5px;color:var(--soft)}
.tag{flex:none;padding:2px 10px;border-radius:999px;background:var(--danger-dim);border:1px solid rgba(239,68,68,.25);color:var(--danger);font-size:12.5px;font-weight:500;line-height:1.6}

.stats{display:grid;grid-template-columns:repeat(4,1fr);border-bottom:1px solid var(--border)}
.stat{padding:22px 24px;border-right:1px solid var(--border);transition:background .2s}
.stat:hover{background:var(--surface)}
.stat:last-child{border-right:0}
.stat small{display:block;font-size:13px;font-weight:500;line-height:1.4;color:var(--muted);margin-bottom:8px}
.stat strong{display:block;font-size:28px;font-weight:600;letter-spacing:-.025em;line-height:1.15;font-variant-numeric:tabular-nums}
.stat strong.ok{color:var(--green)} .stat strong.bad{color:var(--danger)}
.stat em{display:block;font-style:normal;font-size:13px;line-height:1.4;color:var(--muted);margin-top:6px}

.body{display:grid;grid-template-columns:1.5fr 1fr}
.col{padding:28px 24px;min-width:0}
.col + .col{border-left:1px solid var(--border)}
.col h4{margin:0;font-size:14px;font-weight:600;line-height:1.4}
.hd{display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px 16px;margin-bottom:22px}
.lg{display:flex;gap:14px;font-size:12.5px;line-height:1.4;color:var(--muted)}
.lg span{display:inline-flex;align-items:center;gap:6px}
.lg span::before{content:"""";width:6px;height:6px;border-radius:50%;background:var(--green)}
.lg span.r::before{background:var(--danger)}

/* Caminho da requisição */
.flow{position:relative;display:grid;grid-template-columns:repeat(3,1fr);text-align:center;padding-bottom:6px}
.seg{position:absolute;top:34px;height:2px}
.seg::before{content:"""";position:absolute;inset:0;background:repeating-linear-gradient(90deg,var(--c) 0 6px,transparent 6px 12px);background-size:12px 2px;transform-origin:left;animation:draw .8s var(--spring) 1s backwards,flowX .9s linear infinite}
.seg.a{--c:rgba(16,185,129,.55);left:calc(16.666% + 40px);width:calc(33.333% - 80px)}
.seg.b{--c:rgba(239,68,68,.55);left:calc(50% + 40px);width:calc(33.333% - 80px)}
.seg.b::before{animation:draw .8s var(--spring) 1s backwards,flowX 1.5s linear infinite}

.ring{position:absolute;inset:6px;border-radius:50%;border:2px solid var(--danger);opacity:0;pointer-events:none}
.pk{position:absolute;left:0;top:50%;transform:translate(-50%,-50%);padding:4px 9px;border-radius:999px;background:var(--card);border:1px solid;font:600 11px/1 var(--mono);white-space:nowrap;opacity:0;z-index:2}
.pk.g{color:var(--green);border-color:rgba(16,185,129,.6);box-shadow:0 0 12px rgba(16,185,129,.45)}
.pk.r{color:var(--danger);border-color:rgba(239,68,68,.65);box-shadow:0 0 12px rgba(239,68,68,.5)}

.node{position:relative;z-index:1}
.ico{position:relative;width:68px;height:68px;margin:0 auto 12px;display:grid;place-items:center}
.ico svg{width:100%;height:100%}
.node small{display:block;font-size:13px;line-height:1.4;color:var(--muted);word-break:break-all}
.node small.mono{font-family:var(--mono);font-size:12px}
.node b{display:block;margin-top:2px;font-size:16px;font-weight:600;line-height:1.4}
.st{display:inline-flex;align-items:center;gap:7px;margin-top:8px;font-size:13px;font-weight:500;line-height:1.4}
.st::before{content:"""";width:7px;height:7px;border-radius:50%;background:currentColor}
.st.ok{color:var(--green)} .st.ok::before{animation:okpulse 2.4s ease-in-out infinite}
.st.off{color:var(--danger)}

.log{margin-top:28px;border:1px solid var(--border);border-radius:10px;background:var(--bg);padding:14px 16px;font:400 12.5px/1.9 var(--mono);color:var(--soft);min-height:150px;overflow-x:auto;white-space:nowrap}
.log .t{color:var(--muted)}
.log .i{color:var(--green)} .log .w{color:var(--text)} .log .e{color:var(--danger)}
.log .ln{animation:up .35s var(--spring) both}
.log .cur{display:inline-block;width:7px;height:14px;background:var(--soft);vertical-align:-2px;margin-left:4px;animation:blink 1s steps(1) infinite}

.kv{margin:0;padding:0;list-style:none}
.kv li{display:flex;justify-content:space-between;align-items:baseline;gap:16px;padding:12px 0;border-bottom:1px solid var(--border);font-size:14px;line-height:1.5}
.kv li:last-child{border-bottom:0}
.kv span{color:var(--muted)}
.kv b{font-weight:500;color:var(--text);text-align:right;word-break:break-all}
.kv .m{font-family:var(--mono);font-size:12.5px}
.kv b.bad{color:var(--danger)} .kv b.ok{color:var(--green)}
.cp{all:unset;box-sizing:border-box;cursor:pointer;font:500 12.5px/1.5 var(--mono);color:var(--text);text-align:right;word-break:break-all;border-radius:6px;padding:0 4px;margin:0 -4px;transition:background .15s,color .15s}
.cp:hover{background:var(--surface);color:#fff}
.cp:focus-visible{outline:2px solid var(--primary);outline-offset:2px}
.cp.ok{color:var(--green)}

.info{display:grid;grid-template-columns:1fr 1fr;gap:56px;padding:64px 0 56px}
h3.sp{margin-top:28px}
.btn{display:inline-flex;align-items:center;justify-content:center;min-height:44px;margin-top:6px;padding:10px 20px;border-radius:8px;border:1px solid var(--primary);background:var(--primary);color:#fff;font:500 14px/1.4 var(--sans);cursor:pointer;transition:background .15s,transform .15s}
.btn:hover{background:var(--primary-h);transform:translateY(-1px)}
.btn:active{transform:none}
.btn:focus-visible{outline:2px solid var(--primary);outline-offset:2px}
.auto{margin-top:14px;max-width:340px;font-size:13px;line-height:1.5;color:var(--muted)}
.meter{height:2px;margin-top:8px;border-radius:2px;background:var(--border);overflow:hidden}
.meter i{display:block;height:100%;background:var(--primary);transform-origin:left;transform:scaleX(0)}
.meter i.run{animation:meter 30s linear forwards}

.foot{border:0;background:transparent;padding:22px 0 max(30px,env(safe-area-inset-bottom));text-align:center;font-size:13px;line-height:1.5;color:var(--muted)}
.foot b{color:var(--primary);font-weight:600}

.stroke{stroke:var(--muted)} .fo{fill:var(--green-dim)} .fe{fill:var(--danger-dim)} .om{stroke:var(--green)} .em{stroke:var(--danger)}

@media(max-width:900px){
  .wrap{padding:0 max(18px,env(safe-area-inset-left))}
  .hero{padding:56px 0 32px}
  .stats{grid-template-columns:1fr 1fr}
  .stat:nth-child(2){border-right:0}
  .stat:nth-child(-n+2){border-bottom:1px solid var(--border)}
  .body{grid-template-columns:1fr}
  .col + .col{border-left:0;border-top:1px solid var(--border)}
  .info{grid-template-columns:1fr;gap:36px;padding:44px 0}
}

/* Celular: o caminho vira uma lista vertical e os pacotes descem/sobem */
@media(max-width:640px){
  body{font-size:14.5px}
  .hero{padding:40px 0 28px}
  .code{margin-bottom:16px}
  .dots{display:none}
  .bar{padding:12px 16px}
  .stat{padding:16px}
  .stat strong{font-size:22px}
  .stat em{font-size:12px}
  .col{padding:22px 16px}
  .flow{grid-template-columns:1fr;text-align:left;padding-bottom:0}
  .node{display:grid;grid-template-columns:56px 1fr;column-gap:16px;align-content:start;min-height:120px}
  .node:last-child{min-height:0}
  .node > :not(.ico){grid-column:2}
  .ico{grid-row:1/5;width:56px;height:56px;margin:0;align-self:start}
  .seg{left:27px;top:56px;width:2px;height:64px}
  .seg.b{left:27px;top:176px;width:2px;height:64px}
  .seg::before{background:repeating-linear-gradient(180deg,var(--c) 0 6px,transparent 6px 12px);background-size:2px 12px;animation:fade .6s ease 1s backwards,flowY .9s linear infinite}
  .seg.b::before{animation:fade .6s ease 1s backwards,flowY 1.5s linear infinite}
  .pk{left:50%;top:0}
  .log{margin-top:22px;font-size:12px;padding:12px}
  .kv li{padding:11px 0}
  .info{padding:36px 0 40px;gap:32px}
  .btn{width:100%}
  .auto{max-width:none}
}
@media(max-width:360px){
  .stats{grid-template-columns:1fr}
  .stat{border-right:0!important;border-bottom:1px solid var(--border)}
  .stat:last-child{border-bottom:0}
}

@media(prefers-reduced-motion:reduce){.rv{animation:none;opacity:1}}
</style>
</head>
<body>
<div class=""site"">
<main>
  <section class=""wrap hero"">
    <div class=""code rv""><i></i>Erro 503</div>
    <h1 class=""rv"" style=""--d:.08s"">O acesso a este domínio foi bloqueado</h1>
    <p class=""sub rv"" style=""--d:.16s"">O administrador desativou este domínio na Rede Narc. Você ainda consegue acessar o domínio, mas a Rede Narc está bloqueando o acesso ao servidor.</p>
  </section>

  <section class=""wrap"">
    <div class=""console rv"" style=""--d:.26s"">
      <div class=""bar"">
        <div class=""dots""><b></b><b></b><b></b></div>
        <div class=""path"">Narc Agent / Diagnóstico / <span id=""h1"">domain.com</span></div>
        <span class=""tag"">Bloqueado pela Rede Narc</span>
      </div>

      <div class=""stats"">
        <div class=""stat rv"" style=""--d:.4s""><small>Status do domínio</small><strong class=""ok"">Acessível</strong><em>O domínio ainda responde</em></div>
        <div class=""stat rv"" style=""--d:.47s""><small>Código HTTP</small><strong id=""code"">503</strong><em>Serviço indisponível</em></div>
        <div class=""stat rv"" style=""--d:.54s""><small>Rede Narc</small><strong class=""bad"">Bloqueando</strong><em>Acesso ao servidor barrado</em></div>
        <div class=""stat rv"" style=""--d:.61s""><small>Reativação</small><strong>--</strong><em>Definida pelo administrador</em></div>
      </div>

      <div class=""body"">
        <div class=""col"">
          <div class=""hd"">
            <h4>Caminho da requisição</h4>
            <div class=""lg"" aria-hidden=""true""><span>Requisição</span><span class=""r"">Resposta 503</span></div>
          </div>
          <div class=""flow"">
            <div class=""seg a"" aria-hidden=""true""><span class=""pk g"">GET</span><span class=""pk r"">503</span></div>
            <div class=""seg b"" aria-hidden=""true""><span class=""pk g"">GET</span><span class=""pk r"">503</span></div>
            <div class=""node n1 rv"" style=""--d:.7s"">
              <div class=""ico""><svg viewBox=""0 0 70 70"" fill=""none""><rect x=""6"" y=""12"" width=""46"" height=""32"" rx=""4"" class=""stroke"" stroke-width=""2""/><line x1=""6"" y1=""20"" x2=""52"" y2=""20"" class=""stroke"" stroke-width=""2"" opacity="".5""/><circle cx=""52"" cy=""46"" r=""12"" class=""fo"" stroke=""rgba(16,185,129,.3)"" stroke-width=""1.5""/><path d=""M46 46l4 4 8-10"" class=""om"" stroke-width=""3"" stroke-linecap=""round"" stroke-linejoin=""round""/></svg></div>
              <small>Você</small><b>Navegador</b><span class=""st ok"">Funcionando</span>
            </div>
            <div class=""node n2 rv"" style=""--d:.82s"">
              <div class=""ico""><svg viewBox=""0 0 70 70"" fill=""none""><rect x=""10"" y=""10"" width=""38"" height=""34"" rx=""4"" class=""stroke"" stroke-width=""2""/><line x1=""10"" y1=""18"" x2=""48"" y2=""18"" class=""stroke"" stroke-width=""2"" opacity="".5""/><circle cx=""50"" cy=""46"" r=""12"" class=""fo"" stroke=""rgba(16,185,129,.3)"" stroke-width=""1.5""/><path d=""M44 46l4 4 8-10"" class=""om"" stroke-width=""3"" stroke-linecap=""round"" stroke-linejoin=""round""/></svg></div>
              <small class=""mono"" id=""h2"">domain.com</small><b>Domínio</b><span class=""st ok"">Acessível</span>
            </div>
            <div class=""node n3 rv"" style=""--d:.94s"">
              <div class=""ico""><span class=""ring""></span><svg viewBox=""0 0 70 70"" fill=""none""><path d=""M16 46h28a10 10 0 0 0 0-20 14 14 0 0 0-27-2A9 9 0 0 0 16 46Z"" class=""stroke"" stroke-width=""2""/><circle cx=""48"" cy=""46"" r=""12"" class=""fe"" stroke=""rgba(239,68,68,.3)"" stroke-width=""1.5""/><path d=""M42 40l12 12M54 40L42 52"" class=""em"" stroke-width=""3"" stroke-linecap=""round""/></svg></div>
              <small>Conexão</small><b>Rede Narc</b><span class=""st off"">Bloqueando</span>
            </div>
          </div>
          <div class=""log rv"" style=""--d:1s"" id=""log"" aria-label=""Registro da requisição""></div>
        </div>

        <div class=""col"">
          <h4 style=""margin-bottom:20px"">Detalhes</h4>
          <ul class=""kv"">
            <li><span>Domínio</span><b class=""m"" id=""h3"">domain.com</b></li>
            <li><span>Status do domínio</span><b class=""ok"">Acessível</b></li>
            <li><span>Código</span><b>503</b></li>
            <li><span>Rede Narc</span><b class=""bad"">Bloqueando</b></li>
            <li><span>Ray ID</span><button class=""cp"" id=""ray"" type=""button"" title=""Copiar Ray ID"" aria-label=""Copiar Ray ID"">—</button></li>
            <li><span>Horário (UTC)</span><b class=""m"" id=""ts"">—</b></li>
          </ul>
        </div>
      </div>
    </div>
  </section>

  <section class=""wrap info rv"" style=""--d:1.1s"">
    <div>
      <h2>O que aconteceu?</h2>
      <p>O administrador desativou este <span class=""s"">domínio</span> na Rede Narc. Isso pode acontecer por manutenção, troca de IP ou porta, atualização de regras ou desligamento temporário.</p>
      <p>O domínio continua acessível e o servidor não está necessariamente fora do ar. É a Rede Narc que está barrando o acesso ao servidor enquanto o domínio estiver desativado.</p>
    </div>
    <div>
      <h2>O que posso fazer?</h2>
      <h3>Se você é visitante</h3>
      <p>Aguarde alguns minutos e tente de novo. O domínio pode ser reativado a qualquer momento.</p>
      <button class=""btn"" type=""button"" onclick=""location.reload()"">Tentar novamente</button>
      <div class=""auto"" aria-live=""polite""><span id=""cd"">Verificando de novo em 30 s</span><div class=""meter""><i id=""mt""></i></div></div>
      <h3 class=""sp"">Se você é o administrador</h3>
      <p>Reative o domínio no painel do Narc Agent. Quando ele estiver ativo, a Rede Narc libera o acesso ao servidor e tudo volta ao normal.</p>
    </div>
  </section>
</main>
<footer class=""foot"">Powered by <b>ByCronoz</b></footer>
</div>

<script>
(function(){
  const $ = id => document.getElementById(id);
  const reduce = matchMedia(""(prefers-reduced-motion: reduce)"").matches;
  const host = location.host || ""domain.com"";
  [""h1"",""h2"",""h3""].forEach(id => $(id).textContent = host);

  const pad = (n,l=2) => String(n).padStart(l,""0"");
  const now = new Date();
  $(""ts"").textContent = `${now.getUTCFullYear()}-${pad(now.getUTCMonth()+1)}-${pad(now.getUTCDate())} ${pad(now.getUTCHours())}:${pad(now.getUTCMinutes())}:${pad(now.getUTCSeconds())}`;
  let ray = """"; for(let i=0;i<16;i++) ray += Math.floor(Math.random()*16).toString(16);
  $(""ray"").textContent = ray;

  $(""ray"").addEventListener(""click"", async () => {
    const el = $(""ray"");
    try { await navigator.clipboard.writeText(ray); } catch(e){
      const r = document.createRange(); r.selectNodeContents(el);
      const s = getSelection(); s.removeAllRanges(); s.addRange(r); document.execCommand(""copy""); s.removeAllRanges();
    }
    el.textContent = ""Copiado""; el.classList.add(""ok"");
    setTimeout(() => { el.textContent = ray; el.classList.remove(""ok""); }, 1400);
  });

  if(!reduce){
    const el = $(""code""), t0 = performance.now() + 450, dur = 900;
    el.textContent = ""0"";
    (function tick(t){
      const p = Math.min(Math.max((t - t0)/dur, 0), 1);
      el.textContent = Math.round(503 * (1 - Math.pow(1-p, 3)));
      if(p < 1) requestAnimationFrame(tick);
    })(performance.now());
  }

  // Registro sincronizado com o ciclo de 7 s da animação do caminho
  const log = $(""log"");
  const stamp = d => `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}.${pad(d.getUTCMilliseconds(),3)}`;
  const esc = s => s.replace(/&/g,""&amp;"").replace(/</g,""&lt;"");
  const steps = [
    [0,""i"",""INFO "",`requisição recebida  GET ${host}/`],
    [1700,""i"",""INFO "",""domínio acessível, encaminhando para a Rede Narc""],
    [3200,""w"",""WARN "",""narc: domínio desativado pelo administrador""],
    [3450,""e"",""ERROR"",""Rede Narc bloqueou o acesso ao servidor""],
    [4100,""e"",""ERROR"",""resposta 503 Service Unavailable""]
  ];
  function add(i, last){
    log.querySelector("".cur"")?.remove();
    const [,c,lv,msg] = steps[i];
    log.insertAdjacentHTML(""beforeend"",
      `<div class=""ln""><span class=""t"">${stamp(new Date())}</span> <span class=""${c}"">${lv}</span> ${esc(msg)}${last?'<span class=""cur""></span>':''}</div>`);
  }
  // Animação do caminho (Web Animations API), ciclo de 7 s
  const CYCLE = 7000;
  const mq = matchMedia(""(max-width: 640px)"");
  const pk = (seg, c) => document.querySelector(`.seg.${seg} .pk.${c}`);
  const svg = n => document.querySelector(`.${n} .ico svg`);
  const ring = document.querySelector("".ring"");
  let timers = [];
  const at = (ms, fn) => timers.push(setTimeout(fn, ms));

  function send(el, from, to, dur){
    const p = mq.matches ? ""top"" : ""left"";
    el.animate([
      {[p]: from, opacity: 0},
      {[p]: from, opacity: 1, offset: .12},
      {[p]: to,   opacity: 1, offset: .88},
      {[p]: to,   opacity: 0}
    ], {duration: dur, easing: ""ease-in-out""});
  }
  function glow(el, rgb, dur){
    el.animate([
      {filter: ""none""},
      {filter: `drop-shadow(0 0 10px rgba(${rgb},.9))`, offset: .3},
      {filter: ""none""}
    ], {duration: dur});
  }
  function block(){
    const f = ""drop-shadow(0 0 14px rgba(239,68,68,.95))"";
    svg(""n3"").animate([
      {transform: ""translateX(0)"", filter: f},
      {transform: ""translateX(-4px)"", filter: f},
      {transform: ""translateX(4px)"", filter: f},
      {transform: ""translateX(-3px)"", filter: f},
      {transform: ""translateX(2px)"", filter: f},
      {transform: ""none"", filter: ""none""}
    ], {duration: 700});
    ring.animate([{transform: ""scale(.6)"", opacity: .8}, {transform: ""scale(2.2)"", opacity: 0}], {duration: 900, easing: ""ease-out""});
  }

  function cycle(){
    timers.forEach(clearTimeout); timers = [];
    log.innerHTML = """";
    steps.forEach((s,i) => at(s[0], () => add(i, i === steps.length-1)));
    at(300,  () => { send(pk(""a"",""g""), ""0%"", ""100%"", 1300); glow(svg(""n1""), ""16,185,129"", 900); });
    at(1600, () => glow(svg(""n2""), ""16,185,129"", 800));
    at(1900, () => send(pk(""b"",""g""), ""0%"", ""100%"", 1300));
    at(3200, block);
    at(4100, () => send(pk(""b"",""r""), ""100%"", ""0%"", 1100));
    at(5200, () => glow(svg(""n2""), ""239,68,68"", 800));
    at(5300, () => send(pk(""a"",""r""), ""100%"", ""0%"", 1100));
    at(6400, () => glow(svg(""n1""), ""239,68,68"", 800));
  }
  setTimeout(() => { cycle(); setInterval(cycle, CYCLE); }, reduce ? 300 : 1800);

  // Nova verificação automática a cada 30 s
  const LIMIT = 30; let left = LIMIT, busy = false;
  const mt = $(""mt""), cd = $(""cd"");
  function restart(){
    left = LIMIT; cd.textContent = `Verificando de novo em ${left} s`;
    mt.classList.remove(""run""); void mt.offsetWidth; if(!reduce) mt.classList.add(""run"");
  }
  async function check(){
    if(busy) return; busy = true;
    cd.textContent = ""Verificando…"";
    try{
      const r = await fetch(location.href, {cache:""no-store""});
      const html = await r.text();
      if(!html.includes('name=""narc-status""')) return location.reload();
    }catch(e){}
    busy = false; restart();
  }
  restart();
  setInterval(() => {
    if(document.hidden) return;
    left--;
    if(left <= 0) check(); else cd.textContent = `Verificando de novo em ${left} s`;
  }, 1000);
})();
</script>
</body>
</html>";

        private static void TryChmod(string path, string permissions)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"{permissions} {path}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                p?.WaitForExit();
            }
            catch { }
        }

        public async Task SaveConfigAsync(string filename, string content)
        {
            var path = Path.Combine(_configPath, filename);
            await File.WriteAllTextAsync(path, content);
            TryChmod(path, "644");
        }

        public async Task<string> GetConfigAsync(string filename)
        {
            var path = Path.Combine(_configPath, filename);
            return await File.ReadAllTextAsync(path);
        }

        public Task DeleteConfigAsync(string filename)
        {
            var path = Path.Combine(_configPath, filename);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return Task.CompletedTask;
        }

        public async Task BackupAsync(string filename)
        {
            var path = Path.Combine(_configPath, filename);
            var backupPath = path + ".bak";
            if (File.Exists(path))
            {
                await Task.Run(() => File.Copy(path, backupPath, overwrite: true));
            }
        }

        public async Task RestoreAsync(string filename)
        {
            var path = Path.Combine(_configPath, filename);
            var backupPath = path + ".bak";
            if (File.Exists(backupPath))
            {
                await Task.Run(() => File.Copy(backupPath, path, overwrite: true));
            }
        }

        public Task DeleteBackupAsync(string filename)
        {
            var backupPath = Path.Combine(_configPath, filename + ".bak");
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            return Task.CompletedTask;
        }

        public bool ConfigExists(string filename)
        {
            return File.Exists(Path.Combine(_configPath, filename));
        }

        public bool BackupExists(string filename)
        {
            return File.Exists(Path.Combine(_configPath, filename + ".bak"));
        }

        /// <summary>
        /// Busca o filename de um proxy a partir do dominio (server_name), escaneando
        /// os configs existentes. Usado quando a API manda get_config apenas com "domain"
        /// <summary>
        /// Busca o filename de um proxy a partir do dominio (server_name), escaneando
        /// os configs existentes (e backups .bak se desativados). Usado quando a API manda get_config apenas com "domain"
        /// (fallback quando o filename pedido nao segue o padrao narc_proxy_{id}.conf).
        /// </summary>
        public async Task<string?> FindFilenameByDomainAsync(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain) || !Directory.Exists(_configPath))
                return null;

            // 1. Procura em narc_proxy_*.conf por server_name
            foreach (var file in Directory.GetFiles(_configPath, "narc_proxy_*.conf"))
            {
                if (await MatchesDomainAsync(file, domain))
                    return Path.GetFileName(file);
            }

            // 2. Procura em qualquer *.conf por server_name (ex: dominios manuais ou legados)
            foreach (var file in Directory.GetFiles(_configPath, "*.conf"))
            {
                if (await MatchesDomainAsync(file, domain))
                    return Path.GetFileName(file);
            }

            // 3. Procura em arquivos de backup *.conf.bak (quando o proxy foi desativado)
            foreach (var file in Directory.GetFiles(_configPath, "*.conf.bak"))
            {
                if (await MatchesDomainAsync(file, domain))
                {
                    var baseName = Path.GetFileName(file);
                    return baseName[..^4]; // remove .bak
                }
            }

            // 4. Fallback: correspondência direta por nome de arquivo comum
            var directCandidates = new[]
            {
                $"narc_proxy_{domain}.conf",
                $"{domain}.conf"
            };

            foreach (var candidate in directCandidates)
            {
                if (File.Exists(Path.Combine(_configPath, candidate)) || File.Exists(Path.Combine(_configPath, candidate + ".bak")))
                    return candidate;
            }

            return null;
        }

        private static async Task<bool> MatchesDomainAsync(string file, string domain)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                var matches = Regex.Matches(content, @"\bserver_name\s+([^;]+);");
                foreach (Match match in matches)
                {
                    var names = match.Groups[1].Value.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (Array.Exists(names, n => string.Equals(n, domain, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Recupera o dominio (server_name) parseado de um arquivo de config existente (ou backup .bak).
        /// Usado como fallback quando o painel manda delete_proxy ou toggle_config sem o campo domain.
        /// </summary>
        public async Task<string?> GetDomainFromConfigAsync(string proxyId)
        {
            var filename = proxyId.StartsWith("narc_proxy_") ? proxyId : $"narc_proxy_{proxyId}";
            if (!filename.EndsWith(".conf")) filename += ".conf";

            var path = Path.Combine(_configPath, filename);
            if (!File.Exists(path))
            {
                if (File.Exists(path + ".bak"))
                {
                    path += ".bak";
                }
                else
                {
                    return null;
                }
            }

            try
            {
                var content = await File.ReadAllTextAsync(path);
                var match = Regex.Match(content, @"server_name\s+(.+?);");
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NginxConfigService] Falha ao ler dominio: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Remove configs cujo ssl_certificate aponta para um arquivo inexistente.
        /// Chamado automaticamente apos falha de reload (NginxOrphanConfigException).
        /// </summary>
        public async Task CleanupOrphanConfigsAsync()
        {
            try
            {
                if (!Directory.Exists(_configPath)) return;

                var files = Directory.GetFiles(_configPath, "*.conf");
                foreach (var file in files)
                {
                    string content;
                    try { content = await File.ReadAllTextAsync(file); }
                    catch { continue; }

                    var match = Regex.Match(content, @"ssl_certificate\s+(.+?);");
                    if (!match.Success) continue;

                    var certPath = match.Groups[1].Value.Trim();
                    if (!File.Exists(certPath))
                    {
                        Console.WriteLine($"[NginxConfigService] Removendo orphan config {Path.GetFileName(file)} (cert ausente: {certPath})");
                        File.Delete(file);
                        var backup = file + ".bak";
                        if (File.Exists(backup)) File.Delete(backup);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NginxConfigService] Erro limpando orphan configs: {ex.Message}");
            }
        }

        /// <summary>
        /// Retorna a lista de todos os arquivos de configuração de proxy narc_proxy_*.conf.
        /// </summary>
        public System.Collections.Generic.List<string> GetAllProxyConfigFiles()
        {
            var list = new System.Collections.Generic.List<string>();
            if (Directory.Exists(_configPath))
            {
                list.AddRange(Directory.GetFiles(_configPath, "narc_proxy_*.conf"));
            }
            return list;
        }

        /// <summary>
        /// Retorna todos os arquivos .conf na pasta de configurações Nginx que podem ser configurações
        /// de proxy (narc_proxy_*.conf, arquivos com prefixo narc_, ou arquivos de domínio .conf).
        /// Ignora arquivos padrão do sistema como default.conf.
        /// </summary>
        public System.Collections.Generic.List<string> GetAllCandidateProxyFiles()
        {
            var list = new System.Collections.Generic.List<string>();
            if (!Directory.Exists(_configPath)) return list;

            foreach (var file in Directory.GetFiles(_configPath, "*.conf"))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("default.conf", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(file);
            }
            return list;
        }

        /// <summary>
        /// Verifica se o arquivo e seu conteúdo indicam que ele é uma configuração de proxy gerenciada pelo Narc.
        /// </summary>
        public static bool IsNarcManagedConfig(string filename, string content)
        {
            if (filename.StartsWith("narc_", StringComparison.OrdinalIgnoreCase)) return true;
            if (content.Contains("# Narc", StringComparison.OrdinalIgnoreCase)) return true;
            if (content.Contains("# Configuração NGINX", StringComparison.OrdinalIgnoreCase)) return true;
            if (content.Contains("narc_deactivated", StringComparison.OrdinalIgnoreCase)) return true;
            if (content.Contains("/etc/narc/ssl", StringComparison.OrdinalIgnoreCase)) return true;
            if (content.Contains(".well-known/acme-challenge", StringComparison.OrdinalIgnoreCase)) return true;
            if (content.Contains("proxy_pass http://", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Extrai todos os domínios listados na diretiva server_name de um arquivo .conf.
        /// </summary>
        public async Task<System.Collections.Generic.List<string>> ExtractDomainsFromConfigFileAsync(string filePath)
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                if (!File.Exists(filePath)) return list;
                var content = await File.ReadAllTextAsync(filePath);
                return ExtractDomainsFromConfigContent(content);
            }
            catch
            {
                return list;
            }
        }

        /// <summary>
        /// Extrai todos os domínios listados na diretiva server_name de um texto de configuração Nginx.
        /// </summary>
        public static System.Collections.Generic.List<string> ExtractDomainsFromConfigContent(string content)
        {
            var list = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(content)) return list;

            var matches = Regex.Matches(content, @"\bserver_name\s+([^;]+);");
            foreach (Match match in matches)
            {
                var names = match.Groups[1].Value.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var name in names)
                {
                    var cleaned = name.Trim();
                    if (!string.IsNullOrEmpty(cleaned) && cleaned != "_" && !list.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(cleaned);
                    }
                }
            }
            return list;
        }
    }
}
