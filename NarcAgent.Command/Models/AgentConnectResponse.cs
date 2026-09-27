using System.Text.Json.Serialization;

namespace NarcAgent.Command.Models
{
    public class AgentConnectResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("ip")]
        public string? Ip { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        // A API manda "refresh_token" (snake_case). Sem o atributo, nem case-insensitive
        // nem a naming policy camelCase batem com isso ("refreshToken" != "refresh_token"),
        // entao essa propriedade sempre vinha null.
        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }
    }
}