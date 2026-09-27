Agent C
│
├── Narc Manager
│   ├── register()
│   ├── heartbeat()
│   ├── getInfo()
│   ├── getServers()
│   └── getStatus()
│
├── Docker Manager
│   ├── create()
│   ├── start()
│   ├── stop()
│   ├── restart()
│   ├── remove()
│   └── inspect()
│
├── Console Manager
│   ├── sendCommand()
│   └── streamLogs()
│
├── File Manager
│   ├── upload()
│   ├── download()
│   └── delete()
│
├── Resource Manager
│   ├── CPU
│   ├── RAM
│   └── Disk
│
└── Proxy Manager
    └── Nginx



                        PAINEL
                       │
                       ▼
                      API
                       │
          ┌────────────┼────────────┐
          │            │            │
          ▼            ▼            ▼
       Node #1       Node #2      Node #3
       Agent C       Agent C      Agent C
          │            │            │
       Docker        Docker       Docker
          │            │            │
      ┌───┼───┐     ┌──┼───┐     ┌──┼───┐
      ▼   ▼   ▼     ▼  ▼   ▼     ▼  ▼   ▼
     S1  S2  S3    S4 S5  S6    S7 S8  S9

---

## Como Iniciar o Agente (Linux)

Após compilar (publish) o projeto para Linux, envie os arquivos para a máquina host e execute os seguintes comandos no diretório onde o binário está localizado:

```bash
# Dar permissão de execução ao binário
chmod +x ./NarcAgent

# Executar o agente (recomendado usar sudo pois gerencia Docker e NGINX)
sudo ./NarcAgent
```

*Dica: Para rodar em background de forma persistente, considere criar um serviço no `systemd`.*