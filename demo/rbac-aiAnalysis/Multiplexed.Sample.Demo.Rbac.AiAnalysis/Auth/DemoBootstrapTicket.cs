public sealed class DemoBootstrapTicket
{
    public string UserId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string TenantGroupId { get; set; } = default!;
    public string? CurrentNamespace { get; set; } = default!;
    public DateTimeOffset IssuedAtUtc { get; set; }
}