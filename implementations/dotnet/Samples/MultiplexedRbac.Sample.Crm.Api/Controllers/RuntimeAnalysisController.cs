using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Providers;
using MultiplexedRbac.Sample.Crm.Api.AI.Runtime;
using MultiplexedRbac.Sample.Crm.Api.AI.Services;

namespace MultiplexedRbac.Sample.Crm.Api.Controllers
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
        private readonly IRuntimeAnalysisScenarioPolicyExecutor _scenarioPolicyExecutor;

        public RuntimeAnalysisController(
            IRuntimeAnalysisSnapshotBuilder snapshotBuilder,
            IAiRuntimeAnalysisProvider analysisProvider,
            IRuntimeAnalysisRuntimeExecutor runtimeExecutor,
            IRuntimeAnalysisScenarioPolicyExecutor scenarioPolicyExecutor)
        {
            _snapshotBuilder = snapshotBuilder;
            _analysisProvider = analysisProvider;
            _runtimeExecutor = runtimeExecutor;
            _scenarioPolicyExecutor = scenarioPolicyExecutor;
        }

        [HttpGet("provider-status")]
        [ProducesResponseType<RuntimeAnalysisProviderStatus>(
            StatusCodes.Status200OK)]
        public ActionResult<RuntimeAnalysisProviderStatus> GetProviderStatus()
        {
            return Ok(
                _analysisProvider.Status);
        }

        [HttpPost("snapshot")]
        [ProducesResponseType<RuntimeAnalysisSnapshot>(
            StatusCodes.Status200OK)]
        [ProducesResponseType(
            StatusCodes.Status400BadRequest)]
        public ActionResult<RuntimeAnalysisSnapshot> BuildSnapshot(
            [FromBody] RuntimeAnalysisSnapshotRequest request)
        {
            try
            {
                var snapshot = _snapshotBuilder.Build(
                    request);

                return Ok(
                    snapshot);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(
                    new
                    {
                        error = exception.Message
                    });
            }
        }

        [HttpPost("analyze")]
        [ProducesResponseType<RuntimeAnalysisRuntimeExecutionResult>(
            StatusCodes.Status200OK)]
        [ProducesResponseType(
            StatusCodes.Status400BadRequest)]
        [ProducesResponseType(
            StatusCodes.Status502BadGateway)]
        [ProducesResponseType(
            StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<RuntimeAnalysisRuntimeExecutionResult>>
            AnalyzeAsync(
                [FromBody] RuntimeAnalysisAnalyzeRequest request,
                CancellationToken cancellationToken)
        {
            try
            {
                ValidateQuestion(
                    request.Question);

                var snapshot = _snapshotBuilder.Build(
                    request.SnapshotRequest);

                var result = await _runtimeExecutor.AnalyzeAsync(
                        new RuntimeAnalysisProviderRequest
                        {
                            Question = request.Question.Trim(),
                            Snapshot = snapshot
                        },
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
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
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

        [HttpPost("validate-scenario")]
        [ProducesResponseType<
            RuntimeAnalysisScenarioPolicyRuntimeExecutionResult>(
                StatusCodes.Status200OK)]
        [ProducesResponseType(
            StatusCodes.Status400BadRequest)]
        [ProducesResponseType(
            StatusCodes.Status502BadGateway)]
        public async Task<
            ActionResult<RuntimeAnalysisScenarioPolicyRuntimeExecutionResult>>
            ValidateScenarioAsync(
                [FromBody] RuntimeAnalysisScenarioPolicyValidationRequest request,
                CancellationToken cancellationToken)
        {
            if (request.Scenario is null)
            {
                return BadRequest(
                    new
                    {
                        error = "Suggested scenario is required."
                    });
            }

            try
            {
                var result = await _scenarioPolicyExecutor.ValidateAsync(
                        request.Scenario,
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

        private static void ValidateQuestion(
            string? question)
        {
            if (string.IsNullOrWhiteSpace(
                    question))
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
