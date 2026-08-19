using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Steps;
using StackExchange.Redis;

namespace Multiplexed.AI.Stores.Cache.Redis.Helpers
{
    /// <summary>
    /// Provides shared helper methods for the Redis DAG execution store.
    /// </summary>
    public sealed class RedisDagStoreHelper
    {
        private readonly IRedisDagStoreServices _services;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisDagStoreHelper"/> class.
        /// </summary>
        /// <param name="services">The Redis DAG store services.</param>
        public RedisDagStoreHelper(
            IRedisDagStoreServices services)
        {
            ArgumentNullException.ThrowIfNull(services);

            _services = services;
        }

        /// <summary>
        /// Determines whether a step status is terminal.
        /// </summary>
        /// <param name="status">The step execution status.</param>
        /// <returns><c>true</c> when the status is terminal; otherwise <c>false</c>.</returns>
        public static bool IsTerminal(
            AiStepExecutionStatus status)
        {
            return status == AiStepExecutionStatus.Completed ||
                   status == AiStepExecutionStatus.Failed;
        }

        /// <summary>
        /// Determines whether a step status is non-terminal.
        /// </summary>
        /// <param name="status">The step execution status.</param>
        /// <returns><c>true</c> when the status is non-terminal; otherwise <c>false</c>.</returns>
        public static bool IsNonTerminal(
            AiStepExecutionStatus status)
        {
            return status == AiStepExecutionStatus.None ||
                   status == AiStepExecutionStatus.Ready ||
                   status == AiStepExecutionStatus.Running ||
                   status == AiStepExecutionStatus.WaitingForRetry ||
                   status == AiStepExecutionStatus.WaitingForExternal;
        }

        /// <summary>
        /// Builds the Redis key prefix for all step keys belonging to one execution.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <returns>The Redis step key prefix.</returns>
        public string GetStepKeyPrefix(
            string executionId)
        {
            return _services.KeyBuilder.GetDagStepKeyPrefix(executionId);
        }

        /// <summary>
        /// Builds the Redis key used to persist the full execution state blob.
        ///
        /// IMPORTANT:
        /// - This key preserves global state bags such as Data and Metadata
        /// - Step keys remain the authoritative distributed DAG truth for step lifecycle
        /// - The state blob complements step storage and must not replace it
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <returns>The Redis state blob key.</returns>
        public string GetStateBlobKey(
            string executionId)
        {
            return _services.KeyBuilder.GetExecutionRecordKey(executionId) + ":state";
        }

        /// <summary>
        /// Returns the current UTC time as a Unix timestamp in milliseconds.
        /// </summary>
        /// <returns>The current Unix timestamp in milliseconds.</returns>
        public static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Retrieves a connected Redis server for script loading.
        /// </summary>
        /// <returns>The connected Redis server.</returns>
        public IServer GetServer()
        {
            return _services.Multiplexer.GetEndPoints()
                .Select(e => _services.Multiplexer.GetServer(e))
                .First(s => s.IsConnected);
        }

        /// <summary>
        /// Loads a prepared Lua script onto Redis and returns its SHA-bound instance.
        /// </summary>
        /// <param name="script">The prepared Lua script.</param>
        /// <returns>The loaded Lua script.</returns>
        public LoadedLuaScript LoadScript(
            LuaScript script)
        {
            var server = GetServer();
            return script.Load(server);
        }

        /// <summary>
        /// Removes stale default hot-state entries for logically completed steps.
        /// </summary>
        /// <param name="state">The reconstructed execution state.</param>
        /// <param name="completedStepNames">The logical completed-step history from the execution record.</param>
        public static void RemoveStaleCompletedNoneSteps(
            AiExecutionState state,
            IReadOnlySet<string> completedStepNames)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(completedStepNames);

            if (state.Steps.Count == 0)
            {
                return;
            }

            var staleStepNames = state.Steps.Values
                .Where(step =>
                    !string.IsNullOrWhiteSpace(step.StepName) &&
                    completedStepNames.Contains(step.StepName) &&
                    step.Status == AiStepExecutionStatus.None &&
                    step.Result is null &&
                    step.CompletedAtUtc is null)
                .Select(step => step.StepName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            foreach (var stepName in staleStepNames)
            {
                state.Steps.Remove(stepName);
            }
        }
    }
}