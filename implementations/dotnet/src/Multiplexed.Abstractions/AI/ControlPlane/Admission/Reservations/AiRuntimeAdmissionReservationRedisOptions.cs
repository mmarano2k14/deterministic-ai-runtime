namespace Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations
{
    /// <summary>
    /// Configuration options for Redis-backed runtime admission reservations.
    /// </summary>
    public sealed class AiRuntimeAdmissionReservationRedisOptions
    {
        /// <summary>
        /// Redis key prefix used to isolate reservation keys between environments,
        /// tests, deployments, or tenants.
        /// </summary>
        public string KeyPrefix { get; set; } = "multiplexed:ai";

        /// <summary>
        /// Logical TTL applied to each reservation member.
        /// </summary>
        public TimeSpan ReservationTtl { get; set; } =
            TimeSpan.FromMinutes(2);

        /// <summary>
        /// Redis key TTL applied to the ZSET key itself.
        /// This should be longer than <see cref="ReservationTtl"/>.
        /// </summary>
        public TimeSpan KeyTtl { get; set; } =
            TimeSpan.FromMinutes(10);
    }
}