using Microsoft.AspNetCore.Mvc;
using NServiceBus;
using MultiplexedRbac.Sample.Crm.Contracts.Events;
using Multiplexed.Rbac.Core.Authorization.Engine;

// using MultiplexedRbac.Core.Authorization;  // ton IAuthorizationEngine (Part 4)

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.Controllers;

[ApiController]
[Route("billing")]
public sealed class BillingController : ControllerBase
{
    private readonly IMessageSession _bus;
    private readonly IAuthorizationEngine _auth;

    public BillingController(
        IMessageSession bus,
        IAuthorizationEngine auth
    )
    {
        _bus = bus;
        _auth = auth;
    }

    [HttpPost("{invoiceId}/refund")]
    public async Task<IActionResult> Refund([FromRoute] string invoiceId, [FromQuery] decimal amount)
    {
        // ✅ Part 4 deterministic check
        if (!_auth.IsAllowed("billing","invoice","refund"))
             return Forbid();

        await _bus.Publish(new InvoiceRefundRequested
        {
            InvoiceId = invoiceId,
            Amount = amount
        });

        return Accepted(new { invoiceId, amount, status = "InvoiceRefundRequested published" });
    }

    [HttpGet("{invoiceId}")]
    public IActionResult Get([FromRoute] string invoiceId)
    {
        if (!_auth.IsAllowed("billing","invoice","read"))
             return Forbid();

        return Ok(new { invoiceId, status = "demo invoice read" });
    }
}