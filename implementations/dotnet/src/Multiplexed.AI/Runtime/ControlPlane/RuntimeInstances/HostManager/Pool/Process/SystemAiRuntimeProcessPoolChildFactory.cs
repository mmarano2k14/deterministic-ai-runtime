using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Starts real operating-system runtime child processes for a process-host runtime pool.
    /// </summary>
    public sealed class SystemAiRuntimeProcessPoolChildFactory :
        IAiRuntimeProcessPoolChildFactory
    {
        private readonly AiRuntimeProcessPoolChildProcessOptions options;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="SystemAiRuntimeProcessPoolChildFactory"/> class.
        /// </summary>
        /// <param name="options">The child-process options.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the child-process options are invalid.
        /// </exception>
        public SystemAiRuntimeProcessPoolChildFactory(
            AiRuntimeProcessPoolChildProcessOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            this.options = CopyOptions(options);
            AiRuntimeProcessPoolChildProcessOptionsValidator.Validate(this.options);
        }

        /// <inheritdoc />
        public Task<IAiRuntimeProcessPoolChild> StartAsync(
            AiRuntimeProcessPoolChildStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = this.CreateStartInfo(request);
            var process = new System.Diagnostics.Process
            {
                StartInfo = startInfo
            };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        $"The runtime pool child process '{startInfo.FileName}' did not start.");
                }

                IAiRuntimeProcessPoolChild child =
                    new SystemAiRuntimeProcessPoolChild(
                        process,
                        request,
                        this.options.RedirectOutput,
                        this.options.KillEntireProcessTreeOnStop,
                        TimeSpan.FromSeconds(this.options.StopTimeoutSeconds));

                return Task.FromResult(child);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Creates a process start description with authoritative identity variables.
        /// </summary>
        /// <param name="request">The typed child-process start request.</param>
        /// <returns>The process start description.</returns>
        private ProcessStartInfo CreateStartInfo(
            AiRuntimeProcessPoolChildStartRequest request)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = this.options.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = this.options.CreateNoWindow,
                RedirectStandardOutput = this.options.RedirectOutput,
                RedirectStandardError = this.options.RedirectOutput
            };

            if (!string.IsNullOrWhiteSpace(this.options.WorkingDirectory))
            {
                startInfo.WorkingDirectory = this.options.WorkingDirectory;
            }

            foreach (var argument in this.options.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var pair in this.options.EnvironmentVariables)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            startInfo.Environment[AiRuntimeProcessPoolChildEnvironment.PoolId] =
                request.PoolId;

            startInfo.Environment[AiRuntimeProcessPoolChildEnvironment.HostId] =
                request.HostId;

            startInfo.Environment[
                AiRuntimeProcessPoolChildEnvironment.RuntimeInstanceId] =
                request.RuntimeInstanceId;

            startInfo.Environment[
                AiRuntimeProcessPoolChildEnvironment.ProcessOrdinal] =
                request.Ordinal.ToString(CultureInfo.InvariantCulture);

            return startInfo;
        }

        /// <summary>
        /// Validates the authoritative child-process start request.
        /// </summary>
        /// <param name="request">The child-process start request.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when an authoritative identity is missing.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the child ordinal is invalid.
        /// </exception>
        private static void ValidateRequest(
            AiRuntimeProcessPoolChildStartRequest request)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);

            if (request.Ordinal <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.Ordinal,
                    "The runtime pool child ordinal must be greater than zero.");
            }
        }

        /// <summary>
        /// Copies mutable process options so caller mutation cannot alter running factory behavior.
        /// </summary>
        /// <param name="options">The source options.</param>
        /// <returns>An isolated options copy.</returns>
        private static AiRuntimeProcessPoolChildProcessOptions CopyOptions(
            AiRuntimeProcessPoolChildProcessOptions options)
        {
            return new AiRuntimeProcessPoolChildProcessOptions
            {
                ExecutablePath = options.ExecutablePath,
                Arguments = options.Arguments is null
                    ? null!
                    : new(options.Arguments),
                WorkingDirectory = options.WorkingDirectory,
                EnvironmentVariables = options.EnvironmentVariables is null
                    ? null!
                    : new(
                        options.EnvironmentVariables,
                        StringComparer.OrdinalIgnoreCase),
                RedirectOutput = options.RedirectOutput,
                CreateNoWindow = options.CreateNoWindow,
                KillEntireProcessTreeOnStop =
                    options.KillEntireProcessTreeOnStop,
                StopTimeoutSeconds = options.StopTimeoutSeconds
            };
        }
    }
}
