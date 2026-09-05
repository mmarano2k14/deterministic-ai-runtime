using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Providers;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Services;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.Controllers
{
    [ApiController]
    [Route("runtime-analysis")]
    [Authorize]
    public sealed class RuntimeAnalysisController : ControllerBase
    {
        private const int MaxQuestionLength = 2000;

        private readonly IRuntimeAnalysisSnapshotBuilder _snapshotBuilder;
        private readonly IAiRuntimeAnalysisProvider _analysisProvider;
        private readonly IRuntimeAnalysisRuntimeExecutor _runtimeExecutor;
        private readonly IRuntimeAnalysisHumanApprovalService _approvalService;
        private readonly IRuntimeAnalysisScenarioExecutionService
            _scenarioExecutionService;
        private readonly RuntimeAnalysisChildActionService
            _childActionService;

        public RuntimeAnalysisController(
            IRuntimeAnalysisSnapshotBuilder snapshotBuilder,
            IAiRuntimeAnalysisProvider analysisProvider,
            IRuntimeAnalysisRuntimeExecutor runtimeExecutor,
            IRuntimeAnalysisHumanApprovalService approvalService,
            IRuntimeAnalysisScenarioExecutionService scenarioExecutionService,
            RuntimeAnalysisChildActionService childActionService)
        {
            _snapshotBuilder = snapshotBuilder;
            _analysisProvider = analysisProvider;
            _runtimeExecutor = runtimeExecutor;
            _approvalService = approvalService;
            _scenarioExecutionService = scenarioExecutionService;
            _childActionService =
                childActionService
                ?? throw new ArgumentNullException(
                    nameof(childActionService));
        }

        [HttpGet("provider-status")]
        [ProducesResponseType<RuntimeAnalysisProviderStatus>(StatusCodes.Status200OK)]
        public ActionResult<RuntimeAnalysisProviderStatus> GetProviderStatus()
        {
            return Ok(_analysisProvider.Status);
        }

        [HttpPost("snapshot")]
        [ProducesResponseType<RuntimeAnalysisSnapshot>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<RuntimeAnalysisSnapshot> BuildSnapshot(
            [FromBody] RuntimeAnalysisSnapshotRequest request)
        {
            try
            {
                return Ok(_snapshotBuilder.Build(request));
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
        }

        [HttpPost("analyze")]
        [ProducesResponseType<RuntimeAnalysisRuntimeExecutionResult>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<RuntimeAnalysisRuntimeExecutionResult>> AnalyzeAsync(
            [FromBody] RuntimeAnalysisAnalyzeRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                ValidateQuestion(request.Question);

                var snapshot = _snapshotBuilder.Build(request.SnapshotRequest);

                var result = await _runtimeExecutor.AnalyzeAsync(
                        new RuntimeAnalysisProviderRequest
                        {
                            Question = request.Question.Trim(),
                            InvestigationMode =
                                NormalizeInvestigationMode(
                                    request.InvestigationMode),
                            Snapshot = snapshot
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return Ok(result);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { error = exception.Message });
            }
            catch (RuntimeAnalysisRuntimeExecutionException exception)
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new { error = exception.Message });
            }
        }

        [HttpPost("executions/{executionId}/approval")]
        [ProducesResponseType<RuntimeAnalysisRuntimeExecutionResult>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<RuntimeAnalysisRuntimeExecutionResult>> DecideApprovalAsync(
            string executionId,
            [FromBody] RuntimeAnalysisHumanApprovalDecisionRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                var decidedBy =
                    User.Identity?.Name
                    ?? "authenticated-user";

                var result = await _approvalService.DecideAsync(
                        executionId,
                        request.Decision,
                        decidedBy,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Ok(result);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Conflict(new { error = exception.Message });
            }
            catch (RuntimeAnalysisRuntimeExecutionException exception)
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new { error = exception.Message });
            }
        }

        [HttpPost("executions/{executionId}/scenario-execution")]
        [ProducesResponseType<RuntimeAnalysisRuntimeExecutionResult>(
            StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<RuntimeAnalysisRuntimeExecutionResult>>
            CompleteScenarioExecutionAsync(
                string executionId,
                [FromBody] RuntimeAnalysisScenarioExecutionObservation observation,
                CancellationToken cancellationToken)
        {
            try
            {
                var completedBy =
                    User.Identity?.Name
                    ?? "authenticated-user";

                var result = await _scenarioExecutionService.CompleteAsync(
                        executionId,
                        observation,
                        completedBy,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Ok(
                    result);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(
                    new
                    {
                        error = exception.Message
                    });
            }
            catch (InvalidOperationException exception)
            {
                return Conflict(
                    new
                    {
                        error = exception.Message
                    });
            }
            catch (RuntimeAnalysisRuntimeExecutionException exception)
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new
                    {
                        error = exception.Message
                    });
            }
        }

        [HttpGet("executions/{rootExecutionId}")]
        [ProducesResponseType<RuntimeAnalysisRuntimeExecutionResult>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<RuntimeAnalysisRuntimeExecutionResult>>
            GetExecutionAsync(
                string rootExecutionId,
                [FromQuery] string rootRunId,
                CancellationToken cancellationToken)
        {
            try
            {
                var result = await _childActionService.GetRootAsync(
                        rootExecutionId,
                        rootRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Ok(result);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (RuntimeAnalysisRuntimeExecutionException exception)
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new { error = exception.Message });
            }
        }

        [HttpPost("executions/{rootExecutionId}/children/{childExecutionId}/approval")]
        [ProducesResponseType<RuntimeAnalysisRuntimeExecutionResult>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<RuntimeAnalysisRuntimeExecutionResult>>
            DecideChildApprovalAsync(
                string rootExecutionId,
                string childExecutionId,
                [FromBody] RuntimeAnalysisChildApprovalDecisionRequest request,
                CancellationToken cancellationToken)
        {
            try
            {
                var decidedBy =
                    User.Identity?.Name
                    ?? "authenticated-user";

                var result = await _childActionService.DecideApprovalAsync(
                        rootExecutionId,
                        childExecutionId,
                        request.RootRunId,
                        request.Decision,
                        decidedBy,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Ok(result);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Conflict(new { error = exception.Message });
            }
            catch (RuntimeAnalysisRuntimeExecutionException exception)
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new { error = exception.Message });
            }
        }

        [HttpPost("executions/{rootExecutionId}/children/{childExecutionId}/scenario-execution")]
        [ProducesResponseType<RuntimeAnalysisRuntimeExecutionResult>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<RuntimeAnalysisRuntimeExecutionResult>>
            CompleteChildScenarioExecutionAsync(
                string rootExecutionId,
                string childExecutionId,
                [FromBody] RuntimeAnalysisChildScenarioExecutionRequest request,
                CancellationToken cancellationToken)
        {
            try
            {
                var completedBy =
                    User.Identity?.Name
                    ?? "authenticated-user";

                var result = await _childActionService.CompleteScenarioExecutionAsync(
                        rootExecutionId,
                        childExecutionId,
                        request.RootRunId,
                        request.Observation,
                        completedBy,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Ok(result);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Conflict(new { error = exception.Message });
            }
            catch (RuntimeAnalysisRuntimeExecutionException exception)
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new { error = exception.Message });
            }
        }

        private static string NormalizeInvestigationMode(
            string? investigationMode)
        {
            var normalized =
                string.IsNullOrWhiteSpace(
                    investigationMode)
                    ? RuntimeAnalysisInvestigationModes.StopWhenConclusive
                    : investigationMode.Trim();

            if (!RuntimeAnalysisInvestigationModes.IsSupported(
                    normalized))
            {
                throw new ArgumentException(
                    $"Unsupported investigation mode '{normalized}'.");
            }

            return normalized;
        }

        private static void ValidateQuestion(string? question)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                throw new ArgumentException(
                    "Runtime analysis question is required.");
            }

            if (question.Length > MaxQuestionLength)
            {
                throw new ArgumentException(
                    $"Runtime analysis question cannot exceed {MaxQuestionLength} characters.");
            }
        }
    }
}
