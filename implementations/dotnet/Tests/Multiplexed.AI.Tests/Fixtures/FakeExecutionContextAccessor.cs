using System;
using Multiplexed.Rbac.Core.ExecutionContext;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Test execution context accessor used by shared queue dispatcher unit tests.
    /// </summary>
    public sealed class FakeExecutionContextAccessor : IExecutionContextAccessor
    {
        private RbacExecutionContext? current;

        /// <inheritdoc />
        public RbacExecutionContext? Current => this.current;

        /// <inheritdoc />
        public void Set(
            RbacExecutionContext context)
        {
            this.current =
                context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <inheritdoc />
        public void Clear()
        {
            this.current = null;
        }
    }
}