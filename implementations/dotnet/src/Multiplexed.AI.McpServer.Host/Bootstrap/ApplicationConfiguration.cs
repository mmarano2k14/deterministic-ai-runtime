using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Configures the HTTP application pipeline.
    /// </summary>
    public static class ApplicationConfiguration
    {
        /// <summary>
        /// Configures application endpoints according to the MCP host mode.
        /// </summary>
        public static void Configure(
            WebApplication app)
        {
            ArgumentNullException.ThrowIfNull(app);

            var hostOptions =
                app.Services
                    .GetRequiredService<IOptions<AiMcpHostOptions>>()
                    .Value;

            app.MapHealthChecks("/health");
            ConfigureMatrixMcpEffectEvidenceEndpoint(app);
            ConfigureMatrixPublicationEnvironmentDiagnosticsEndpoint(app);
            ConfigureMatrixScaleOutDiagnosticsEndpoint(app);
            ConfigureMatrixRecoveryAndJournalEndpoints(app);
            ConfigureMatrixExecutionControlEndpoints(app);

            switch (hostOptions.Mode)
            {
                case AiMcpHostMode.ControlPlaneOnly:
                case AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances:
                case AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances:
                case AiMcpHostMode.ControlPlaneWithGrpcRuntimeInstances:
                    app.UseAuthentication();
                    app.UseWhen(
                        context =>
                            context.Request.Path
                                .StartsWithSegments("/mcp"),
                        branch =>
                        {
                            branch.UseMiddleware<
                                ExecutionContextMiddleware>();
                            branch.UseMiddleware<
                                NamespaceGuardMiddleware>();
                        });
                    app.UseAuthorization();
                    app.MapMcp("/mcp");
                    break;

                case AiMcpHostMode.RuntimeInstanceOnly:
                    ConfigureRuntimeInstanceEndpoints(app);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported MCP host mode '{hostOptions.Mode}'.");
            }
        }


        /// <summary>
        /// Exposes bounded matrix-only diagnostics for durable MCP effect evidence.
        /// It is disabled in every normal host and returns no endpoint, credential or secret header material.
        /// </summary>
        private static void ConfigureMatrixMcpEffectEvidenceEndpoint(WebApplication app)
        {
            var matrix = app.Configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            if (!matrix.Enabled)
            {
                return;
            }

            app.MapGet(
                "/matrix/mcp-effect-evidence/{executionId}/{stepName}",
                async (
                    string executionId,
                    string stepName,
                    AiMcpEffectEvidenceJournal journal,
                    IAiDagExecutionStore dagStore,
                    CancellationToken cancellationToken) =>
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                    ArgumentException.ThrowIfNullOrWhiteSpace(stepName);

                    var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string>());
                    var probe = new AiMcpToolRequest(
                        AiMcpEffectIdentities.RequestSchemaVersion,
                        "matrix-diagnostic",
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        new AiMcpToolInvocationContext(
                            matrix.TenantId,
                            matrix.TenantGroupId,
                            executionId,
                            "matrix-diagnostic",
                            null,
                            stepName,
                            "mcp.tool"),
                        "matrix-effect-probe",
                        "v1",
                        "probe.fail-count",
                        arguments);
                    var effectIdentity = AiMcpEffectIdentities.Create(probe);
                    probe = probe with { Effect = effectIdentity };

                    var scope = new AiMcpEffectEvidenceScope(matrix.TenantId, matrix.TenantGroupId);
                    var effect = await journal.GetAsync(
                        scope,
                        effectIdentity.EffectId,
                        cancellationToken).ConfigureAwait(false);
                    if (effect is null)
                    {
                        return Results.NotFound();
                    }

                    var state = await dagStore.GetStateAsync(executionId, cancellationToken).ConfigureAwait(false);
                    var step = state is not null && state.Steps.TryGetValue(stepName, out var candidate)
                        ? candidate
                        : null;

                    return Results.Ok(new
                    {
                        effectId = effect.Intent.Effect.EffectId,
                        status = effect.Status.ToString(),
                        revision = effect.Revision,
                        attemptRequestId = effect.Attempt?.RequestId,
                        resultIsError = effect.Result?.IsError,
                        uncertaintyReasonCode = effect.Uncertainty?.ReasonCode,
                        retryCount = step?.RetryState?.RetryCount,
                        stepStatus = step?.Status.ToString()
                    });
                });
        }

        /// <summary>
        /// Exposes the exact matrix publication environment selected by the external SDK runner.
        /// The endpoint is diagnostic-only and never creates, mutates, or selects runtime capacity.
        /// </summary>
        private static void ConfigureMatrixPublicationEnvironmentDiagnosticsEndpoint(WebApplication app)
        {
            var matrix = app.Configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            if (!matrix.Enabled)
            {
                return;
            }

            app.MapGet(
                "/matrix/publication-environment/{reference}",
                (string reference, IAiPublicationEnvironmentCatalog catalog) =>
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(reference);
                    var runtime = catalog.Find(reference);
                    if (runtime is null)
                    {
                        return Results.NotFound(new
                        {
                            found = false,
                            reference
                        });
                    }

                    var descriptor =
                        (catalog as IAiPublicationExecutionEnvironmentCatalog)?
                            .FindExecutionDescriptor(reference);

                    return Results.Ok(new
                    {
                        found = true,
                        runtime.Reference,
                        runtime.ExecutionLanguage,
                        runtime.RuntimeVersion,
                        runtime.RuntimeSha256,
                        executionDescriptor = descriptor is null
                            ? null
                            : new
                            {
                                descriptor.OperatingSystem,
                                descriptor.Architecture,
                                artifactKind = descriptor.Artifact.Kind.ToString(),
                                descriptor.Artifact.Digest,
                                descriptor.Artifact.MediaType,
                                isolationTier = descriptor.Requirements.IsolationTier.ToString(),
                                pathProtection = descriptor.Requirements.PathProtection.ToString()
                            }
                    });
                });
        }

        /// <summary>
        /// Exposes bounded matrix-only scale-out readiness and request diagnostics.
        /// The endpoint does not create capacity; it only reports the existing watcher/store authorities.
        /// </summary>
        private static void ConfigureMatrixScaleOutDiagnosticsEndpoint(WebApplication app)
        {
            var matrix = app.Configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            if (!matrix.Enabled)
            {
                return;
            }

            app.MapGet(
                "/matrix/scaleout",
                async (
                    IEnumerable<IHostedService> hostedServices,
                    IAiRuntimeScaleOutRequestStore requestStore,
                    CancellationToken cancellationToken) =>
                {
                    var watcher = hostedServices
                        .OfType<AiRuntimeScaleOutRequestWatcherHostedService>()
                        .SingleOrDefault();

                    var controlPlaneId = watcher?.ResolvedControlPlaneId;
                    IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> requests =
                        Array.Empty<AiRuntimeScaleOutRequestRecord>();
                    if (!string.IsNullOrWhiteSpace(controlPlaneId))
                    {
                        requests = await requestStore
                            .ListAsync(
                                new AiRuntimeScaleOutRequestQuery
                                {
                                    ControlPlaneId = controlPlaneId,
                                    IncludeExpired = true,
                                    MaxResults = 100
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return Results.Ok(new
                    {
                        watcherRegistered = watcher is not null,
                        watcherReady = watcher?.IsReady ?? false,
                        watcherId = watcher?.WatcherId,
                        resolvedControlPlaneId = controlPlaneId,
                        readyAtUtc = watcher?.ReadyAtUtc,
                        requests = requests
                            .OrderBy(request => request.CreatedAtUtc)
                            .Select(request => new
                            {
                                requestId = request.RequestId,
                                sharedRunId = request.SharedRunId,
                                status = request.Status.ToString(),
                                reason = request.Reason,
                                rejectionReason = request.RejectionReason,
                                providerHint = request.ProviderHint,
                                requestedTargetInstanceCount = request.RequestedTargetInstanceCount,
                                createdAtUtc = request.CreatedAtUtc,
                                observedAtUtc = request.ObservedAtUtc,
                                fulfilledAtUtc = request.FulfilledAtUtc,
                                rejectedAtUtc = request.RejectedAtUtc,
                                fulfilledRuntimeInstanceId = request.FulfilledRuntimeInstanceId
                            })
                            .ToArray()
                    });
                });
        }

        /// <summary>
        /// Exposes matrix-only recovery and durable result-acceptance probes through production authorities.
        /// These endpoints are disabled for every normal host.
        /// </summary>
        private static void ConfigureMatrixRecoveryAndJournalEndpoints(WebApplication app)
        {
            var matrix = app.Configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            if (!matrix.Enabled)
            {
                return;
            }

            app.MapPost(
                "/matrix/recovery/{recoveryCase}/{executionId}",
                async (
                    string recoveryCase,
                    string executionId,
                    MatrixRecoverySeedRequest seed,
                    MatrixRecoveryProbe probe,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var result = await probe
                            .RunAsync(recoveryCase, executionId, seed, cancellationToken)
                            .ConfigureAwait(false);
                        return (IResult)Results.Ok(result);
                    }
                    catch (Exception exception)
                    {
                        return (IResult)Results.Json(
                            new
                            {
                                error = "matrix-recovery-probe-failed",
                                exceptionType = exception.GetType().Name,
                                message = exception.Message
                            },
                            statusCode: StatusCodes.Status500InternalServerError);
                    }
                });

            app.MapPost(
                "/matrix/journal-result-acceptance/{acceptanceCase}",
                async (
                    string acceptanceCase,
                    MatrixJournalResultAcceptanceProbe probe,
                    CancellationToken cancellationToken) =>
                {
                    var result = await probe
                        .RunAsync(acceptanceCase, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(result);
                });
        }

        /// <summary>
        /// Exposes matrix-only setup for an execution that is expected to accept human/external input.
        /// The endpoint delegates to the production execution-control service and exists only so the E2E
        /// harness can create the precondition for the public SDK input-submission command.
        /// </summary>
        private static void ConfigureMatrixExecutionControlEndpoints(WebApplication app)
        {
            var matrix = app.Configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            if (!matrix.Enabled)
            {
                return;
            }

            app.MapPost(
                "/matrix/execution-control/{executionId}/wait-for-input",
                async (
                    string executionId,
                    MatrixExecutionControlWaitRequest request,
                    IAiExecutionControlService controlService,
                    CancellationToken cancellationToken) =>
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                    ArgumentNullException.ThrowIfNull(request);
                    ArgumentException.ThrowIfNullOrWhiteSpace(request.WaitingKey);

                    var state = await controlService
                        .MarkWaitingForInputAsync(
                            executionId,
                            request.WaitingKey,
                            request.WaitingStepName,
                            request.Reason,
                            matrix.UserId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return Results.Ok(new
                    {
                        executionId = state.ExecutionId,
                        status = state.Status.ToString(),
                        pendingAction = state.PendingAction.ToString(),
                        waitingKey = state.WaitingKey,
                        waitingStepName = state.WaitingStepName,
                        updatedAtUtc = state.UpdatedAtUtc
                    });
                });
        }

        private sealed record MatrixExecutionControlWaitRequest(
            string WaitingKey,
            string? WaitingStepName,
            string? Reason);

        /// <summary>
        /// Configures either a single runtime endpoint or the stable Runtime Pool endpoint.
        /// </summary>
        private static void ConfigureRuntimeInstanceEndpoints(
            WebApplication app)
        {
            var kubernetesPoolOptions =
                app.Configuration
                    .GetSection("AiKubernetesRuntimePoolInPod")
                    .Get<AiKubernetesRuntimePoolInPodOptions>();

            if (kubernetesPoolOptions?.Enabled == true)
            {
                ConfigureRuntimePoolEndpoints(
                    app,
                    kubernetesPoolOptions.TransportName);
                return;
            }

            var processPoolOptions =
                app.Configuration
                    .GetSection("AiRuntimeProcessPool")
                    .Get<AiRuntimeProcessPoolOptions>();

            if (processPoolOptions?.Enabled == true)
            {
                var processRuntimeOptions =
                    app.Configuration
                        .GetSection("AiRuntimeProcessPoolRuntimeInstance")
                        .Get<AiRuntimeProcessPoolRuntimeInstanceOptions>()
                    ?? throw new InvalidOperationException(
                        "AiRuntimeProcessPoolRuntimeInstance configuration is required when AiRuntimeProcessPool is enabled.");

                ConfigureRuntimePoolEndpoints(
                    app,
                    processRuntimeOptions.TransportName);
                return;
            }

            var disableRuntimeCommandEndpoint =
                app.Configuration.GetValue<bool>(
                    "Tests:DisableRuntimeCommandEndpoint");

            if (disableRuntimeCommandEndpoint)
            {
                return;
            }

            var transportName =
                ResolveRuntimeCommandTransportName(
                    app.Configuration);

            switch (transportName)
            {
                case "http":
                    app.MapAiRuntimeInstanceHttpCommandEndpoint();
                    break;

                case "grpc":
                    app.MapAiRuntimeInstanceGrpcCommandService();
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported runtime command transport '{transportName}'.");
            }
        }

        /// <summary>
        /// Maps the stable exact router and Runtime Pool readiness endpoint.
        /// </summary>
        private static void ConfigureRuntimePoolEndpoints(
            WebApplication app,
            string transportName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transportName);

            switch (transportName
                .Trim()
                .ToLowerInvariant())
            {
                case "http":
                    app.MapAiRuntimePoolHttpCommandEndpoint();
                    break;

                case "grpc":
                    app.MapAiRuntimePoolGrpcCommandService();
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Runtime Pool transport '{transportName}'.");
            }

            app.MapGet(
                "/runtime-pool/readiness",
                async (
                    IAiRuntimeProcessPoolManager manager,
                    CancellationToken cancellationToken) =>
                {
                    var snapshot =
                        await manager
                            .GetSnapshotAsync(cancellationToken)
                            .ConfigureAwait(false);

                    var ready =
                        snapshot.Status
                            == AiRuntimeProcessPoolManagerStatus.Running
                        && !snapshot.IsBelowMinimumCapacity
                        && snapshot.Children.Count
                            >= snapshot.MinimumProcessCount
                        && snapshot.Children.All(
                            child =>
                                child.Status
                                == AiRuntimeProcessPoolChildStatus.Running);

                    return ready
                        ? Results.Ok(
                            new
                            {
                                ready = true,
                                snapshot.PoolId,
                                snapshot.HostId,
                                RuntimeInstanceIds =
                                    snapshot.Children
                                        .Select(
                                            child =>
                                                child.RuntimeInstanceId)
                                        .ToArray()
                            })
                        : Results.Json(
                            new
                            {
                                ready = false,
                                snapshot.PoolId,
                                snapshot.HostId,
                                Status =
                                    snapshot.Status.ToString(),
                                snapshot.IsBelowMinimumCapacity,
                                ChildCount =
                                    snapshot.Children.Count
                            },
                            statusCode:
                                StatusCodes
                                    .Status503ServiceUnavailable);
                });
        }

        /// <summary>
        /// Resolves the single-runtime command transport.
        /// </summary>
        private static string ResolveRuntimeCommandTransportName(
            IConfiguration configuration)
        {
            var transportName =
                configuration[
                    "AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"];

            if (!string.IsNullOrWhiteSpace(transportName))
            {
                return transportName
                    .Trim()
                    .ToLowerInvariant();
            }

            var providerName =
                configuration[
                    "AiRuntimeInstanceRegistration:ProviderName"];

            if (!string.IsNullOrWhiteSpace(providerName))
            {
                return providerName
                    .Trim()
                    .ToLowerInvariant();
            }

            var metadataProviderName =
                configuration[
                    "AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"];

            return string.IsNullOrWhiteSpace(metadataProviderName)
                ? "http"
                : metadataProviderName
                    .Trim()
                    .ToLowerInvariant();
        }
    }
}
