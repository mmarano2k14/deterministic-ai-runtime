using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    public sealed class FakeRuntimeObservability : IAiRuntimeObservability
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FakeRuntimeObservability"/> class.
        /// </summary>
        /// <param name="ledger">The decision ledger recorder.</param>
        public FakeRuntimeObservability(IAiDecisionLedgerRecorder ledger)
        {
            this.Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        /// <inheritdoc />
        public IAiRuntimeMetrics Metrics => throw new NotSupportedException();

        /// <inheritdoc />
        public IAiRuntimeTracer Tracer => throw new NotSupportedException();

        /// <inheritdoc />
        public IAiDecisionLedgerRecorder Ledger { get; }

        /// <inheritdoc />
        public IAiRuntimeCorrelationAccessor Correlation => throw new NotSupportedException();
    }
}
