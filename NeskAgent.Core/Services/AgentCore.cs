using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NeskAgent.Command;
using NeskAgent.Command.Models;

namespace NeskAgent.Core.Services
{
    public sealed class AgentCore : IDisposable
    {
        private readonly CommandRouter _router;
        private Uri _serverUri;
        private readonly string _agentId;
        private readonly string _agentName;
        private string? _authToken;
        private DateTime? _tokenExpiresAt;
        private string? _refreshToken;
        private readonly TokenStorage? _tokenStorage;
        private ClientWebSocket? _ws;
        private CancellationTokenSource _cts = new();
        private readonly Queue<PendingResult> _pendingResults = new();
        private readonly SemaphoreSlim _queueLock = new(1, 1);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly HttpClient _httpClient;

        private const int MaxPendingResults = 50;
        private static readonly TimeSpan ResultTtl = TimeSpan.FromMinutes(5);

        public AgentCore(CommandRouter router, string serverUri, string agentId, string agentName, string? authToken = null, DateTime? tokenExpiresAt = null, TokenStorage? tokenStorage = null, string? refreshToken = null)
        {
            _router = router;
            _serverUri = NormalizeWebSocketUri(serverUri);
            _agentId = agentId;
            _agentName = agentName;
            _authToken = authToken;
            _tokenExpiresAt = tokenExpiresAt;
            _refreshToken = refreshToken;
            _tokenStorage = tokenStorage;
            _httpClient = new HttpClient { BaseAddress = new Uri(serverUri.Replace("wss://", "https://").Replace("ws://", "http://")) };
        }

