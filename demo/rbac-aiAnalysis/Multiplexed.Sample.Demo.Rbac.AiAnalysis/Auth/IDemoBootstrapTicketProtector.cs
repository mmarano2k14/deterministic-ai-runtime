namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.Auth
{
    public interface IDemoBootstrapTicketProtector
    {
        string Protect(DemoBootstrapTicket ticket);
        DemoBootstrapTicket? Unprotect(string protectedValue);
    }
}
