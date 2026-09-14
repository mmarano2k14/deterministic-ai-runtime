namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag
{
    /// <summary>
    /// Real hosted .NET execution followed by serialized checkpoint/service reconstruction.
    /// The .NET function runs in a real process; interruption of the runtime host is modeled
    /// by disposed services and restored stores, not by an OS kill or MongoDB restart.
    /// </summary>
    [Trait("Category", "DotNetProcess")]
    public sealed class AiDurableInvocationColdRestoreProcessTests
    {
        [Fact]
        public async Task Real_DotNet_Result_Is_Applied_After_Cold_Restore_Without_Relaunching_The_Worker()
        {
            var checkpoint = await DurableInvocationColdRestoreProof.CaptureAsync("dotnet", true, false, realDotNet: true);
            await DurableInvocationColdRestoreProof.RestoreAndApplyAsync(checkpoint);
        }
    }
}
