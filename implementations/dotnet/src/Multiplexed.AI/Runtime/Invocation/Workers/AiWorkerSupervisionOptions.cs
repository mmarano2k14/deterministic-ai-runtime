using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>Explicit host policy. Expired work is not replayed without a declared idempotent-work contract.</summary>
    public sealed class AiWorkerSupervisionOptions
    {
        public AiWorkerSupervisionOptions(int maxConcurrentProcesses = 4, TimeSpan? leaseDuration = null,
            TimeSpan? renewalInterval = null, TimeSpan? leaseSafetyMargin = null,
            TimeSpan? executionTimeout = null, bool allowExpiredLeaseReassignment = false,
            int maxAssignmentEpoch = 8)
        {
            MaxConcurrentProcesses = maxConcurrentProcesses;
            LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(30);
            RenewalInterval = renewalInterval ?? TimeSpan.FromSeconds(5);
            LeaseSafetyMargin = leaseSafetyMargin ?? TimeSpan.FromSeconds(2);
            ExecutionTimeout = executionTimeout ?? TimeSpan.FromMinutes(2);
            AllowExpiredLeaseReassignment = allowExpiredLeaseReassignment;
            MaxAssignmentEpoch = maxAssignmentEpoch;
            if (maxConcurrentProcesses is < 1 or > 32 || maxAssignmentEpoch is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentProcesses));
            if (LeaseDuration < TimeSpan.FromSeconds(3) || LeaseDuration > TimeSpan.FromMinutes(5) ||
                LeaseDuration.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
                RenewalInterval < TimeSpan.FromMilliseconds(100) || RenewalInterval >= LeaseDuration / 2 ||
                LeaseSafetyMargin < TimeSpan.FromMilliseconds(100) || LeaseSafetyMargin >= LeaseDuration / 3 ||
                RenewalInterval + LeaseSafetyMargin >= LeaseDuration ||
                ExecutionTimeout < TimeSpan.FromSeconds(1) || ExecutionTimeout > TimeSpan.FromHours(1))
                throw new ArgumentException("Invalid worker lease, renewal, safety margin or execution timeout.");
        }
        public int MaxConcurrentProcesses { get; }
        public TimeSpan LeaseDuration { get; }
        public TimeSpan RenewalInterval { get; }
        public TimeSpan LeaseSafetyMargin { get; }
        public TimeSpan ExecutionTimeout { get; }
        public bool AllowExpiredLeaseReassignment { get; }
        public int MaxAssignmentEpoch { get; }
    }

    /// <summary>Explicit trusted control-plane scopes. No tenant discovery or public listener is installed.</summary>
    public sealed class AiWorkerPollingOptions
    {
        public AiWorkerPollingOptions(IEnumerable<AiDurableInvocationScope> scopes,
            IEnumerable<string> executionLanguages, int pageSize = 16, TimeSpan? interval = null)
        {
            ArgumentNullException.ThrowIfNull(scopes); ArgumentNullException.ThrowIfNull(executionLanguages);
            Scopes = Array.AsReadOnly(scopes.Distinct().ToArray());
            ExecutionLanguages = Array.AsReadOnly(executionLanguages.Distinct(StringComparer.Ordinal).ToArray());
            PageSize = pageSize; Interval = interval ?? TimeSpan.FromSeconds(1);
            if (Scopes.Count is < 1 or > 1024 || ExecutionLanguages.Count is < 1 or > 3 ||
                pageSize is < 1 or > 100 || Interval < TimeSpan.FromMilliseconds(100) || Interval > TimeSpan.FromMinutes(5))
                throw new ArgumentException("Polling requires bounded explicit scopes, languages, page size and interval.");
            foreach (var scope in Scopes) Durable.AiDurableInvocationValidation.ValidateScope(scope);
            foreach (var language in ExecutionLanguages) Durable.AiDurableInvocationValidation.ValidateLanguage(language);
        }
        public IReadOnlyList<AiDurableInvocationScope> Scopes { get; }
        public IReadOnlyList<string> ExecutionLanguages { get; }
        public int PageSize { get; }
        public TimeSpan Interval { get; }
    }
}
