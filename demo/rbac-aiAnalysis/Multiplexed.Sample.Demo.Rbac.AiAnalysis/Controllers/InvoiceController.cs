using Microsoft.AspNetCore.Mvc;
using Multiplexed.Rbac.Core.Authorization.Engine;
using MultiplexedRbac.Sample.Crm.Services.Contracts;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.Controllers
{
    [ApiController]
    [Route("invoices")]
    public sealed class InvoiceController : ControllerBase
    {
        private readonly IAuthorizationEngine _auth;
        private readonly IInvoiceService _svc;

        public InvoiceController(IAuthorizationEngine auth, IInvoiceService svc)
        {
            _auth = auth;
            _svc = svc;
        }

        [HttpGet("{invoiceId}")]
        public async Task<IActionResult> Get(string invoiceId, CancellationToken ct)
        {
            // Engine direct (Part 4)
            if (!_auth.IsAllowed("billing", "invoice", "read"))
                return Forbid();

            // Proxy call (Part 4) — will also enforce if attribute is on interface/method
            var result = await _svc.GetAsync(invoiceId, ct);
            return Ok(new { invoiceId, result });
        }

        [HttpPost("{invoiceId}/refund")]
        public async Task<IActionResult> Refund(string invoiceId, [FromQuery] decimal amount, CancellationToken ct)
        {
            if (!_auth.IsAllowed("billing", "invoice", "refund"))
                return Forbid();

            // Optional: call service too (proxy enforcement)
            await _svc.RefundAsync(invoiceId, amount, ct);

            // Here you publish InvoiceRefundRequested (Part 6)
            return Accepted(new { invoiceId, amount });
        }
    }
}
