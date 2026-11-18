using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NarcAgent.Proxy.Models
{
    public class FlexibleBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.Number:
                    return reader.GetInt32() != 0;
                case JsonTokenType.String:
                    var s = reader.GetString()?.Trim().ToLowerInvariant();
                    return s == "true" || s == "1" || s == "yes" || s == "t";
                default:
                    return false;
            }
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }
    }

    public class DatabaseProxy
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("agent_id")]
        public string AgentId { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonPropertyName("target_host")]
        public string TargetHost { get; set; } = "127.0.0.1";

        [JsonPropertyName("target_port")]
        public int TargetPort { get; set; } = 80;

        [JsonPropertyName("enabled")]
        [JsonConverter(typeof(FlexibleBoolConverter))]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("force_ssl")]
        [JsonConverter(typeof(FlexibleBoolConverter))]
        public bool ForceSsl { get; set; } = false;

        [JsonPropertyName("ssl_issued")]
        [JsonConverter(typeof(FlexibleBoolConverter))]
        public bool SslIssued { get; set; } = false;

        [JsonPropertyName("is_custom")]
        [JsonConverter(typeof(FlexibleBoolConverter))]
        public bool IsCustom { get; set; } = false;

        [JsonPropertyName("raw_config")]
        public string? RawConfig { get; set; }

        [JsonPropertyName("websocket_enabled")]
        [JsonConverter(typeof(FlexibleBoolConverter))]
        public bool WebsocketEnabled { get; set; } = true;
    }

    public class ReconciliationResult
    {
        public int TotalDbProxies { get; set; }
        public int VerifiedConfigs { get; set; }
        public int SslInjectedConfigs { get; set; }
        public int RemovedOrphans { get; set; }
        public System.Collections.Generic.List<string> RemovedFiles { get; set; } = new();
        public System.Collections.Generic.List<string> Errors { get; set; } = new();
        public bool Success { get; set; }
    }
}
