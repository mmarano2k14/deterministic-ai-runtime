using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.DotNet;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.Python;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Packaging
{
    /// <summary>Finite closure proof for immutable packaged dependencies across the three hosted languages.</summary>
    [Trait("Category", "DependencyPackagingClosure")]
    public sealed class AiDependencyPackagingCompatibilityClosureTests
    {
        [Fact]
        public void Capability_Matrix_Closes_The_Selected_Package_Formats_Without_A_Second_Environment_Identity()
        {
            var capabilities = AiDependencyPackagingContracts.All.OrderBy(value => value.Kind).ToArray();

            Assert.Equal(3, capabilities.Length);
            Assert.All(capabilities, capability =>
                Assert.Equal(AiDependencyPackageExecutionSupport.Hosted, capability.Support));
            Assert.Equal("python", capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.PythonWheelBundle).ExecutionLanguage);
            Assert.Equal("typescript", capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.NodeLockedBundle).ExecutionLanguage);
            Assert.Equal("dotnet", capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.DotNetAssemblyClosure).ExecutionLanguage);

            var dependency = new AiPublicationDependencyUpload(
                "legacy",
                "1.0.0",
                new[] { new AiPublicationFileUpload("legacy.dat", new byte[] { 1, 2, 3 }) });
            Assert.Null(dependency.Package);
            Assert.DoesNotContain("Package", JsonSerializer.Serialize(dependency), StringComparison.Ordinal);
        }

        [PythonWorkerFact]
        public async Task Python_Wheel_Run_Keeps_The_Original_Package_After_Republication()
        {
            var profile = await PythonWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var publication = fixture.Publication;
            publication.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
            publication.Clock.Set(DateTimeOffset.UtcNow);

            const string source = "from wheel_rules import transform\ndef run(inputs, context):\n    return {\"success\": True, \"payload\": transform(inputs[\"amount\"])}";
            var original = await publication.PublishAsync(PythonWorkerTestSupport.Upload(
                profile.Runtime,
                source: source,
                dependencies: new[] { PythonWorkerTestSupport.WheelUpload(4) }));
            var parent = await publication.CreateAsync(original);
            var replacement = await publication.PublishAsync(PythonWorkerTestSupport.Upload(
                profile.Runtime,
                source: source,
                dependencies: new[] { PythonWorkerTestSupport.WheelUpload(9) }));
            Assert.NotEqual(original.PublicationRef, replacement.PublicationRef);

            Assert.Equal(AiExecutionStatus.Waiting, (await publication.RunNextAsync(parent.ExecutionId)).Status);
            var identity = Identity(parent.ExecutionId);
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = PythonWorkerTestSupport.Supervisor(
                fixture,
                await PythonWorkerTestSupport.TransportAsync(),
                capacity,
                options);

            Assert.Equal(AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var recorded = (await publication.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal(original.PublicationRef, recorded.Definition.Target.PublicationRef);
            Assert.Equal("84", recorded.Result!.PayloadJson);
            Assert.Equal(0, publication.LatestLookups);

            await ApplyFirstAsync(publication, parent.ExecutionId, recorded.ResultSha256!);
        }

        [TypeScriptWorkerFact]
        public async Task Node_Locked_Run_Keeps_The_Original_Package_After_Republication()
        {
            var profile = await TypeScriptWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var publication = fixture.Publication;
            publication.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
            publication.Clock.Set(DateTimeOffset.UtcNow);

            var original = await publication.PublishAsync(TypeScriptWorkerTestSupport.UploadWithLockedDependency(
                profile.Runtime,
                revision: "1",
                factor: "4"));
            var parent = await publication.CreateAsync(original);
            var replacement = await publication.PublishAsync(TypeScriptWorkerTestSupport.UploadWithLockedDependency(
                profile.Runtime,
                revision: "2",
                factor: "9"));
            Assert.NotEqual(original.PublicationRef, replacement.PublicationRef);

            Assert.Equal(AiExecutionStatus.Waiting, (await publication.RunNextAsync(parent.ExecutionId)).Status);
            var identity = Identity(parent.ExecutionId);
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = TypeScriptWorkerTestSupport.Supervisor(
                fixture,
                await TypeScriptWorkerTestSupport.TransportAsync(),
                capacity,
                options);

            Assert.Equal(AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var recorded = (await publication.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal(original.PublicationRef, recorded.Definition.Target.PublicationRef);
            using (var payload = JsonDocument.Parse(recorded.Result!.PayloadJson))
            {
                Assert.Equal(1, payload.RootElement.GetProperty("revision").GetInt32());
                Assert.Equal(4, payload.RootElement.GetProperty("amount").GetInt32());
            }
            Assert.Equal(0, publication.LatestLookups);

            await ApplyFirstAsync(publication, parent.ExecutionId, recorded.ResultSha256!);
        }

        [Fact]
        public async Task DotNet_Managed_Closure_Run_Remains_On_The_Original_Publication_After_Republication()
        {
            var profile = await DotNetWorkerTestSupport.ProfileAsync();
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var publication = fixture.Publication;
            publication.Environments.Entries[profile.Runtime.Reference] = profile.Runtime;
            publication.Clock.Set(DateTimeOffset.UtcNow);

            var original = await publication.PublishAsync(DotNetWorkerTestSupport.Upload(
                profile.Runtime,
                revision: "1",
                packagedDependency: true,
                method: "UseDependency"));
            var parent = await publication.CreateAsync(original);
            var replacement = await publication.PublishAsync(DotNetWorkerTestSupport.Upload(
                profile.Runtime,
                revision: "2",
                packagedDependency: true,
                method: "UseDependency"));
            Assert.NotEqual(original.PublicationRef, replacement.PublicationRef);

            Assert.Equal(AiExecutionStatus.Waiting, (await publication.RunNextAsync(parent.ExecutionId)).Status);
            var identity = Identity(parent.ExecutionId);
            var options = new AiWorkerSupervisionOptions();
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = DotNetWorkerTestSupport.Supervisor(
                fixture,
                await DotNetWorkerTestSupport.TransportAsync(),
                capacity,
                options);

            Assert.Equal(AiWorkerDispatchDisposition.Accepted,
                (await supervisor.DispatchAsync(PublicationTestSupport.Scope, identity)).Disposition);
            var recorded = (await publication.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.Equal(original.PublicationRef, recorded.Definition.Target.PublicationRef);
            Assert.Equal("3", recorded.Result!.PayloadJson);
            Assert.Equal(0, publication.LatestLookups);

            await ApplyFirstAsync(publication, parent.ExecutionId, recorded.ResultSha256!);
        }

        [PythonWorkerFact]
        public async Task Missing_Python_Wheel_Material_Is_Refused_Before_Worker_Launch()
        {
            var profile = await PythonWorkerTestSupport.ProfileAsync();
            const string source = "from wheel_rules import transform\ndef run(inputs, context):\n    return {\"success\": True, \"payload\": transform(inputs[\"amount\"])}";
            await MissingMaterialIsRefusedAsync(
                profile.Runtime,
                PythonWorkerTestSupport.Upload(
                    profile.Runtime,
                    source: source,
                    dependencies: new[] { PythonWorkerTestSupport.WheelUpload() }));
        }

        [TypeScriptWorkerFact]
        public async Task Missing_Node_Locked_Material_Is_Refused_Before_Worker_Launch()
        {
            var profile = await TypeScriptWorkerTestSupport.ProfileAsync();
            await MissingMaterialIsRefusedAsync(
                profile.Runtime,
                TypeScriptWorkerTestSupport.UploadWithLockedDependency(profile.Runtime));
        }

        [Fact]
        public async Task Missing_DotNet_Managed_Closure_Material_Is_Refused_Before_Worker_Launch()
        {
            var profile = await DotNetWorkerTestSupport.ProfileAsync();
            await MissingMaterialIsRefusedAsync(
                profile.Runtime,
                DotNetWorkerTestSupport.Upload(
                    profile.Runtime,
                    packagedDependency: true,
                    method: "UseDependency"));
        }

        private static AiDurableInvocationIdentity Identity(string executionId) =>
            new(PublicationTestSupport.Scope.TenantId, executionId, "first");

        private static async Task ApplyFirstAsync(
            PublicationTestSupport.Fixture publication,
            string executionId,
            string resultSha256)
        {
            var engine = new AiDagExecutionEngine(
                publication.EngineServices,
                DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            await engine.ResumeExternalWaitingStepAsync(executionId, "first");
            await publication.RunNextAsync(executionId);
            var step = (await publication.Store.GetStateAsync(executionId))!.Steps["first"];
            Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
            Assert.Equal(resultSha256, step.Result!.InvocationReceipt!.ResultSha256);
        }

        private static async Task MissingMaterialIsRefusedAsync(
            AiPublicationEnvironment runtime,
            AiPipelinePublicationUpload upload)
        {
            using var fixture = new WorkerTestSupport.PublishedFixture();
            var publication = fixture.Publication;
            publication.Environments.Entries[runtime.Reference] = runtime;
            publication.Clock.Set(DateTimeOffset.UtcNow);

            var published = await publication.PublishAsync(upload);
            var parent = await publication.CreateAsync(published);
            Assert.Equal(AiExecutionStatus.Waiting, (await publication.RunNextAsync(parent.ExecutionId)).Status);
            var identity = Identity(parent.ExecutionId);
            var invocation = (await publication.Journal.GetAsync(PublicationTestSupport.Scope, identity))!;
            Assert.NotNull(invocation);

            var function = published.Manifest.Functions.Single(value => value.Site.StepName == "first");
            var environmentJson = publication.MemoryPayloads.Documents[function.Environment.Key];
            var environment = JsonSerializer.Deserialize<AiPublicationEnvironmentSnapshot>(environmentJson)!;
            var dependency = Assert.Single(environment.Dependencies);
            Assert.NotNull(dependency.Package);
            var packageFile = dependency.Files.First(file =>
                !string.Equals(file.Path, dependency.Package!.ManifestPath, StringComparison.Ordinal));
            Assert.True(publication.MemoryPayloads.Documents.TryRemove(packageFile.Payload.Key, out _));

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Preparer.PrepareAsync(invocation));
            Assert.Null(publication.Accessor.Current);
        }
    }
}
