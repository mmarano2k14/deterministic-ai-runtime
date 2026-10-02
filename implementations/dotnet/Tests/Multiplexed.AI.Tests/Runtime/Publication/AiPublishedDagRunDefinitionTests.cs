namespace Multiplexed.AI.Tests.Runtime.Publication
{
    public sealed class AiPublishedDagRunDefinitionTests
    {
        [Fact]
        public async Task Creation_Returns_The_Verified_Frozen_Definition_Under_Execute_Authority()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var executeOnly = PublicationTestSupport.Identity(actions: new[] { "execute" });

            var result = await fixture.AsAsync(
                () => fixture.Runs.CreateWithDefinitionAsync(
                    PublicationTestSupport.Scope,
                    "dispatch-definition",
                    publication.PublicationRef,
                    "{\"amount\":1}"),
                executeOnly);

            Assert.Equal(publication.Manifest.PipelineName, result.Definition.Name);
            Assert.Equal(publication.Manifest.PipelineVersion, result.Definition.Version);
            Assert.Equal(result.Record.PipelineName, result.Definition.Name);
            Assert.Equal(
                publication.Manifest.Definition.Sha256,
                result.Record.PipelineDefinitionSnapshot?.ContentHash);
        }
    }
}
