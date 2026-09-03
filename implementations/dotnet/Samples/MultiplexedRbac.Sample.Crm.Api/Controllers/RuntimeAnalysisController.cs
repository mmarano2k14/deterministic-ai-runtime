using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Services;

namespace MultiplexedRbac.Sample.Crm.Api.Controllers
{
    [ApiController]
    [Route("runtime-analysis")]
    [Authorize]
    public sealed class RuntimeAnalysisController : ControllerBase
    {
        private readonly IRuntimeAnalysisSnapshotBuilder _snapshotBuilder;

        public RuntimeAnalysisController(
            IRuntimeAnalysisSnapshotBuilder snapshotBuilder)
        {
            _snapshotBuilder = snapshotBuilder;
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
    }
}
