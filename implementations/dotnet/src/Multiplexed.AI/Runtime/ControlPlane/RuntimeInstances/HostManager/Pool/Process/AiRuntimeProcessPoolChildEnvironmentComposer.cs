using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Composes the durable child-process environment shared by ProcessPool and KubernetesPool
    /// Runtime Pool hosts.
    /// </summary>
    public static class AiRuntimeProcessPoolChildEnvironmentComposer
    {
        /// <summary>
        /// Adds the shared stores, replay, ledger, payload, and tracing settings required by a
        /// durable RuntimeInstanceOnly child.
        /// </summary>
        public static void AddDurableRuntimeEnvironment(
            IDictionary<string, string> destination,
            string? redisConnectionString,
            string? mongoConnectionString,
            string? mongoDatabaseName,
            string? openAiApiKey)
        {
            ArgumentNullException.ThrowIfNull(destination);

            AddWhenPresent(
                destination,
                "ConnectionStrings__Redis",
                redisConnectionString);
            AddWhenPresent(
                destination,
                "ConnectionStrings__Mongo",
                mongoConnectionString);
            AddWhenPresent(
                destination,
                "Mongo__DatabaseName",
                mongoDatabaseName);
            AddWhenPresent(
                destination,
                "OpenAI__ApiKey",
                openAiApiKey);

            destination["AiEngine__Snapshots__Enabled"] = "true";
            destination["AiEngine__Snapshots__Mongo__Enabled"] = "true";
            AddWhenPresent(
                destination,
                "AiEngine__Snapshots__Mongo__ConnectionString",
                mongoConnectionString);
            AddWhenPresent(
                destination,
                "AiEngine__Snapshots__Mongo__DatabaseName",
                mongoDatabaseName);

            destination["AiPayloadStore__Enabled"] = "true";
            destination["AiPayloadStore__Provider"] = "mongo-redis";
            destination["AiPayloadStore__RequireReplaySafePayloads"] = "true";

            destination["AiEngine__PayloadStore__Enabled"] = "true";
            destination["AiEngine__PayloadStore__Provider"] = "mongo-redis";
            destination["AiEngine__PayloadStore__RequireReplaySafePayloads"] = "true";

            destination["AiEngine__Payloads__Enabled"] = "true";
            destination["AiEngine__Payloads__Provider"] = "mongo-redis";
            destination["AiEngine__Payloads__RequireReplaySafePayloads"] = "true";

            destination["AiDecisionLedger__Provider"] = "mongo";
            destination["AiObservability__Ledger__Provider"] = "mongo";

            destination["AiExecutionReplay__MetadataStore__Provider"] = "mongo";
            destination[
                "AiExecutionReplay__MetadataStore__Mongo__CollectionName"] =
                "ai_execution_replay_metadata";

            destination["AiEngine__Observability__EnableTracing"] = "true";
            destination[
                "AiEngine__Observability__EnableInMemoryRecording"] = "true";
            destination["AiEngine__Observability__Tracing__Mode"] = "Mongo";
            destination[
                "AiEngine__Observability__Tracing__MongoCollectionName"] =
                "ai_runtime_traces";
        }

        /// <summary>
        /// Adds topology metadata without duplicating the common Runtime Pool child composition.
        /// </summary>
        public static void AddHostMetadata(
            IDictionary<string, string> destination,
            string hostProvider,
            string hostCreationMode,
            string hostType,
            string deployment,
            string transportEndpointScope)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentException.ThrowIfNullOrWhiteSpace(hostProvider);
            ArgumentException.ThrowIfNullOrWhiteSpace(hostCreationMode);
            ArgumentException.ThrowIfNullOrWhiteSpace(hostType);
            ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
            ArgumentException.ThrowIfNullOrWhiteSpace(transportEndpointScope);

            destination[
                "AiRuntimeInstanceRegistration__Metadata__host.provider"] =
                hostProvider;
            destination[
                "AiRuntimeInstanceRegistration__Metadata__host.creation.mode"] =
                hostCreationMode;
            destination[
                "AiRuntimeInstanceRegistration__Metadata__hostType"] =
                hostType;
            destination[
                "AiRuntimeInstanceRegistration__Metadata__deployment"] =
                deployment;
            destination[
                "AiRuntimeInstanceRegistration__Metadata__transport.endpoint.scope"] =
                transportEndpointScope;
        }

        /// <summary>
        /// Adds one non-empty child configuration value.
        /// </summary>
        public static void AddWhenPresent(
            IDictionary<string, string> destination,
            string key,
            string? value)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!string.IsNullOrWhiteSpace(value))
            {
                destination[key] = value;
            }
        }
    }
}
