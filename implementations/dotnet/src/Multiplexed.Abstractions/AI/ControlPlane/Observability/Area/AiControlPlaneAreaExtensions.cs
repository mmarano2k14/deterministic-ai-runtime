namespace Multiplexed.Abstractions.AI.ControlPlane.Observability.Area
{
    /// <summary>
    /// Provides stable export names for control-plane areas.
    /// </summary>
    public static class AiControlPlaneAreaExtensions
    {
        /// <summary>
        /// Converts the control-plane area to a stable kebab-case name for
        /// logs, metrics, tracing, ledger events, Kibana, OpenSearch, and Grafana labels.
        /// </summary>
        public static string ToStableName(this AiControlPlaneArea area)
        {
            return area switch
            {
                AiControlPlaneArea.Replay => "replay",
                AiControlPlaneArea.ExecutionControl => "execution-control",
                AiControlPlaneArea.RunControl => "run-control",
                AiControlPlaneArea.InstanceRegistry => "instance-registry",
                AiControlPlaneArea.Admission => "admission",
                AiControlPlaneArea.SharedQueue => "shared-queue",
                AiControlPlaneArea.SharedController => "shared-controller",
                AiControlPlaneArea.Scaling => "scaling",
                AiControlPlaneArea.Recovery => "recovery",
                AiControlPlaneArea.ChildDag => "child-dag",
                AiControlPlaneArea.Policy => "policy",

                _ => throw new ArgumentOutOfRangeException(
                    nameof(area),
                    area,
                    "Unsupported control-plane area.")
            };
        }
    }
}