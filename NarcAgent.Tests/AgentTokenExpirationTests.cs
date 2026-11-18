using System;
using NarcAgent.Command;
using NarcAgent.Core.Services;
using Xunit;

namespace NarcAgent.Tests
{
    public class AgentTokenExpirationTests
    {
        [Fact]
        public void IsTokenExpiredOrNearExpiry_WhenTokenValidFor20Minutes_ReturnsFalse()
        {
            var router = new CommandRouter();
            // Simula um token emitido agora, válido por 20 minutos (mesmo que venha com Kind Local ou Utc)
            var expiresAt = DateTime.UtcNow.AddMinutes(20);
            using var agent = new AgentCore(router, "ws://localhost:8080/ws/agent/test", "test", "test", "fake-token", expiresAt);

            Assert.False(agent.IsTokenExpiredOrNearExpiry());
        }

        [Fact]
        public void IsTokenExpiredOrNearExpiry_WhenTokenExpiresIn3Minutes_ReturnsTrue()
        {
            var router = new CommandRouter();
            var expiresAt = DateTime.UtcNow.AddMinutes(3);
            using var agent = new AgentCore(router, "ws://localhost:8080/ws/agent/test", "test", "test", "fake-token", expiresAt);

            Assert.True(agent.IsTokenExpiredOrNearExpiry());
        }

        [Fact]
        public void IsTokenExpiredOrNearExpiry_WhenTokenNull_ReturnsTrue()
        {
            var router = new CommandRouter();
            using var agent = new AgentCore(router, "ws://localhost:8080/ws/agent/test", "test", "test", null, null);

            Assert.True(agent.IsTokenExpiredOrNearExpiry());
        }

        [Fact]
        public void IsTokenExpiredOrNearExpiry_WhenDateTimeHasLocalKind_HandlesUtcProperly()
        {
            var router = new CommandRouter();
            // Simula DateTime local (ex: horário de Brasília) daqui a 20 minutos
            var localExp = DateTime.Now.AddMinutes(20);
            using var agent = new AgentCore(router, "ws://localhost:8080/ws/agent/test", "test", "test", "fake-token", localExp);

            Assert.False(agent.IsTokenExpiredOrNearExpiry());
        }
    }
}