        public async Task RunAsync(CancellationToken externalCt)
        {
            if (IsTokenExpiredOrNearExpiry() && !string.IsNullOrEmpty(_refreshToken))
            {
                Console.WriteLine("[AgentCore] Access token is expired or missing. Attempting immediate renewal with refresh token...");
                await RenewTokenAsync(externalCt);
            }

            _ = TokenRefreshLoopAsync(externalCt);
            _ = TelemetryLoopAsync(externalCt);

            while (!externalCt.IsCancellationRequested)
            {
                try
                {
                    await ConnectAsync(externalCt);
                    await ReceiveLoopAsync(externalCt);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AgentCore] Connection error: {ex.Message}");
                }

                if (!externalCt.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), externalCt);
                }
            }
        }

        private async Task TokenRefreshLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(25), ct);
                    await RenewTokenAsync(ct);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AgentCore] Token refresh error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Loop que envia telemetria periodicamente para a API central.
        /// Atualiza CPU, RAM, DISK, uptime e OS a cada 5 segundos.
        /// </summary>
        private async Task TelemetryLoopAsync(CancellationToken ct)
        {
            // Espera um pouco antes do primeiro envio
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SendTelemetryAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AgentCore] Telemetry loop error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>
        /// Coleta e envia telemetria para a API central
        /// </summary>
        private async Task SendTelemetryAsync()
        {
            try
            {
                var command = JsonDocument.Parse("{\"action\":\"request_telemetry\"}");
                var result = await _router.RouteAsync(command, _cts?.Token ?? default);

                if (result != null && !string.IsNullOrEmpty(result.Payload))
                {
                    await SendAsync(result.Payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AgentCore] Error sending telemetry: {ex.Message}");
            }
        }

        private async Task RenewTokenAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_authToken) && string.IsNullOrEmpty(_refreshToken))
                return;

            try
            {
                Console.WriteLine("[AgentCore] Renewing token...");
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/agentes/renovar-token");
                if (!string.IsNullOrEmpty(_authToken))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);
                }
                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    var body = new { refresh_token = _refreshToken };
                    request.Content = new StringContent(
                        JsonSerializer.Serialize(body),
                        System.Text.Encoding.UTF8,
                        "application/json"
                    );
                }

                var response = await _httpClient.SendAsync(request, ct);
                var content = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.TryGetProperty("token", out var newToken))
                {
                    _authToken = newToken.GetString();
                    if (root.TryGetProperty("expires_at", out var exp) && DateTime.TryParse(exp.GetString(), out var expTime))
                    {
                        _tokenExpiresAt = expTime;
                    }
                    Console.WriteLine("[AgentCore] Token renewed successfully!");

                    if (_tokenStorage != null && !string.IsNullOrEmpty(_authToken))
                    {
                        await _tokenStorage.SaveAsync(_authToken, _tokenExpiresAt);
                        Console.WriteLine("[AgentCore] Token persisted to agent.nortlin");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AgentCore] Failed to renew token: {ex.Message}");
            }
        }

        private async Task ConnectAsync(CancellationToken ct)
        {
            _ws?.Dispose();
            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            if (!string.IsNullOrEmpty(_authToken))
            {
                _ws.Options.SetRequestHeader("Authorization", $"Bearer {_authToken}");
            }

            var combined = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct).Token;

            try
            {
                await _ws.ConnectAsync(_serverUri, combined);
                Console.WriteLine($"[AgentCore] Connected to {_serverUri}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AgentCore] Failed to connect to {_serverUri}: {ex.Message}");
                throw;
            }

            await FlushPendingResultsAsync(combined);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new MemoryStream();
            var segment = new byte[8192];

            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                buffer.SetLength(0);

                try
                {
                    ValueWebSocketReceiveResult result;

                    do
                    {
                        if (ct.IsCancellationRequested) return;

                        var memory = new Memory<byte>(segment);
                        result = await _ws.ReceiveAsync(memory, ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Console.WriteLine("[AgentCore] WebSocket closed by server.");
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            buffer.Write(segment, 0, result.Count);
                        }
                    } while (!result.EndOfMessage);

                    if (buffer.Length > 0)
                    {
                        var message = Encoding.UTF8.GetString(buffer.ToArray());
                        await HandleMessageAsync(message);
                    }
                }
                catch (WebSocketException wsEx)
                {
                    Console.WriteLine($"[AgentCore] WebSocket error: {wsEx.Message}");
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AgentCore] Unexpected error in receive loop: {ex.Message}");
                    return;
                }
            }
        }

        private async Task HandleMessageAsync(string messageJson)
        {
            try
            {
                Console.WriteLine($"[AgentCore] Received command: {messageJson}");

                using var doc = JsonDocument.Parse(messageJson);

                // Extrai metadados uteis para ecoar nas respostas
                string? action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() : null;
                string? requestId = doc.RootElement.TryGetProperty("request_id", out var r) ? r.GetString() : null;
                string? incomingAgentId = doc.RootElement.TryGetProperty("agent_id", out var aid) ? aid.GetString() : null;

                // Status update: apenas loga
                if (action == "update_status")
                {
                    if (doc.RootElement.TryGetProperty("data", out var dataElement) &&
                        dataElement.TryGetProperty("status", out var statusElement))
                    {
                        Console.WriteLine($"[AgentCore] Status updated to: {statusElement.GetString()}");
                    }
                    return;
                }

                // Roteia para o plugin
                var result = await _router.RouteAsync(doc, CancellationToken.None);
                if (result == null) return;

                // Constrói o envelope correto que a API espera
                var envelope = BuildEnvelope(result, action, requestId, incomingAgentId);
                await SendAsync(JsonSerializer.Serialize(envelope));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AgentCore] Error handling message: {ex.Message}");
            }
        }

        /// <summary>
        /// Converte um CommandResult em um envelope JSON no formato esperado pela API:
        /// - type=command_result para Ack/Async
        /// - type=config_content quando o plugin retorna o conteudo bruto de uma config
        /// - type=error para erros
        /// - se o plugin ja retornou JSON valido com um campo "type" no Payload, usa esse envelope direto
        /// </summary>
        private static Dictionary<string, object?> BuildEnvelope(
            CommandResult result,
            string? action,
            string? requestId,
            string? agentId)
        {
            // Se o plugin devolveu um Payload que ja é JSON com "type" (ex: TelemetryPlugin),
            // mescla com request_id/agent_id e retorna o envelope completo.
            if (!string.IsNullOrEmpty(result.Payload))
            {
                try
                {
                    using var pdoc = JsonDocument.Parse(result.Payload);
                    if (pdoc.RootElement.ValueKind == JsonValueKind.Object &&
                        pdoc.RootElement.TryGetProperty("type", out _))
                    {
                        var dict = new Dictionary<string, object?>();
                        foreach (var prop in pdoc.RootElement.EnumerateObject())
                        {
                            dict[prop.Name] = JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
                        }
                        if (!string.IsNullOrEmpty(requestId) && !dict.ContainsKey("request_id"))
                            dict["request_id"] = requestId;
                        if (!string.IsNullOrEmpty(agentId) && !dict.ContainsKey("agent_id"))
                            dict["agent_id"] = agentId;
                        return dict;
                    }
                }
                catch { /* nao era JSON, segue o fluxo normal */ }
            }

            // Fallback: monta o envelope baseado no Kind
            var env = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            switch (result.Kind)
            {
                case CommandResultKind.Ack:
                case CommandResultKind.Async:
                    env["type"] = "command_result";
                    env["command"] = action ?? "";
                    env["success"] = result.Success;
                    env["message"] = result.Message;
                    env["async"] = result.Kind == CommandResultKind.Async;
                    if (!string.IsNullOrEmpty(result.Payload)) env["payload"] = result.Payload;
                    break;

                case CommandResultKind.Content:
                    // get_config / save_config devolvem texto cru; envelopa como config_content
                    env["type"] = "config_content";
                    env["filename"] = "";
                    env["content"] = result.Payload;
                    env["success"] = result.Success;
                    if (!string.IsNullOrEmpty(result.Message)) env["message"] = result.Message;
                    break;

                case CommandResultKind.Error:
                    env["type"] = "error";
                    env["error"] = result.Message;
                    env["command"] = action ?? "";
                    break;
            }

            if (!string.IsNullOrEmpty(requestId))
                env["request_id"] = requestId;

            return env;
        }

        public async Task SendAsync(string message)
        {
            if (_ws?.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                var segment = new ArraySegment<byte>(bytes);
                await _sendLock.WaitAsync();
                try
                {
                    await _ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            else
            {
                await QueuePendingResultAsync(message);
            }
        }

        private async Task QueuePendingResultAsync(string message)
        {
            await _queueLock.WaitAsync();
            try
            {
                if (_pendingResults.Count >= MaxPendingResults)
                {
                    _pendingResults.Dequeue();
                }
                _pendingResults.Enqueue(new PendingResult
                {
                    Payload = message,
                    Timestamp = DateTime.UtcNow
                });
            }
            finally
            {
                _queueLock.Release();
            }
        }

        private async Task FlushPendingResultsAsync(CancellationToken ct)
        {
            while (_pendingResults.Count > 0 && _ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                PendingResult result;
                await _queueLock.WaitAsync(ct);
                try
                {
                    if (_pendingResults.Count == 0) break;
                    result = _pendingResults.Dequeue();
                }
                finally
                {
                    _queueLock.Release();
                }

                if (DateTime.UtcNow - result.Timestamp < ResultTtl)
                {
                    await SendAsync(result.Payload);
                }
            }
        }

        private static Uri NormalizeWebSocketUri(string uri)
        {
            var url = uri.TrimEnd('/');

            if (!url.StartsWith("ws://") && !url.StartsWith("wss://"))
            {
                if (url.StartsWith("http://"))
                {
                    url = "ws://" + url["http://".Length..];
                }
                else if (url.StartsWith("https://"))
                {
                    url = "wss://" + url["https://".Length..];
                }
                else
                {
                    url = "wss://" + url;
                }
            }

            return new Uri(url);
        }

        public bool IsTokenExpiredOrNearExpiry()
        {
            if (string.IsNullOrEmpty(_authToken)) return true;
            if (!_tokenExpiresAt.HasValue) return true;
            return DateTime.UtcNow >= _tokenExpiresAt.Value.AddSeconds(-60);
        }

        public void Dispose()
        {
            _ws?.Dispose();
            _cts?.Cancel();
            _cts?.Dispose();
            _queueLock?.Dispose();
            _sendLock?.Dispose();
            _httpClient?.Dispose();
        }

        private class PendingResult
        {
            public string Payload { get; set; } = "";
            public DateTime Timestamp { get; set; }
        }
    }
}
