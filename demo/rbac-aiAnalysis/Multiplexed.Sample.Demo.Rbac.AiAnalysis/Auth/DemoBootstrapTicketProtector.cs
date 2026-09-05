using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.Auth
{
    public sealed class DemoBootstrapTicketProtector : IDemoBootstrapTicketProtector
    {
        private readonly IDataProtector _protector;
        private readonly JsonSerializerOptions _jsonOptions;

        public DemoBootstrapTicketProtector(IDataProtectionProvider provider)
        {
            _protector = provider.CreateProtector("demo.bootstrap.ticket.v1");
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        public string Protect(DemoBootstrapTicket ticket)
        {
            var json = JsonSerializer.Serialize(ticket, _jsonOptions);
            return _protector.Protect(json);
        }

        public DemoBootstrapTicket? Unprotect(string protectedValue)
        {
            try
            {
                var json = _protector.Unprotect(protectedValue);
                return JsonSerializer.Deserialize<DemoBootstrapTicket>(json, _jsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }
}
