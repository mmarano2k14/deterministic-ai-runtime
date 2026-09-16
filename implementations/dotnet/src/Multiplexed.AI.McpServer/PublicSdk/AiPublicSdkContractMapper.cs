using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;

namespace Multiplexed.AI.McpServer.PublicSdk
{
    /// <summary>Explicitly maps public wire contracts to server-owned runtime contracts and back.</summary>
    public static class AiPublicSdkContractMapper
    {
        public static AiPipelinePublicationUpload ToInternal(AiSdkPipelinePublicationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            RequireSchema(request.SchemaVersion, 1, nameof(AiSdkPipelinePublicationRequest));
            return new AiPipelinePublicationUpload(
                ToInternal(request.Definition),
                request.Functions.Select(ToInternal).ToArray());
        }

        public static AiSdkPipelinePublicationResponse ToPublic(AiPipelinePublication publication)
        {
            ArgumentNullException.ThrowIfNull(publication);
            return new AiSdkPipelinePublicationResponse
            {
                PublicationRef = publication.PublicationRef,
                PublicationSha256 = publication.PublicationSha256,
                PipelineName = publication.Manifest.PipelineName,
                PipelineVersion = publication.Manifest.PipelineVersion
            };
        }

        public static AiPipelineDefinition ToInternal(AiSdkPipelineDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            RequireSchema(definition.SchemaVersion, 1, nameof(AiSdkPipelineDefinition));
            return new AiPipelineDefinition
            {
                Name = Required(definition.Name, "definition.name"),
                Version = definition.Version,
                ExecutionLanguage = definition.ExecutionLanguage,
                ExecutionMode = definition.ExecutionMode switch
                {
                    AiSdkExecutionMode.Sequential => AiExecutionMode.Sequential,
                    AiSdkExecutionMode.Dag => AiExecutionMode.Dag,
                    _ => throw new NotSupportedException($"Execution mode '{definition.ExecutionMode}' is not supported.")
                },
                Steps = definition.Steps.Select(ToInternal).ToArray(),
                Config = ToInternal(definition.Config)
            };
        }

        public static AiSdkExecutionStatus ToPublic(AiExecutionStatus status) => status switch
        {
            AiExecutionStatus.Pending => AiSdkExecutionStatus.Pending,
            AiExecutionStatus.Running => AiSdkExecutionStatus.Running,
            AiExecutionStatus.Waiting => AiSdkExecutionStatus.Waiting,
            AiExecutionStatus.Completed => AiSdkExecutionStatus.Completed,
            AiExecutionStatus.Failed => AiSdkExecutionStatus.Failed,
            AiExecutionStatus.Cancelled => AiSdkExecutionStatus.Cancelled,
            _ => throw new NotSupportedException($"Execution status '{status}' is not supported.")
        };

        public static AiSdkExecutionStepStatus ToPublic(AiStepExecutionStatus status) => status switch
        {
            AiStepExecutionStatus.None => AiSdkExecutionStepStatus.Pending,
            AiStepExecutionStatus.Ready => AiSdkExecutionStepStatus.Ready,
            AiStepExecutionStatus.Running => AiSdkExecutionStepStatus.Running,
            AiStepExecutionStatus.WaitingForRetry => AiSdkExecutionStepStatus.WaitingForRetry,
            AiStepExecutionStatus.WaitingForExternal => AiSdkExecutionStepStatus.WaitingForExternal,
            AiStepExecutionStatus.Completed => AiSdkExecutionStepStatus.Completed,
            AiStepExecutionStatus.Failed => AiSdkExecutionStepStatus.Failed,
            _ => throw new NotSupportedException($"Step status '{status}' is not supported.")
        };

