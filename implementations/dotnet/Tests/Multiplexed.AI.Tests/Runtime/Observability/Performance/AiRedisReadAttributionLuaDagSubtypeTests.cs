using Multiplexed.AI.Runtime.Observability.Performance;
using System;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Observability.Performance
{
    [Collection("PERF1 Redis attribution diagnostics")]
    public sealed class AiRedisReadAttributionLuaDagSubtypeTests : IDisposable
    {
        private readonly string? previousEnabled;
        private readonly string? previousScope;

        public AiRedisReadAttributionLuaDagSubtypeTests()
        {
            previousEnabled = Environment.GetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.EnabledEnvironmentVariable);
            previousScope = Environment.GetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.ScopeEnvironmentVariable);

            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.EnabledEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.ScopeEnvironmentVariable,
                null);
            AiRedisReadAttributionDiagnostics.ResetCurrentProcess();
        }

        [Theory]
        [InlineData(AiRedisReadAttributionOperations.LuaDagClaim)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagClaimBatch)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagClaimSpecific)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagComplete)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagPark)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagResumeExternalWait)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagFail)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagRecover)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagRecoverRunningForRecovery)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagFinalize)]
        [InlineData(AiRedisReadAttributionOperations.LuaDagRetention)]
        public void RecordInvocation_Should_Record_Bounded_Lua_Dag_Subtype(
            string operationName)
        {
            using var scope = EnableScope();

            AiRedisReadAttributionDiagnostics.RecordInvocation(operationName);

            var operation = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            Assert.Equal(operationName, operation.Operation);
            Assert.Equal("LUA", operation.Command);
            Assert.Equal(1L, operation.Calls);
            Assert.Equal(0L, operation.ResponsePayloadBytes);
        }

        public void Dispose()
        {
            var currentScope = Environment.GetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.ScopeEnvironmentVariable);
            AiRedisReadAttributionDiagnostics.EndScope(currentScope);

            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.EnabledEnvironmentVariable,
                previousEnabled);
            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.ScopeEnvironmentVariable,
                previousScope);
            AiRedisReadAttributionDiagnostics.ResetCurrentProcess();
        }

        private static ScopeLease EnableScope()
        {
            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.EnabledEnvironmentVariable,
                "1");

            var scope = AiRedisReadAttributionDiagnostics.BeginScope();
            Assert.False(string.IsNullOrWhiteSpace(scope));
            return new ScopeLease(scope!);
        }

        private sealed class ScopeLease : IDisposable
        {
            private readonly string scope;

            public ScopeLease(string scope)
            {
                this.scope = scope;
            }

            public void Dispose()
            {
                AiRedisReadAttributionDiagnostics.EndScope(scope);
            }
        }
    }
}
