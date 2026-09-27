using NarcAgent.Core;
using System;
using System.Threading.Tasks;

var agent = new AgentCore();

agent.Log += (sender, message) =>
{
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    Console.WriteLine($"[{timestamp}] [INFO] {message}");
};

agent.ConnectionStatusChanged += (sender, connected) =>
{
    var status = connected ? "Conectado" : "Desconectado";
    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [INFO] WebSocket: {status}");
};

try
{
    await agent.StartAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [ERROR] Erro fatal: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
finally
{
    agent.Stop();
}
