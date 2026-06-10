namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity
{
    /// <summary>
    /// Defines the unique identity of the control-plane host currently orchestrating runtime instances.
    ///
    /// In MCP / Kubernetes scenarios, this identifies the control-plane process, pod,
    /// or host responsible for submitting, admitting, draining, and dispatching shared runs.
    /// </summary>
    public interface IAiControlPlaneHostIdentity
    {
        /// <summary>
        /// Gets the unique control-plane host identifier.
        /// </summary>
        string ControlPlaneHostId { get; }
    }
}