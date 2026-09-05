using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.Auth;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.Context;
using System.Net.Sockets;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.Controllers
{
    [ApiController]
    [Route("demo")]
    [Authorize]
    public sealed class DemoController : ControllerBase
    {
        private readonly IContextStore _store;
        private readonly DemoSeedState _state;
        private readonly ContextRuntimeOptions _opt;
        private readonly IDemoBootstrapTicketProtector _ticketProtector;

        public DemoController(
            IContextStore store,
            DemoSeedState state,
            IDemoBootstrapTicketProtector ticketProtector,
            IOptions<ContextRuntimeOptions> options)
        {
            _store = store;
            _state = state;
            _ticketProtector = ticketProtector;
            _opt = options.Value;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] Auth.LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username))
                return BadRequest("Username required");

            var ctx = ContextFactory.Full(request.Username);

            var contextKey = await _store.StoreAsync(ctx);

            var ticket = new DemoBootstrapTicket
            {
                UserId = ctx.UserId,
                TenantId = ctx.TenantId,
                TenantGroupId = ctx.TenantGroupId,
                CurrentNamespace = ctx.Namespaces?.FirstOrDefault()?.Name,
                IssuedAtUtc = DateTimeOffset.UtcNow
            };

            var protectedTicket = _ticketProtector.Protect(ticket);

            Response.Cookies.Append(
                "demo_session",
                protectedTicket,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = false, // true en HTTPS réel
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    Expires = DateTimeOffset.UtcNow.AddHours(8)
                });

            _state.AccessContextKey = contextKey;
            Response.Headers[_opt.AccessContextHeader] = contextKey;

            return Ok(new
            {
                accessContext = contextKey,
                user = request.Username
            });
        }
        [HttpGet("bootstrap")]
        public async Task<IActionResult> Bootstrap()
        {
            if (!Request.Cookies.TryGetValue("demo_session", out var protectedTicket) || string.IsNullOrWhiteSpace(protectedTicket))
            {
                return Ok(new
                {
                    isAuthenticated = false,
                    contextKey = (string?)null
                });
            }

            var ticket = _ticketProtector.Unprotect(protectedTicket);

            if (ticket == null)
            {
                return Ok(new
                {
                    isAuthenticated = false,
                    contextKey = (string?)null
                });
            }

            var ctx = ContextFactory.Full(ticket.UserId);

            var contextKey = await _store.StoreAsync(ctx);

            _state.AccessContextKey = contextKey;
            Response.Headers[_opt.AccessContextHeader] = contextKey;

            return Ok(new
            {
                isAuthenticated = true,
                contextKey = contextKey,
                userId = ticket.UserId
            });
        }


        [HttpGet("context")]
        public IActionResult GetContext()
        {
            if (string.IsNullOrWhiteSpace(_state.AccessContextKey))
                return Problem("Demo context was not seeded yet.");

            Response.Headers[_opt.AccessContextHeader] = _state.AccessContextKey;
            return Ok(new { accessContext = _state.AccessContextKey });
        }

    }
}