using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Net.Http.Json;
using NarcAgent.Command.Interfaces;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NarcAgent.Command;
using NarcAgent.Core.Services;
using NarcAgent.Plugins;
using NarcAgent.Proxy;
using NarcAgent.Command.Models;
using DotNetEnv;

namespace NarcAgent
{
    class Program
    {
		// A API responde em snake_case/lowercase (id, status, token, refresh_token, expires_at),
		// mas o model AgentConnectResponse usa PascalCase. System.Text.Json e case-sensitive
		// por padrao, entao sem essa opcao TODAS as propriedades vinham null.
		private static readonly JsonSerializerOptions ApiJsonOptions = new() { PropertyNameCaseInsensitive = true };

		static async Task<(bool approved, string? token, DateTime? expiresAt, string? refreshToken)> RegisterAgentAsync(string apiUrl, string agentId, string agentName)
		{
			try
			{
				var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
				
				var fullUrl = apiUrl.TrimEnd('/') + "/api/agentes/conectar";
				var payload = new { id = agentId, name = agentName };
				var response = await httpClient.PostAsJsonAsync(fullUrl, payload);

				if (!response.IsSuccessStatusCode)
				{
					Console.WriteLine($"[NarcAgent] Registration failed: HTTP {response.StatusCode}");
					return (false, null, null, null);
				}

				var result = await response.Content.ReadFromJsonAsync<AgentConnectResponse>(ApiJsonOptions);
				if (result == null)
				{
					Console.WriteLine("[NarcAgent] Registration failed: empty response.");
					return (false, null, null, null);
				}

				if (result.Status == "approved")
				{
					DateTime? expiresAt = null;
					if (!string.IsNullOrEmpty(result.ExpiresAt) && DateTime.TryParse(result.ExpiresAt, out var parsedDate))
					{
						expiresAt = parsedDate;
					}
					Console.WriteLine($"[NarcAgent] Agent status: {result.Status}. Token expires at: {result.ExpiresAt}");
					
					return (true, result.Token, expiresAt, result.RefreshToken);
				}

				Console.WriteLine($"[NarcAgent] Agent status: {result.Status}. Waiting for admin approval...");
				return (false, null, null, null);
			}
			catch (HttpRequestException ex)
			{
				Console.WriteLine($"[NarcAgent] Registration failed: {ex.Message}");
				return (false, null, null, null);
			}
		}

        static string NormalizeWebSocketUrl(string apiUrl)
        {
            var url = apiUrl.TrimEnd('/');

            if (url.StartsWith("http://"))
            {
                url = "ws://" + url["http://".Length..];
            }
            else if (url.StartsWith("https://"))
            {
                url = "wss://" + url["https://".Length..];
            }
            else if (!url.StartsWith("ws://") && !url.StartsWith("wss://"))
            {
                url = "wss://" + url;
            }

            return url;
        }

