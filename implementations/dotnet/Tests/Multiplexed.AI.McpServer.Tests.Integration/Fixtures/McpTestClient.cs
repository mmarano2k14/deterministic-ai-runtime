using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.AI.McpServer.Models.Responses;
using Multiplexed.AI.McpServer.Tools;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures
{
    public sealed class McpTestClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly HttpClient httpClient;

        public McpTestClient(HttpClient httpClient)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public Task<string> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            return PostJsonRpcRawAsync(
                ToJsonRpcPayload("tools/list"),
                cancellationToken);
        }

        public Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiSharedRuntimeControllerResult>(
                "run.submit_run",
                request,
                cancellationToken);
        }

        public Task<IReadOnlyList<AiSharedRuntimeControllerResult>> SubmitManyRunsAsync(
            AiSharedRuntimeControllerRequest request,
            int count,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<IReadOnlyList<AiSharedRuntimeControllerResult>>(
                "run.submit_many_runs",
                new
                {
                    request,
                    count
                },
                cancellationToken);
        }

        public Task<AiSharedRuntimeControllerResult> ListSharedRunsAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiSharedRuntimeControllerResult>(
                "run.list_shared",
                request,
                cancellationToken);
        }

        public Task<AiSharedRuntimeControllerResult> GetSharedRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiSharedRuntimeControllerResult>(
                "run.get_shared",
                request,
                cancellationToken);
        }

        public Task<AiSharedRuntimeControllerResult> CancelSharedRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiSharedRuntimeControllerResult>(
                "run.cancel_shared",
                request,
                cancellationToken);
        }

        public Task<AiSharedQueuePumpResult> DrainQueueAsync(
            AiSharedQueuePumpRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiSharedQueuePumpResult>(
                "queue.drain",
                request,
                cancellationToken);
        }

        public Task<IReadOnlyList<AiSharedQueueItem>> ListSharedQueueAsync(
            bool includeTerminal = true,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<IReadOnlyList<AiSharedQueueItem>>(
                "shared_queue.list",
                new
                {
                    includeTerminal
                },
                cancellationToken);
        }

        public Task<SharedQueueStatusResult> GetSharedQueueStatusAsync(
            bool includeTerminal = true,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<SharedQueueStatusResult>(
                "shared_queue.status",
                new
                {
                    includeTerminal
                },
                cancellationToken);
        }

        public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListRuntimeInstancesAsync(
            bool includeStopped = false,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<IReadOnlyList<AiRuntimeInstanceSnapshot>>(
                "instance.list",
                new
                {
                    includeStopped
                },
                cancellationToken);
        }

        public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListActiveRuntimeInstancesAsync(
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<IReadOnlyList<AiRuntimeInstanceSnapshot>>(
                "instance.active",
                new { },
                cancellationToken);
        }

        public Task<AiRuntimeInstanceSnapshot?> GetRuntimeInstanceStatusAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiRuntimeInstanceSnapshot?>(
                "instance.status",
                new
                {
                    runtimeInstanceId
                },
                cancellationToken);
        }

        public Task<AiRuntimeQueueControlPlaneResult> GetRuntimeQueueStatusAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiRuntimeQueueControlPlaneResult>(
                "runtime_queue.status",
                request,
                cancellationToken);
        }

        public Task<AiRuntimeQueueControlPlaneResult> GetRuntimeQueueRunStatusAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiRuntimeQueueControlPlaneResult>(
                "runtime_queue.run_status",
                request,
                cancellationToken);
        }

        public Task<AiRuntimeQueueControlPlaneResult> PauseRuntimeQueueAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiRuntimeQueueControlPlaneResult>(
                "runtime_queue.pause",
                request,
                cancellationToken);
        }

        public Task<AiRuntimeQueueControlPlaneResult> ResumeRuntimeQueueAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiRuntimeQueueControlPlaneResult>(
                "runtime_queue.resume",
                request,
                cancellationToken);
        }

        public Task<AiRuntimeQueueControlPlaneResult> CancelRuntimeQueueRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiRuntimeQueueControlPlaneResult>(
                "runtime_queue.cancel_run",
                request,
                cancellationToken);
        }

        public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRuntimeRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiRuntimeQueueControlPlaneResult>(
                "runtime_queue.cancel_queued_run",
                request,
                cancellationToken);
        }

        public Task<AiExecutionControlPlaneResult> PauseExecutionAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiExecutionControlPlaneResult>(
                "control.pause",
                request,
                cancellationToken);
        }

        public Task<AiExecutionControlPlaneResult> ResumeExecutionAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiExecutionControlPlaneResult>(
                "control.resume",
                request,
                cancellationToken);
        }

        public Task<AiExecutionControlPlaneResult> CancelExecutionAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiExecutionControlPlaneResult>(
                "control.cancel",
                request,
                cancellationToken);
        }

        public Task<AiExecutionControlPlaneResult> GetExecutionStatusAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiExecutionControlPlaneResult>(
                "control.status",
                request,
                cancellationToken);
        }

        public Task<AiReplayControlResult> ReplayExecutionAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiReplayControlResult>(
                "replay.execution",
                request,
                cancellationToken);
        }

        public Task<AiReplayControlResult> AuditExecutionAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiReplayControlResult>(
                "replay.audit",
                request,
                cancellationToken);
        }

        public Task<AiReplayControlResult> GetReplayReportAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiReplayControlResult>(
                "replay.report",
                request,
                cancellationToken);
        }

        public Task<AiReplayControlResult> GetReplayLedgerAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiReplayControlResult>(
                "observability.ledger",
                request,
                cancellationToken);
        }

        public Task<AiReplayControlResult> GetReplayTraceAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<AiReplayControlResult>(
                "observability.trace",
                request,
                cancellationToken);
        }

        public Task<IReadOnlyList<AiDecisionLedgerEntry>> GetLedgerByExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<IReadOnlyList<AiDecisionLedgerEntry>>(
                "observability.ledger.get_by_execution",
                new
                {
                    executionId
                },
                cancellationToken);
        }

        public Task<IReadOnlyList<AiDecisionLedgerEntry>> QueryLedgerAsync(
            AiDecisionLedgerQuery query,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<IReadOnlyList<AiDecisionLedgerEntry>>(
                "observability.ledger.query",
                query,
                cancellationToken);
        }

        public Task<IReadOnlyList<AiTraceEvent>> GetTraceByExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<IReadOnlyList<AiTraceEvent>>(
                "observability.trace.get_by_execution",
                new
                {
                    executionId
                },
                cancellationToken);
        }

        public Task<string> GetMetricsStatusAsync(
            CancellationToken cancellationToken = default)
        {
            return CallToolAsync<string>(
                "observability.metrics.status",
                new { },
                cancellationToken);
        }



        private async Task<T> CallToolAsync<T>(
            string toolName,
            object arguments,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
            ArgumentNullException.ThrowIfNull(arguments);

            var payload = ToJsonRpcPayload(
                "tools/call",
                new
                {
                    name = toolName,
                    arguments = ToToolArguments(arguments)
                });

            var raw = await PostJsonRpcRawAsync(
                payload,
                cancellationToken);

            return DeserializeToolResult<T>(raw);
        }

        private static object ToToolArguments(
            object arguments)
        {
            return arguments switch
            {
                AiSharedRuntimeControllerRequest request => new { request },
                AiSharedQueuePumpRequest request => new { request },
                AiRuntimeQueueControlPlaneRequest request => new { request },
                AiExecutionControlPlaneRequest request => new { request },
                AiReplayControlRequest request => new { request },
                AiDecisionLedgerQuery query => new { query },
                _ => arguments
            };
        }

        private async Task<string> PostJsonRpcRawAsync(
            object payload,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };

            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");

            var response = await httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            response.EnsureSuccessStatusCode();

            return content;
        }

        private static object ToJsonRpcPayload(
            string method,
            object? @params = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(method);

            return new
            {
                jsonrpc = "2.0",
                id = Guid.NewGuid().ToString("N"),
                method,
                @params
            };
        }

        private static T DeserializeToolResult<T>(
            string raw)
        {
            var dataJson = ExtractSseDataJson(raw);

            using var rpcDocument = JsonDocument.Parse(dataJson);

            if (rpcDocument.RootElement.TryGetProperty("error", out var rpcError))
            {
                throw new InvalidOperationException(
                    $"MCP JSON-RPC error: {rpcError}");
            }

            var result = rpcDocument.RootElement.GetProperty("result");
            var text = ExtractFirstContentText(result);

            if (result.TryGetProperty("isError", out var isErrorElement) &&
                isErrorElement.ValueKind == JsonValueKind.True)
            {
                throw new InvalidOperationException(
                    $"MCP tool returned isError=true. Text: {text}");
            }

            if (typeof(T) == typeof(string))
            {
                return (T)(object)text;
            }

            return JsonSerializer.Deserialize<T>(text, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Unable to deserialize MCP tool result to '{typeof(T).Name}'.");
        }

        private static string ExtractFirstContentText(
            JsonElement result)
        {
            var content = result.GetProperty("content");

            if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    "MCP tool result does not contain content.");
            }

            var firstContent = content[0];

            if (!firstContent.TryGetProperty("text", out var textElement))
            {
                throw new InvalidOperationException(
                    "MCP tool result content does not contain text.");
            }

            return textElement.GetString()
                ?? throw new InvalidOperationException(
                    "MCP tool result text is empty.");
        }

        private static string ExtractSseDataJson(
            string raw)
        {
            if (!raw.Contains("data:", StringComparison.OrdinalIgnoreCase))
            {
                return raw.Trim();
            }

            var lines = raw.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var dataLine = lines.FirstOrDefault(line =>
                line.StartsWith("data:", StringComparison.OrdinalIgnoreCase));

            if (dataLine is null)
            {
                throw new InvalidOperationException(
                    $"SSE response does not contain a data line. Raw: {raw}");
            }

            return dataLine["data:".Length..].Trim();
        }
    }
}