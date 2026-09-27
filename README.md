# NarcAgent v3

> **Agente de gerenciamento de servidores em tempo real** com controle via WebSocket, telemetria integrada, gerenciamento automatizado de reverse proxy via Nginx e ferramenta CLI de emergência com ACME v2 nativo.

---

## Índice

- [Visão Geral](#visão-geral)
- [Arquitetura Geral: Daemon vs Ferramenta CLI](#arquitetura-geral-daemon-vs-ferramenta-cli)
  - [1. O Daemon (NarcAgent) — Serviço Contínuo](#1-o-daemon-narcagent--serviço-contínuo-em-background)
  - [2. A Ferramenta CLI de Emergência (NarcAgent.Cli)](#2-a-ferramenta-cli-de-emergência-narcagentcli--intervenção-manual)
  - [Comparativo: Daemon vs Ferramenta CLI](#comparativo-daemon-vs-ferramenta-cli)
- [Ferramenta CLI de Emergência (`NarcAgent.Cli`)](#ferramenta-cli-de-emergência-narcagentcli)
  - [Quando Utilizar o CLI](#quando-utilizar-o-cli-de-emergência)
  - [Sintaxe e Parâmetros](#sintaxe-e-parâmetros)
  - [Fluxo de Execução do `--generate-ssl`](#fluxo-de-execução-do---generate-ssl)
  - [Exemplos Práticos](#exemplos-práticos-de-uso-do-cli)
- [Plugin NarcAgent.Proxy e Tecnologia ILRepack](#plugin-narcagentproxy-e-tecnologia-ilrepack)
  - [Eliminação do Certbot (ACME v2 100% Nativo)](#eliminação-do-certbot-acme-v2-100-nativo)
  - [O Desafio dos Plugins Dinâmicos com Múltiplas DLLs](#o-desafio-dos-plugins-dinâmicos-com-múltiplas-dlls)
  - [A Solução com ILRepack: Plugin de Arquivo Único](#a-solução-com-ilrepack-plugin-de-arquivo-único)
  - [Pipeline de Build no `ILRepack.targets`](#pipeline-de-build-no-ilrepacktargets)
- [Arquitetura Interna: EXE vs DLL](#arquitetura-interna-exe-vs-dll)
- [Estrutura do Projeto](#estrutura-do-projeto)
- [Como Funciona a Comunicação](#como-funciona-a-comunicação)
- [Ciclo de Vida do Agente](#ciclo-de-vida-do-agente)
- [Sistema de Plugins](#sistema-de-plugins)
- [Reverse Proxy e Nginx](#reverse-proxy-e-nginx)
- [Comandos e Ações Disponíveis](#comandos-e-ações-disponíveis)
- [Configuração](#configuração)
- [Build e Deploy](#build-e-deploy)
- [Tecnologias](#tecnologias)
- [Diagrama de Sequência: Atualizando um Proxy](#diagrama-de-sequência-atualizando-um-proxy)
- [Licença](#licença)

---

## Visão Geral

O **NarcAgent** é uma solução completa em **C# / .NET 8** projetada para infraestrutura de servidores de alta disponibilidade. Ele oferece monitoramento, telemetria em tempo real, execução remota e orquestração de reverse proxy **Nginx** com suporte a emissão automática e reparo de certificados SSL (Let's Encrypt / ACME v2).

O ecossistema é dividido estrategicamente em:
1. **Daemon em Background (`NarcAgent`)**: Conecta-se persistentemente via **WebSocket** à API Central para receber comandos em tempo real e transmitir métricas.
2. **CLI de Emergência (`NarcAgent.Cli`)**: Executável de intervenção manual que opera de forma **100% autônoma e offline da API Central**, permitindo geração imediata de certificados SSL e injeção nas configurações do Nginx.

```
┌────────────────────────────────────────────────────────────────────────┐
│                          INFRAESTRUTURA GERAL                          │
│                                                                        │
│   ┌─────────────────┐       WebSocket       ┌──────────────────────┐   │
│   │   API Central   │  ◄─────────────────►  │  NarcAgent (Daemon)  │   │
│   │  (Painel Web)   │                       │   (Serviço Host)     │   │
│   └─────────────────┘                       └──────────┬───────────┘   │
│                                                        │               │
│                                             ┌──────────┴───────────┐   │
│                                             │                      │   │
│   ┌─────────────────────┐                   ▼                      ▼   │
│   │ Sysadmin / Operador │            ┌─────────────┐        ┌────────┐ │
│   │   (Terminal SSH)    │            │  Telemetria │        │ Shell  │ │
│   └──────────┬──────────┘            └─────────────┘        └────────┘ │
│              │                                                         │
│              ▼                                                         │
│   ┌─────────────────────┐        (Carrega via Reflexão ou Referência)  │
│   │   NarcAgent.Cli     │───────────────────┐                          │
│   │ (Emergência Offline)│                   │                          │
│   └─────────────────────┘                   ▼                          │
│                                  ┌─────────────────────┐               │
│                                  │   NarcAgent.Proxy   │               │
│                                  │ (Single-File Plugin │               │
│                                  │    via ILRepack)    │               │
│                                  └──────────┬──────────┘               │
│                                             │                          │
│                                             ▼                          │
│                                  ┌─────────────────────┐               │
│                                  │     Nginx / SSL     │               │
│                                  │ (/etc/nginx/conf.d) │               │
│                                  └─────────────────────┘               │
└────────────────────────────────────────────────────────────────────────┘
```

---

## Arquitetura Geral: Daemon vs Ferramenta CLI

O NarcAgent foi projetado separando as responsabilidades de **orquestração contínua** e **intervenção emergencial**.

```
                 ┌──────────────────────────────────────┐
                 │       BINÁRIOS DO REPOSITÓRIO        │
                 └──────────────────┬───────────────────┘
                                    │
           ┌────────────────────────┴────────────────────────┐
           ▼                                                 ▼
┌─────────────────────────┐                       ┌─────────────────────────┐
│       NarcAgent         │                       │      NarcAgent.Cli      │
│  (Daemon de Produção)   │                       │   (Ferramenta de Linha   │
│                         │                       │     de Emergência)      │
├─────────────────────────┤                       ├─────────────────────────┤
│ • Processo contínuo     │                       │ • Execução sob demanda  │
│ • Long-running service  │                       │ • Short-lived process   │
│ • WebSocket persistente │                       │ • Não usa WebSocket     │
│ • Depende da API Central│                       │ • 100% Offline da API   │
│ • Telemetria contínua   │                       │ • Focado em SSL/Nginx   │
│ • Execução remota       │                       │ • Intervenção manual SSH│
└─────────────────────────┘                       └─────────────────────────┘
```

### 1. O Daemon (`NarcAgent`) — Serviço Contínuo em Background

- **Finalidade**: Executar continuamente no servidor hospedeiro como um daemon ou serviço (`systemd` no Linux ou Windows Service).
- **Conectividade**: Conecta-se à API Central através de um WebSocket persistente e seguro (`wss://agent.narc.fun/ws/agent/{agentId}`).
- **Autenticação e Resiliência**: Realiza handshake e registro com a API (`/api/agentes/conectar`), armazena credenciais em disco (`token.json`), gerencia expiração de JWT com refresh-tokens e aplica reconexão automática com backoff exponencial.
- **Roteamento de Comandos**: Recebe payloads JSON do painel web e os direciona via `CommandRouter` para os plugins apropriados (`TelemetryPlugin`, `ShellPlugin`, `ProxyPlugin`).
- **Carregamento Modular**: Além de plugins compilados internamente, detecta e carrega dinamicamente qualquer arquivo `.dll` localizado na pasta `plugins/` via reflexão (`Assembly.LoadFrom`).

### 2. A Ferramenta CLI de Emergência (`NarcAgent.Cli`) — Intervenção Manual

- **Finalidade**: Fornecer ao administrador de sistemas um utilitário direto de console para resolução de incidentes e operações manuais.
- **Independência Total da API Central**: **Não abre conexão WebSocket e não consulta a API Central**. Funciona mesmo se a API estiver fora do ar, se o servidor estiver sem acesso externo ou se o daemon estiver parado.
- **Operação Direta no Host**: Reutiliza diretamente a lógica de orquestração do `NarcAgent.Proxy` (`NginxService`, `NginxSslService`, `NginxConfigModifier`) para manipular arquivos de configuração e emitir certificados SSL.
- **Execução Síncrona e Imediata**: Emite o certificado através do protocolo ACME v2 nativo, injeta as diretivas HTTPS no arquivo `.conf` do Nginx, testa a sintaxe (`nginx -t`) e recarrega o serviço (`nginx -s reload`) em questão de segundos.

### Comparativo: Daemon vs Ferramenta CLI

| Característica | Daemon (`NarcAgent`) | CLI de Emergência (`NarcAgent.Cli`) |
|---|---|---|
| **Tipo de Processo** | Serviço de longa duração (background) | Utilitário pontual (executa e encerra) |
| **Ponto de Invocação** | Inicializado pelo sistema (`systemctl`, init script) | Invocado manualmente pelo sysadmin via SSH/console |
| **Comunicação de Rede** | WebSocket persistente com a API Central | Comunicação HTTP REST direta apenas com Let's Encrypt (ACME) |
| **Dependência da API** | Obrigatória (recebe comandos do painel) | **Nenhuma** (opera 100% desconectado do painel central) |
| **Geração de SSL** | Ação assíncrona recebida via WebSocket (`generate_ssl`) | Linha de comando imediata (`--generate-ssl <dominio>`) |
| **Telemetria / Shell** | Sim (CPU, memória, disco, comandos shell) | Não (focado exclusivamente em SSL e Nginx) |
| **Modo de Distribuição** | Binário único executável (`PublishSingleFile`) | Binário único executável (`PublishSingleFile`) |

---

## Ferramenta CLI de Emergência (`NarcAgent.Cli`)

A ferramenta CLI foi criada para garantir que o administrador nunca fique bloqueado caso o painel web esteja temporariamente inacessível ou ocorra alguma falha na fila de comandos WebSocket.

### Quando Utilizar o CLI de Emergência

1. **Painel Central Indisponível**: Quando a API Central ou o banco de dados principal estiver fora do ar e for necessário publicar ou corrigir o HTTPS de um domínio com urgência.
2. **Certificado Expirado ou com Erro**: Se um site entrar em falha de SSL e for necessário forçar uma reemissão e injeção imediata sem esperar o ciclo periódico.
3. **Debug e Homologação Local**: Para validar se o domínio aponta corretamente para o servidor e se a porta 80/443 do host responde ao desafio HTTP-01 antes de cadastrar no painel.

### Sintaxe e Parâmetros

```bash
# Sintaxe padrão com comando explícito
./NarcAgent.Cli --generate-ssl <dominio> [opções]

# Sintaxe simplificada (o domínio pode ser passado diretamente)
./NarcAgent.Cli <dominio> [opções]
```

#### Opções Disponíveis

| Parâmetro | Alias | Obrigatório? | Descrição |
|---|---|---|---|
| `--generate-ssl <dominio>` | `-ssl` | Sim* | Dispara a emissão de certificado SSL via ACME v2 nativo e aplica a injeção in-place no arquivo `.conf` correspondente. |
| `--domain <dominio>` | `-d` | Sim* | Define o domínio alvo caso não seja passado junto à flag `--generate-ssl`. |
| `--email <email>` | `-m` | Não | E-mail de registro no Let's Encrypt para avisos de expiração. Se omitido, busca no `.env` (`ACME_EMAIL`, `SSL_EMAIL`, `ADMIN_EMAIL`). |
| `--proxy-id <id>` | `--id` | Não | ID numérico do proxy (`narc_proxy_<id>.conf`). Se omitido, o CLI localiza automaticamente o arquivo `.conf` que possui o `server_name` correspondente. |
| `--help` | `-h` | Não | Exibe a ajuda detalhada com exemplos no terminal. |

*\* Pelo menos o domínio precisa ser informado (seja como valor de `--generate-ssl`, via `--domain` ou como primeiro argumento posicional).*

### Fluxo de Execução do `--generate-ssl`

Ao executar o comando no servidor, o `NarcAgent.Cli` realiza as seguintes etapas:

```
[CLI: 1/3] Instanciação e Leitura de Ambiente
   ├── Carrega .env via DotNetEnv
   ├── Normaliza o domínio (remove http://, https://, trailing slashes)
   └── Identifica o arquivo de configuração Nginx alvo:
       ├── Por proxy ID explícito (narc_proxy_<id>.conf)
       └── Ou varrendo /etc/nginx/conf.d em busca de 'server_name <dominio>'
           │
           ▼
[CLI: 2/3] Emissão de Certificado SSL Nativo (Certes / ACME v2)
   ├── Carrega ou cria chave de conta ACME (/etc/narc/ssl/account.key)
   ├── Cria nova Order no Let's Encrypt para o domínio
   ├── Gera token de desafio HTTP-01
   ├── Grava o desafio em /var/www/html/.well-known/acme-challenge/<token> (chmod 644)
   ├── Solicita validação e aguarda com polling até status Valid
   ├── Gera chave privada RSA de 2048 bits
   ├── Baixa a cadeia de certificados (fullchain.crt e privkey.key)
   ├── Salva os certificados em /etc/narc/ssl/<dominio>/ (chmod 600 na chave privada)
   └── Espelha cópia em /etc/letsencrypt/live/<dominio>/ para compatibilidade
           │
           ▼
[CLI: 3/3] Injeção In-Place e Recarregamento do Nginx
   ├── NginxConfigModifier lê o .conf original
   ├── Injeta diretiva 'listen 443 ssl http2;'
   ├── Adiciona caminhos dos certificados (ssl_certificate / ssl_certificate_key)
   ├── Insere redirecionamento 80 -> 443 (return 301 https://$host$request_uri;)
   ├── Grava arquivo atualizado preservando backup temporário
   ├── Executa 'nginx -t' (se falhar, reverte o arquivo imediatamente)
   └── Executa 'nginx -s reload'
```

### Exemplos Práticos de Uso do CLI

```bash
# 1. Geração simples com localização automática do .conf pelo domínio
./NarcAgent.Cli --generate-ssl api.byc.re

# 2. Informando e-mail de contato do Let's Encrypt e ID de proxy
./NarcAgent.Cli --generate-ssl api.byc.re --email admin@byc.re --id 99

# 3. Utilizando aliases curtos
./NarcAgent.Cli -ssl api.byc.re -m contato@byc.re

# 4. Sintaxe posicional direta
./NarcAgent.Cli api.byc.re --email admin@byc.re
```

---

## Plugin NarcAgent.Proxy e Tecnologia ILRepack

O projeto **`NarcAgent.Proxy`** é o coração da orquestração de rede do agente. Ele integra recursos avançados de manipulação de Nginx e emissão criptográfica de certificados em uma arquitetura de plugin modular e independente.

### Eliminação do Certbot (ACME v2 100% Nativo)

Em implementações tradicionais em Linux, agentes dependem da ferramenta **Certbot** instalada no sistema operacional. No entanto, o Certbot apresenta sérias desvantagens arquiteturais:

- **Dependências Pesadas no SO**: Exige Python 3, dezenas de bibliotecas do sistema, gerenciadores Snap ou pacotes APT específicos de cada distribuição.
- **Incompatibilidade Entre Distribuições**: Scripts que dependem do Certbot quebram facilmente em Alpine Linux, CentOS ou versões minimalistas de contêineres e VMs.
- **Fragilidade em Chamadas de Sistema**: Subprocessos executados via shell (`certbot certonly ...`) são difíceis de controlar, geram saídas de texto não estruturadas e quebram quando há prompts interativos inesperados.

> **O NarcAgent eliminou completamente o Certbot.**  
> O agente implementa o protocolo **ACME v2 (RFC 8555)** nativamente em código C# utilizando as bibliotecas `Certes` e `BouncyCastle.Cryptography`. Todas as requisições para a autoridade certificadora (Let's Encrypt), geração de par de chaves RSA e validação de desafios HTTP-01 são feitas internamente pelo processo .NET, **sem exigir a instalação de nenhum pacote externo no servidor além do próprio Nginx**.

---

### O Desafio dos Plugins Dinâmicos com Múltiplas DLLs

O NarcAgent foi construído com arquitetura de plugins desacoplada. O daemon carrega o `NarcAgent.Proxy.dll` dinamicamente em tempo de execução através da pasta `plugins/`:

```csharp
var asm = Assembly.LoadFrom("plugins/NarcAgent.Proxy.dll");
```

No entanto, o `NarcAgent.Proxy` depende de bibliotecas externas complexas:
- **`Certes`** (versão 4.0.0 — cliente ACME v2)
- **`BouncyCastle.Cryptography`** (cálculos criptográficos, PKCS, PEM, ASN.1)

Se a compilação gerasse múltiplos arquivos soltos:
```
plugins/
├── NarcAgent.Proxy.dll
├── Certes.dll
└── BouncyCastle.Cryptography.dll
```

Surgiriam dois problemas críticos:
1. **Problema de Resolução do AssemblyLoadContext**: No .NET Core/.NET 8+, assemblies carregados isoladamente com `Assembly.LoadFrom` nem sempre conseguem localizar automaticamente suas bibliotecas dependentes quando estas estão no diretório de plugins, resultando em erros fatais de runtime (`System.IO.FileNotFoundException`).
2. **Poluição de Arquivos e Deploy Complexo**: Atualizar ou distribuir plugins com múltiplos arquivos soltos eleva o risco de versões descompassadas no ambiente de produção.

---

### A Solução com ILRepack: Plugin de Arquivo Único

Para alcançar a máxima robustez e simplicidade operacional, o projeto utiliza a ferramenta **`ILRepack.Lib.MSBuild.Task`**.

O **ILRepack** é uma alternativa open-source de alta performance ao clássico ILMerge. Ele opera diretamente sobre o código de linguagem intermediária (CIL/bytecode .NET), fundindo os assemblies compilados e suas dependências de terceiros em um **único binário monolítico**.

#### Pipeline de Build no `ILRepack.targets`

A fusão ocorre automaticamente no ciclo de compilação do MSBuild através do arquivo `NarcAgent.Proxy/ILRepack.targets`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <!-- 1. Executa no AfterBuild quando Certes.dll é encontrada na pasta de saída -->
  <Target Name="AfterBuild" AfterTargets="Build" Condition="Exists('$(TargetDir)Certes.dll')">
    <Message Importance="high" Text="--- ILRepack: Merging Certes and BouncyCastle into $(TargetPath) ---" />
    <ItemGroup>
      <!-- Inclui o assembly principal e suas dependências externas -->
      <InputAssemblies Include="$(IntermediateOutputPath)$(TargetName)$(TargetExt)" />
      <InputAssemblies Include="$(TargetDir)Certes.dll" />
      <InputAssemblies Include="$(TargetDir)BouncyCastle.Cryptography.dll" />
    </ItemGroup>
    
    <!-- Realiza a fusão dos assemblies em um único DLL -->
    <ILRepack
      Parallel="true"
      DebugInfo="true"
      AllowDuplicateResources="false"
      InputAssemblies="@(InputAssemblies)"
      TargetKind="Dll"
      OutputFile="$(TargetPath)"
      LibraryPath="$(TargetDir)"
    />
    
    <!-- Limpa os arquivos soltos desnecessários -->
    <Delete Files="$(TargetDir)Certes.dll;$(TargetDir)BouncyCastle.Cryptography.dll;$(TargetDir)Certes.pdb;$(TargetDir)BouncyCastle.Cryptography.pdb" />
  </Target>

  <!-- 2. Impede que o dotnet publish copie arquivos avulsos já mesclados -->
  <Target Name="ExcludeMergedFilesFromPublish" AfterTargets="ComputeResolvedFilesToPublishList">
    <ItemGroup>
      <ResolvedFileToPublish Remove="@(ResolvedFileToPublish)" Condition="'%(Filename)' == 'Certes' or '%(Filename)' == 'BouncyCastle.Cryptography'" />
    </ItemGroup>
  </Target>

  <!-- 3. Garante que o diretório publicado receba o DLL já unificado -->
  <Target Name="CopyMergedDllToPublishDir" AfterTargets="Publish" Condition="'$(PublishDir)' != '' and Exists('$(TargetPath)')">
    <Message Importance="high" Text="--- ILRepack: Copying merged $(TargetPath) to $(PublishDir) ---" />
    <Copy SourceFiles="$(TargetPath)" DestinationFiles="$(PublishDir)$(TargetName)$(TargetExt)" />
  </Target>
</Project>
```

#### Vantagens do Plugin Single-File:
- **Distribuição "Plug-and-Play"**: A pasta `plugins/` contém rigorosamente apenas um arquivo: `NarcAgent.Proxy.dll`.
- **Zero Falhas de Dependência**: Todas as classes do Certes e do BouncyCastle já residem dentro do próprio assembly do plugin.
- **Portabilidade Absoluta**: Funciona idêntico em Windows e Linux, sem necessidade de pacotes extras instalados no sistema hospedeiro.

---

## Arquitetura Interna: EXE vs DLL

Para compreender a execução em baixo nível no .NET:

### O que é um executável (`.exe` ou binário Linux)?
- É o **ponto de entrada** da aplicação, compilado com `<OutputType>Exe</OutputType>`.
- Contém o método estático `Main()` e inicializa o runtime do .NET (CLR).
- O repositório gera dois executáveis distintos:
  - `NarcAgent` (o Daemon WebSocket)
  - `NarcAgent.Cli` (a ferramenta de emergência)

### O que é uma biblioteca (`.dll`)?
- É uma biblioteca de tipos e métodos compilada com `<OutputType>Library</OutputType>`.
- Não pode ser iniciada diretamente pelo sistema operacional; ela é carregada em memória por um processo executável.
- É reutilizável entre o Daemon e a CLI (`NarcAgent.Core`, `NarcAgent.Command`, `NarcAgent.Proxy`).

### Analogia Prática

| Componente | Analogia | Função Real |
|---|---|---|
| `NarcAgent` (Daemon) | **Motorista automático** | Roda continuamente, recebe ordens do painel via rádio (WebSocket) e pilota o carro. |
| `NarcAgent.Cli` | **Chave de fenda / Ferramenta manual** | O mecânico usa manualmente no capô quando precisa resolver algo na hora. |
| `NarcAgent.Core.dll` | **Sistema de tração** | Gerencia conectividade, filas resilientes e loop de eventos. |
| `NarcAgent.Proxy.dll` | **Módulo Nginx & SSL (ILRepack)** | Caixa preta autocontida que manipula arquivos de configuração e emite SSL nativo. |
| `NarcAgent.Plugins.dll` | **Sensores do painel** | Coleta telemetria e executa comandos bash autorizados. |

---

## Estrutura do Projeto

A solução está estruturada em camadas bem definidas e desacopladas:

```
NarcAgent.sln                           (Solution file do .NET 8)
│
├── NarcAgent                             ← ⭐ Executável Daemon (Background Service)
│   ├── Program.cs                        Ponto de entrada: registro de agente, loop WS e plugin loader
│   └── NarcAgent.csproj
│
├── NarcAgent.Cli                         ← 🚨 Ferramenta CLI de Emergência
│   ├── Program.cs                        Ponto de entrada do CLI (--generate-ssl, in-place modifier)
│   └── NarcAgent.Cli.csproj
│
├── NarcAgent.Core                        ← 🧠 Núcleo de Comunicação e Resiliência
│   ├── Services/AgentCore.cs             Cliente WebSocket, reconexão resiliente, envio assíncrono
│   ├── Services/AgentConnectService.cs   Serviço de registro HTTP e renovação de token
│   ├── Services/TokenStorage.cs          Armazenamento seguro de token/refresh-token em disco
│   └── NarcAgent.Core.csproj
│
├── NarcAgent.Command                     ← 📜 Contratos e Roteamento
│   ├── Interfaces/IAgentPlugin.cs        Interface unificada que todo plugin deve implementar
│   ├── CommandRouter.cs                  Roteador de comandos baseado no campo "action"
│   ├── Models/CommandResult.cs           Modelo padronizado de resposta (stdout, stderr, kind)
│   ├── Models/AgentConnectResponse.cs    Modelo de autenticação com a API Central
│   └── NarcAgent.Command.csproj
│
├── NarcAgent.Plugins                     ← 🛠 Plugins Básicos
│   ├── ShellPlugin.cs                    Execução controlada de comandos shell (bash / cmd)
│   ├── TelemetryPlugin.cs                Métricas do host (CPU, memória RAM, discos, uptime, SO)
│   └── NarcAgent.Plugins.csproj
│
├── NarcAgent.Proxy                       ← 🌐 Reverse Proxy & SSL (Single-File c/ ILRepack)
│   ├── ProxyPlugin.cs                    Implementação de IAgentPlugin para o Daemon
│   ├── ILRepack.targets                  MSBuild Target: mescla Certes + BouncyCastle na DLL
│   ├── NarcAgent.Proxy.csproj
│   └── Services/Nginx/                   Serviços modulares do Nginx
│       ├── NginxService.cs               Orquestrador de operações (thread-safe c/ SemaphoreSlim)
│       ├── NginxConfigGenerator.cs       Gera blocos HTTP/HTTPS completos
│       ├── NginxConfigModifier.cs        Injeção in-place de SSL e verificação sintática
│       ├── NginxConfigService.cs         Manipulação de arquivos em /etc/nginx/conf.d
│       ├── NginxProcessService.cs        Interage com o processo do Nginx (test, reload, start)
│       └── NginxSslService.cs            ACME v2 nativo via Certes (zero Certbot)
│
└── NarcAgent.Tests                       ← 🧪 Testes Automatizados (xUnit)
    ├── CliModeAndDiscoveryTests.cs       Testes do CLI e descoberta automática de arquivos .conf
    ├── NginxConfigGeneratorTests.cs      Testes de geração de blocos Nginx
    └── NginxConfigModifierTests.cs       Testes de injeção in-place e redirects HTTPS
```

### Grafo de Dependências

```
   ┌──────────────────┐           ┌──────────────────┐
   │    NarcAgent     │           │  NarcAgent.Cli   │
   │     (Daemon)     │           │   (Emergência)   │
   └────────┬─────────┘           └────────┬─────────┘
            │                              │
            ├──► NarcAgent.Core            │
            │          │                   │
            │          ├──► NarcAgent.Command
            │          │
            ├──► NarcAgent.Plugins
            │
            └──► NarcAgent.Proxy ◄─────────┘
                       │
                       └──► [ILRepack: Certes + BouncyCastle]
```

---

## Como Funciona a Comunicação

O Daemon utiliza **WebSocket** bidirecional em tempo real com a API Central:

### Ciclo da Conexão

```
Agente (Daemon)                         API Central
   │                                         │
   │  1. Handshake WebSocket (HTTP 101)      │
   │ ──────────────────────────────────────► │
   │                                         │
   │  2. Heartbeat contínuo (Ping/Pong)      │
   │  <───────── a cada 30 segundos ────────>│
   │                                         │
   │  3. API Central envia comando JSON      │
   │  <───────────────── {"action":"..."}────│
   │                                         │
   │  4. Agente processa e responde          │
   │  ─────────────────> {"kind":"success"}──│
   │                                         │
   │  5. Telemetria periódica                │
   │  ─────────────────> {"cpu": 32, ...}────│
```

### Exemplo de Comando Recebido pela API Central

```json
{
  "action": "update_proxy",
  "requestId": "uuid-8791",
  "data": {
    "proxyId": "42",
    "domain": "api.byc.re",
    "targetHost": "127.0.0.1",
    "targetPort": 5000,
    "enabled": true,
    "ssl": true
  }
}
```

### Exemplo de Resposta Emitida pelo Agente

```json
{
  "requestId": "uuid-8791",
  "kind": "success",
  "stdout": "Proxy atualizado com sucesso e Nginx recarregado.",
  "stderr": null
}
```

---

## Ciclo de Vida do Agente

O `AgentCore` gerencia a integridade da comunicação através dos seguintes mecanismos:

1. **Reconexão com Backoff Exponencial**: Quando ocorre perda de sinal ou interrupção de rede, o agente aguarda intervalos crescentes (5s, 7.5s, até o limite de 30s) antes de tentar restabelecer o canal.
2. **Fila de Resultados Pendentes**: Se um comando é executado enquanto a rede cai, a resposta é armazenada em uma fila interna resiliente (com limite de 50 itens e TTL de 5 minutos). Ao restabelecer a conexão, todos os itens pendentes são despachados automaticamente.
3. **Shutdown Gracioso**: Respeita sinais de cancelamento (`CancellationToken`), fechando os sockets de forma limpa sem deixar conexões órfãs no host.

---

## Sistema de Plugins

Todo recurso de execução implementa a interface contratual `IAgentPlugin`:

```csharp
public interface IAgentPlugin
{
    IReadOnlySet<string> SupportedActions { get; }
    Task<CommandResult> ExecuteAsync(JsonDocument command, CancellationToken ct);
}
```

O `CommandRouter` registra as instâncias e roteia cada mensagem conforme o campo `action`.

### Plugins Nativos

| Plugin | Ações Suportadas | Descrição |
|---|---|---|
| **TelemetryPlugin** | `request_telemetry` | Coleta uso de CPU, memória RAM, partições de disco, uptime do host e dados do sistema operacional. |
| **ShellPlugin** | `shell_execute` | Executa comandos no shell do sistema operacional (apenas se `SHELL_ENABLED=true` no `.env`). |
| **ProxyPlugin** | `update_proxy`, `delete_proxy`, `toggle_config`, `get_config`, `generate_ssl` | Controla o ciclo de vida completo do Nginx e a emissão/injeção de certificados SSL. |

---

## Reverse Proxy e Nginx

O NarcAgent atua como um gerenciador de alto nível para o **Nginx**:

### Serviços Nginx Integrados

| Serviço | Responsabilidade |
|---|---|
| **`NginxService`** | Orquestrador principal. Garante concorrência segura com `SemaphoreSlim` para evitar múltiplos reloads concorrentes. |
| **`NginxConfigGenerator`** | Gera blocos de configuração `.conf` para modos `HTTP`, `HTTPS` e `Disabled`. |
| **`NginxConfigModifier`** | Realiza injeção in-place de blocos SSL em arquivos `.conf` existentes, com suporte a rollback automático se `nginx -t` falhar. |
| **`NginxConfigService`** | Leitura, gravação, exclusão e busca de arquivos `.conf` por domínio (`server_name`). |
| **`NginxProcessService`** | Executa comandos de processo do Nginx (`nginx -t`, `nginx -s reload`, `systemctl reload nginx`). |
| **`NginxSslService`** | Cliente ACME v2 nativo em C# via Certes. Emite certificados Let's Encrypt sem dependência de Certbot. |

### Modos de Configuração

- **HTTP**: Escuta na porta 80, repassando o tráfego para a aplicação interna (`proxy_pass`).
- **HTTPS**: Escuta na porta 443 com SSL ativado (HTTP/2 habilitado) e redireciona automaticamente requisições da porta 80 para HTTPS (301 Moved Permanently).
- **Disabled**: Comenta o bloco de configuração, mantendo o site temporariamente fora do ar sem deletar o arquivo.

---

## Comandos e Ações Disponíveis

Ações disponíveis via conexão WebSocket:

| Ação | Parâmetros Principais | Descrição |
|---|---|---|
| `request_telemetry` | *(nenhum)* | Retorna JSON com telemetria detalhada de hardware e sistema. |
| `shell_execute` | `command`, `args` | Executa comando de terminal no host (requer `SHELL_ENABLED=true`). |
| `update_proxy` | `proxyId`, `domain`, `targetHost`, `targetPort`, `enabled`, `ssl` | Cria ou atualiza um arquivo de reverse proxy no Nginx. |
| `delete_proxy` | `proxyId`, `domain` | Remove a configuração de proxy e certificados associados. |
| `toggle_config` | `domain`, `mode` (`http`/`https`/`disabled`) | Altera o estado do site sem recriar as diretivas. |
| `get_config` | `domain` | Retorna o conteúdo textual do arquivo `.conf` ativo. |
| `generate_ssl` | `domain`, `email` | Emite certificado SSL via ACME v2 e atualiza a configuração. |

---

## Configuração

### Arquivo `.env`

Crie um arquivo `.env` na raiz do executável (baseado no `.env.example`):

```bash
# Identificação do Servidor
AGENT_ID=nortlin-sp-01
AGENT_NAME=nortlin-01

# URL da API Central
API_CENTRAL_URL=https://agent.narc.fun

# Segurança - Habilita/Desabilita execução remota de shell
SHELL_ENABLED=false

# E-mail para notificações do Let's Encrypt (opcional)
ACME_EMAIL=admin@meudominio.com

# Caminhos personalizados do Nginx (opcional, padrão detectado automaticamente)
# NGINX_CONF_DIR=/etc/nginx/conf.d
# NGINX_BIN=/usr/sbin/nginx
```

---

## Build e Deploy

### Requisitos

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 ou Linux (x64 / arm64)

### Script Automatizado (`build.bat`)

O repositório fornece um script interativo de build multiplataforma (`build.bat`):

```bash
$ build.bat
```

Opções de compilação disponíveis:
- **[1] Windows (win-x64)**: Gera `NarcAgent.exe` e `NarcAgent.Cli.exe`.
- **[2] Linux (linux-x64)**: Gera binários ELF autocontidos para servidores Linux convencionais.
- **[3] Linux ARM (linux-arm64)**: Gera binários para arquitetura ARM (Raspberry Pi, instâncias AWS Graviton, etc.).
- **[4] Todas as plataformas**: Compila em lote para todos os alvos acima.

### O que acontece durante o Build?

Para cada plataforma de destino (`rid`), o script executa 4 etapas:

1. **Limpeza da pasta de saída**: Remove compilações anteriores em `build/<rid>/`.
2. **Publicação do Daemon (`NarcAgent`)**:
   ```bash
   dotnet publish NarcAgent/NarcAgent.csproj -c Release -r <rid> --self-contained false -p:PublishSingleFile=true -o build/<rid>
   ```
3. **Publicação do CLI de Emergência (`NarcAgent.Cli`)**:
   ```bash
   dotnet publish NarcAgent.Cli/NarcAgent.Cli.csproj -c Release -r <rid> --self-contained false -p:PublishSingleFile=true -o build/<rid>
   ```
4. **Publicação e Empacotamento do Plugin (`NarcAgent.Proxy`)**:
   ```bash
   dotnet publish NarcAgent.Proxy/NarcAgent.Proxy.csproj -c Release -r <rid> -o build/<rid>/plugins
   ```
   *Neste passo, o **ILRepack** entra em ação e mescla `Certes.dll` e `BouncyCastle.Cryptography.dll` dentro de `NarcAgent.Proxy.dll`, limpando arquivos soltos.*

### Estrutura Final da Pasta de Build

Após a compilação, o diretório de distribuição fica organizado e limpo:

```
build/linux-x64/
├── NarcAgent                  ← Executável único do Daemon (background service)
├── NarcAgent.Cli              ← Executável único do CLI de Emergência
└── plugins/
    └── NarcAgent.Proxy.dll    ← Plugin único autossuficiente (mesclado via ILRepack)
```

### Como Executar em Servidores Linux

1. Envie o conteúdo da pasta `build/linux-x64/` para o servidor (ex.: `/opt/narc-agent`).
2. Defina as permissões de execução:
   ```bash
   chmod +x /opt/narc-agent/NarcAgent
   chmod +x /opt/narc-agent/NarcAgent.Cli
   ```
3. Crie o arquivo `.env` com suas configurações.
4. Execute o agente (recomendado via `sudo` para gerenciamento do Nginx):
   ```bash
   sudo ./NarcAgent
   ```

Para rodar em background permanentemente, configure uma unidade no `systemd` (`/etc/systemd/system/narcagent.service`).

---

## Tecnologias

| Tecnologia | Finalidade no Projeto |
|---|---|
| **.NET 8.0 (C# 12)** | Runtime de alta performance multiplataforma |
| **System.Net.WebSockets** | Conexão full-duplex de baixa latência com a API Central |
| **Certes (4.0.0)** | Cliente ACME v2 nativo em C# (dispensa o Certbot) |
| **BouncyCastle.Cryptography** | Criptografia para geração de chaves RSA e certificados X.509 |
| **ILRepack (`ILRepack.Lib.MSBuild.Task`)** | Mesclagem de assemblies em tempo de build para gerar plugin de arquivo único |
| **Nginx** | Servidor web e reverse proxy de produção |
| **DotNetEnv** | Carregamento transparente de variáveis de ambiente do `.env` |
| **xUnit** | Framework de testes unitários automatizados |

---

## Diagrama de Sequência: Atualizando um Proxy

```
API Central                  NarcAgent (Daemon)           Nginx / SO
     │                              │                         │
     │  {"action":"update_proxy"}   │                         │
     ├─────────────────────────────>│                         │
     │                              │                         │
     │                              │  NginxConfigGenerator   │
     │                              │  .Generate("site.com")  │
     │                              ├────────────────────────>│
     │                              │  (Gera texto do .conf)  │
     │                              │<────────────────────────┤
     │                              │                         │
     │                              │  NginxConfigService     │
     │                              │  .SaveConfig()          │
     │                              ├────────────────────────>│
     │                              │  (Grava no disco)       │
     │                              │<────────────────────────┤
     │                              │                         │
     │                              │  NginxProcessService    │
     │                              │  .Reload()              │
     │                              ├────────────────────────>│
     │                              │  (nginx -s reload)      │
     │                              │<────────────────────────┤
     │                              │                         │
     │     {"kind":"success"}       │                         │
     │<─────────────────────────────┤                         │
```

---

## Licença

Este projeto é distribuído sob a licença [MIT](LICENSE).

---

> **Desenvolvido por ByCronoz — NarcCore.**