        private static AiPipelineStepDefinition ToInternal(AiSdkPipelineStepDefinition step) => new()
        {
            Name = Required(step.Name, "step.name"),
            StepKey = Required(step.StepKey, "step.stepKey"),
            ExecutionLanguage = step.ExecutionLanguage,
            Invocation = step.Invocation is null ? null : new AiInvocationDefinition
            {
                Kind = step.Invocation.Kind switch
                {
                    AiSdkInvocationKind.Native => AiInvocationKind.Native,
                    AiSdkInvocationKind.Custom => AiInvocationKind.Custom,
                    AiSdkInvocationKind.Mcp => AiInvocationKind.Mcp,
                    _ => throw new NotSupportedException($"Invocation kind '{step.Invocation.Kind}' is not supported.")
                },
                ImplementationRef = step.Invocation.ImplementationRef,
                ConnectionRef = step.Invocation.ConnectionRef,
                Tool = step.Invocation.Tool
            },
            Order = step.Order,
            DependsOn = step.DependsOn.ToArray(),
            Input = ToInternal(step.Input),
            Config = ToInternal(step.Config),
            Execution = step.Execution is null ? null : new AiPipelineStepExecutionDefinition
            {
                MaxRetries = step.Execution.MaxRetries,
                RetryDelayMs = step.Execution.RetryDelayMs
            }
        };

        private static AiPublicationFunctionUpload ToInternal(AiSdkPublicationFunctionUpload function) => new(
            new AiPublicationCallSite(
                function.Site.Kind switch
                {
                    AiSdkPublicationFunctionKind.Step => AiPublicationFunctionKind.Step,
                    AiSdkPublicationFunctionKind.ConcurrencyPolicy => AiPublicationFunctionKind.ConcurrencyPolicy,
                    AiSdkPublicationFunctionKind.RetryPolicy => AiPublicationFunctionKind.RetryPolicy,
                    AiSdkPublicationFunctionKind.DelegationPolicy => AiPublicationFunctionKind.DelegationPolicy,
                    _ => throw new NotSupportedException($"Publication function kind '{function.Site.Kind}' is not supported.")
                },
                function.Site.StepName,
                function.Site.PolicyIndex)
            { DefinitionPath = function.Site.DefinitionPath },
            Required(function.EnvironmentRef, "function.environmentRef"),
            Required(function.EntryPointPath, "function.entryPointPath"),
            Required(function.EntryPointSymbol, "function.entryPointSymbol"),
            function.Sources.Select(ToInternal).ToArray(),
            function.Dependencies.Select(ToInternal).ToArray());

        private static AiPublicationFileUpload ToInternal(AiSdkPublicationFileUpload file) =>
            new(Required(file.Path, "file.path"), Convert.FromBase64String(Required(file.ContentBase64, "file.contentBase64")));

        private static AiPublicationDependencyUpload ToInternal(AiSdkPublicationDependencyUpload dependency) =>
            new(Required(dependency.Name, "dependency.name"), Required(dependency.Version, "dependency.version"),
                dependency.Files.Select(ToInternal).ToArray())
            {
                Package = dependency.Package is null ? null : new AiPublicationDependencyPackage(
                    dependency.Package.SchemaVersion,
                    dependency.Package.Kind switch
                    {
                        AiSdkPublicationDependencyPackageKind.PythonWheelBundle => AiPublicationDependencyPackageKind.PythonWheelBundle,
                        AiSdkPublicationDependencyPackageKind.NodeLockedBundle => AiPublicationDependencyPackageKind.NodeLockedBundle,
                        AiSdkPublicationDependencyPackageKind.DotNetAssemblyClosure => AiPublicationDependencyPackageKind.DotNetAssemblyClosure,
                        _ => throw new NotSupportedException($"Dependency package kind '{dependency.Package.Kind}' is not supported.")
                    },
                    Required(dependency.Package.ManifestPath, "dependency.package.manifestPath"))
            };

        private static IReadOnlyDictionary<string, object?> ToInternal(IReadOnlyDictionary<string, JsonElement> values) =>
            values.ToDictionary(pair => pair.Key, pair => ToInternal(pair.Value), StringComparer.Ordinal);

        private static object? ToInternal(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Array => value.EnumerateArray().Select(ToInternal).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                property => property.Name, property => ToInternal(property.Value), StringComparer.Ordinal),
            _ => throw new NotSupportedException($"JSON value kind '{value.ValueKind}' is not supported.")
        };

        private static string Required(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"'{name}' is required.", name);
            return value;
        }

        private static void RequireSchema(int actual, int expected, string contract)
        {
            if (actual != expected) throw new NotSupportedException($"{contract} schema version '{actual}' is not supported; expected '{expected}'.");
        }
    }
}
