using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace NarcAgent.Core.Services
{
    /// <summary>
    /// Persiste o token de autenticação do agente em arquivo JSON (agent.nortlin)
    /// para que o agente possa reconectar após restart sem precisar de reaprovação.
    /// </summary>
    public class TokenStorage
    {
        private readonly string _filePath;

        public TokenStorage(string? filePath = null)
        {
            _filePath = filePath ?? "agent.nythric";
        }

        public record StoredToken(string Token, DateTime? ExpiresAt, string? RefreshToken);

        public async Task SaveAsync(string token, DateTime? expiresAt, string? refreshToken = null)
        {
            // Preserve existing refresh token if not provided
            if (refreshToken == null && File.Exists(_filePath))
            {
                try
                {
                    var existingJson = await File.ReadAllTextAsync(_filePath);
                    var existing = JsonSerializer.Deserialize<StoredToken>(existingJson);
                    refreshToken = existing?.RefreshToken;
                }
                catch { }
            }

            var data = new StoredToken(token, expiresAt, refreshToken);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json);
        }

        public async Task<StoredToken?> LoadAsync()
        {
            if (!File.Exists(_filePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(_filePath);
                return JsonSerializer.Deserialize<StoredToken>(json);
            }
            catch
            {
                return null;
            }
        }

        public void Delete()
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
    }
}