        static async Task Main(string[] args)
        {
            Env.TraversePath().Load();

            var agentId = Environment.GetEnvironmentVariable("AGENT_ID") ?? "default-agent-id";
            var agentName = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "NarcAgent";
            var apiUrl = Environment.GetEnvironmentVariable("API_CENTRAL_URL") ?? "https://agent.narc.fun";

            // Use HTTPS for registration API
            var httpBase = apiUrl;
            if (httpBase.StartsWith("ws://"))
            {
                httpBase = "http://" + httpBase["ws://".Length..];
            }
            else if (httpBase.StartsWith("wss://"))
            {
                httpBase = "https://" + httpBase["wss://".Length..];
            }
            else if (!httpBase.StartsWith("http://") && !httpBase.StartsWith("https://"))
            {
                httpBase = "https://" + httpBase;
            }

            Console.WriteLine("========================================");
            Console.WriteLine("  NARC AGENT v3");
            Console.WriteLine("========================================");
            Console.WriteLine($"  Agent ID:   {agentId}");
            Console.WriteLine($"  Agent Name: {agentName}");
            Console.WriteLine($"  API URL:    {httpBase}");
            Console.WriteLine("========================================");

            while (true)
            {
                // 1. Try to load stored token, or register new agent
                string? authToken = null;
                string? refreshToken = null;
                DateTime? tokenExpiresAt = null;
                bool isRegistered = false;

                var tokenStorage = new TokenStorage();
                var storedToken = await tokenStorage.LoadAsync();

                if (storedToken != null && !string.IsNullOrEmpty(storedToken.Token))
                {
                    Console.WriteLine("[NarcAgent] Found stored token. Will use it for authentication.");
                    authToken = storedToken.Token;
                    refreshToken = storedToken.RefreshToken;
                    tokenExpiresAt = storedToken.ExpiresAt;
                    isRegistered = true;
                }
                else
                {
                    var regBackoff = TimeSpan.FromSeconds(5);
                    var maxRegBackoff = TimeSpan.FromSeconds(30);
                    while (!isRegistered)
                    {
                        (isRegistered, authToken, tokenExpiresAt, refreshToken) = await RegisterAgentAsync(httpBase, agentId, agentName);
                        if (!isRegistered)
                        {
                            Console.WriteLine($"[NarcAgent] Retrying in {regBackoff.TotalSeconds:F0} seconds...");
                            await Task.Delay(regBackoff);
                            regBackoff = TimeSpan.FromSeconds(Math.Min(maxRegBackoff.TotalSeconds, regBackoff.TotalSeconds * 1.5));
                        }
                    }

                    // Save the new token for future reconnects
                    if (!string.IsNullOrEmpty(authToken))
                    {
                        Console.WriteLine($"[NarcAgent] Saving new token and refresh token.");
                        await tokenStorage.SaveAsync(authToken, tokenExpiresAt, refreshToken);
                    }
                }

                // 2. Construct the WebSocket URL
                var wsBase = NormalizeWebSocketUrl(apiUrl);
                var wsUrl = $"{wsBase}/ws/agent/{agentId}";

                // 3. Setup plugins and start the agent
                var shellEnabled = bool.TryParse(Environment.GetEnvironmentVariable("SHELL_ENABLED"), out var se) ? se : false;

                Console.WriteLine("========================================");
                Console.WriteLine("  Starting plugins...");
                Console.WriteLine("========================================");

                var router = new CommandRouter();
                router.RegisterPlugin(new TelemetryPlugin());
                router.RegisterPlugin(new ShellPlugin(shellEnabled));
                router.RegisterPlugin(new ProxyPlugin());

                // Carrega plugins dinamicamente da pasta "plugins"
                var pluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
                if (Directory.Exists(pluginsDir))
                {
                    foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll"))
                    {
                        try
                        {
                            var asm = Assembly.LoadFrom(dll);
                            foreach (var type in asm.GetTypes())
                            {
                                if (typeof(IAgentPlugin).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                                {
                                    if (type == typeof(ProxyPlugin)) continue;
                                    var plugin = (IAgentPlugin)Activator.CreateInstance(type)!;
                                    router.RegisterPlugin(plugin);
                                    Console.WriteLine($"[PluginLoader] Loaded {type.Name} from {Path.GetFileName(dll)}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[PluginLoader] Failed to load {Path.GetFileName(dll)}: {ex.Message}");
                        }
                    }
                }

                Console.WriteLine("========================================");
                Console.WriteLine($"  Connecting to WebSocket...");
                Console.WriteLine($"  URL: {wsUrl}");
                Console.WriteLine("========================================");

                // 4. Start the agent with the authentication token
                try
                {
                    using var core = new AgentCore(router, wsUrl, agentId, agentName, authToken, tokenExpiresAt, tokenStorage, refreshToken);
                    await core.RunAsync(CancellationToken.None);
                    break; // If RunAsync returns normally, exit loop
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine($"\n[NarcAgent] {ex.Message}");
                    Console.WriteLine("[NarcAgent] Forcing re-registration loop...\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n[NarcAgent] Fatal error: {ex.Message}");
                    await Task.Delay(5000);
                }
            }
        }
    }
}