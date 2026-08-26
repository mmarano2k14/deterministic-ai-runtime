using Multiplexed.Abstractions.AI.Execution;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    public sealed class KubernetesRuntimePoolContinuationConsumePhysicalKillTests
    {
        [Fact]
        public void PrearmedKillCommand_Should_Wait_For_Explicit_Trigger_Before_Kill()
        {
            var arguments =
                KubernetesRuntimePoolProductionInfrastructure
                    .CreatePrearmedRuntimeProcessKillArguments(
                        "pod-a",
                        "ai-runtime",
                        4321);

            Assert.Equal("exec", arguments[0]);
            Assert.Equal("-i", arguments[1]);
            Assert.Equal("pod-a", arguments[2]);
            Assert.Equal("sh", arguments[^3]);
            Assert.Equal("-c", arguments[^2]);

            var command = arguments[^1];
            var readIndex =
                command.IndexOf("IFS= read -r command", StringComparison.Ordinal);
            var triggerGuardIndex =
                command.IndexOf("if [ \"$command\" = \"KILL\" ]; then", StringComparison.Ordinal);
            var killIndex =
                command.IndexOf("kill -9 \"$pid\"", StringComparison.Ordinal);

            Assert.Contains("pid=4321;", command);
            Assert.True(readIndex >= 0);
            Assert.True(triggerGuardIndex > readIndex);
            Assert.True(killIndex > triggerGuardIndex);
            Assert.Contains("/proc/$pid/stat", command);
            Assert.Contains("READY PID=%s STARTTIME=%s STATE=%s", command);
            Assert.Contains("KILL_FAIL=PID_REUSED_BEFORE_TRIGGER", command);
            Assert.Contains("DEAD PID=%s STARTTIME=%s PROOF=PROC_ABSENT", command);
            Assert.Contains("PROOF=ZOMBIE", command);
            Assert.Contains("PROOF=PID_REUSED", command);
            Assert.Contains("KILL_FAIL=EXACT_PROCESS_STILL_ALIVE", command);
            Assert.Contains("CANCELLED", command);
        }


        [Theory]
        [InlineData("KILL", "KILL\n")]
        [InlineData("CANCEL", "CANCEL\n")]
        public void PrearmedControlFrame_Should_Use_LfOnly_For_Windows_To_Linux_Stdin(
            string command,
            string expectedFrame)
        {
            var frame =
                KubernetesRuntimePoolProductionInfrastructure
                    .CreatePrearmedRuntimeProcessControlFrame(command);

            Assert.Equal(expectedFrame, frame);
            Assert.Equal(-1, frame.IndexOf('\r'));
            Assert.Equal('\n', frame[^1]);
        }


        [Fact]
        public void ReadyMarker_Should_Capture_Exact_Linux_Process_Incarnation()
        {
            var identity =
                KubernetesRuntimePoolProductionInfrastructure
                    .ParsePrearmedRuntimeProcessReadyMarker(
                        "READY PID=4321 STARTTIME=987654 STATE=S",
                        4321);

            Assert.Equal(4321, identity.ProcessId);
            Assert.Equal(987654, identity.StartTimeTicks);
            Assert.Equal("S", identity.State);
        }

        [Theory]
        [InlineData("KILL_EXIT=0\nDEAD PID=4321 STARTTIME=987654 PROOF=PROC_ABSENT", "PROC_ABSENT")]
        [InlineData("KILL_EXIT=0\nDEAD PID=4321 STARTTIME=987654 PROOF=ZOMBIE STATE=Z", "ZOMBIE")]
        [InlineData("KILL_EXIT=0\nDEAD PID=4321 STARTTIME=987654 PROOF=PID_REUSED OBSERVED_STARTTIME=987700 STATE=S", "PID_REUSED")]
        public void DeathMarker_Should_Prove_The_Armed_Linux_Process_Incarnation_Is_Gone(
            string standardOutput,
            string expectedProof)
        {
            var proof =
                KubernetesRuntimePoolProductionInfrastructure
                    .ParsePrearmedRuntimeProcessDeathMarker(
                        standardOutput,
                        4321,
                        987654);

            Assert.Equal(4321, proof.ProcessId);
            Assert.Equal(987654, proof.StartTimeTicks);
            Assert.Equal(expectedProof, proof.Proof);
        }

        [Fact]
        public void DeathMarker_Should_Reject_A_Different_Process_Incarnation()
        {
            Assert.Throws<InvalidOperationException>(() =>
                KubernetesRuntimePoolProductionInfrastructure
                    .ParsePrearmedRuntimeProcessDeathMarker(
                        "KILL_EXIT=0\nDEAD PID=4321 STARTTIME=111111 PROOF=PROC_ABSENT",
                        4321,
                        987654));
        }

        [Theory]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.Running)]
        [InlineData(AiStepExecutionStatus.WaitingForRetry)]
        public void PhysicalKillBoundaryPolicy_Should_Accept_Only_NonTerminal_PostSchedule_CallSite(
            AiStepExecutionStatus status)
        {
            var callSite =
                new AiStepState
                {
                    Version = 3,
                    Status = status
                };

            Assert.True(
                KubernetesRuntimePoolContinuationConsumePhysicalKillPolicy
                    .IsBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: true,
                        parentIsTerminal: false,
                        scheduledStepVersion: 2,
                        callSite));
        }

        [Fact]
        public void PhysicalKillBoundaryPolicy_Should_Reject_Terminal_Parent()
        {
            var callSite =
                new AiStepState
                {
                    Version = 3,
                    Status = AiStepExecutionStatus.Ready
                };

            Assert.False(
                KubernetesRuntimePoolContinuationConsumePhysicalKillPolicy
                    .IsBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: true,
                        parentIsTerminal: true,
                        scheduledStepVersion: 2,
                        callSite));
        }

        [Fact]
        public void PhysicalKillBoundaryPolicy_Should_Reject_Terminal_CallSite()
        {
            var callSite =
                new AiStepState
                {
                    Version = 3,
                    Status = AiStepExecutionStatus.Completed
                };

            Assert.False(
                KubernetesRuntimePoolContinuationConsumePhysicalKillPolicy
                    .IsBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: true,
                        parentIsTerminal: false,
                        scheduledStepVersion: 2,
                        callSite));
        }

        [Fact]
        public void PhysicalKillBoundaryPolicy_Should_Reject_No_PostSchedule_Progress()
        {
            var callSite =
                new AiStepState
                {
                    Version = 2,
                    Status = AiStepExecutionStatus.Running
                };

            Assert.False(
                KubernetesRuntimePoolContinuationConsumePhysicalKillPolicy
                    .IsBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: true,
                        parentIsTerminal: false,
                        scheduledStepVersion: 2,
                        callSite));
        }
    }
}
