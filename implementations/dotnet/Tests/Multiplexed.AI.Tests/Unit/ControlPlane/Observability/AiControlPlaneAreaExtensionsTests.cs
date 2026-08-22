using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Guards stable export names for centralized engine-event areas.
    /// </summary>
    public sealed class AiControlPlaneAreaExtensionsTests
    {
        [Theory]
        [InlineData(AiControlPlaneArea.Recovery, "recovery")]
        [InlineData(AiControlPlaneArea.ChildDag, "child-dag")]
        [InlineData(AiControlPlaneArea.Policy, "policy")]
        public void ToStableName_Should_Cover_Centralized_Event_Areas(
            AiControlPlaneArea area,
            string expected)
        {
            Assert.Equal(expected, area.ToStableName());
        }
    }
}
