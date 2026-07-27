using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Provides configuration for real operating-system runtime pool child processes.
    /// </summary>
    /// <remarks>
    /// These options belong exclusively to the opt-in process-host Runtime Pool Manager. They do
    /// not modify the existing process host creation strategy.
    /// </remarks>
    public sealed class AiRuntimeProcessPoolChildProcessOptions
    {
        /// <summary>
        /// Gets or sets the executable used to start each runtime child process.
        /// </summary>
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the ordered executable arguments.
        /// </summary>
        /// <remarks>
        /// Arguments are passed through <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
        /// and are not concatenated into a shell command by the production factory.
        /// </remarks>
        public List<string> Arguments { get; set; } = new();

        /// <summary>
        /// Gets or sets the optional process working directory.
        /// </summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>
        /// Gets or sets optional child-process environment variables.
        /// </summary>
        /// <remarks>
        /// The authoritative pool, host, runtime instance, and child ordinal variables are applied
        /// after this dictionary and cannot be overridden by it.
        /// </remarks>
        public Dictionary<string, string> EnvironmentVariables { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets a value indicating whether standard output and standard error are
        /// redirected and drained asynchronously.
        /// </summary>
        public bool RedirectOutput { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the child process should be created without a
        /// window.
        /// </summary>
        public bool CreateNoWindow { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether stopping a child should terminate its entire
        /// process tree.
        /// </summary>
        public bool KillEntireProcessTreeOnStop { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of seconds allowed for a stopped child to complete.
        /// </summary>
        public int StopTimeoutSeconds { get; set; } = 10;
    }
}
