using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.PublicSdk
{
    public sealed class AiPublicSdkPublicationReadBoundaryTests
    {
        [Fact]
        public void Publication_Read_Boundary_Is_Explicitly_Available_To_The_Server_Adapter()
        {
            Assert.NotNull(typeof(AiPipelinePublicationService).GetMethod(nameof(AiPipelinePublicationService.ReadDefinitionAsync)));
            Assert.NotNull(typeof(AiPublishedDagRunService).GetMethod(nameof(AiPublishedDagRunService.ReadPinAsync)));
        }
    }
}
