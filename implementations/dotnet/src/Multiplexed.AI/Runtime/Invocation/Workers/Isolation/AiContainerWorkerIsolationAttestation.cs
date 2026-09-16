using System.Globalization;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Isolation
{
    /// <summary>
    /// Validates the running container state reported by the configured engine before tenant
    /// request bytes are released to the worker. Engine configuration is trusted infrastructure,
    /// but a successful launch is not treated as proof that requested isolation was applied.
    /// </summary>
    public static class AiContainerWorkerIsolationAttestation
    {
        public static void Validate(string json, AiContainerWorkerProfile profile)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Container inspection JSON is required.", nameof(json));
            ArgumentNullException.ThrowIfNull(profile);
            profile.ResourceLimits.Validate();

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
                throw new InvalidOperationException("Container inspection must return exactly one running container.");

            var container = document.RootElement[0];
            var config = RequireObject(container, "Config");
            var host = RequireObject(container, "HostConfig");

            RequireEqual(RequireString(config, "Image"), profile.ImageReference,
                "Container image does not match the pinned immutable OCI manifest.");
            RequireEqual(RequireString(config, "User"), profile.ContainerUser,
                "Container user does not match the configured non-root identity.");

            if (!RequireBoolean(host, "ReadonlyRootfs"))
                throw new InvalidOperationException("Container root filesystem is not read-only.");
            if (RequireBoolean(host, "Privileged"))
                throw new InvalidOperationException("Privileged container execution is not permitted.");
            if (!RequireBoolean(host, "AutoRemove"))
                throw new InvalidOperationException("Container automatic cleanup was not applied.");

            RequireEqual(RequireString(host, "NetworkMode"), "none",
                "Container network mode does not enforce denied egress.");

            var limits = profile.ResourceLimits;
            RequireEqual(RequireInt64(host, "Memory"), limits.MemoryBytes,
                "Container memory limit does not match the server-owned profile.");
            RequireEqual(RequireInt64(host, "MemorySwap"), limits.MemoryBytes,
                "Container memory+swap limit does not match the server-owned profile.");
            RequireEqual(RequireInt64(host, "NanoCpus"), checked((long)limits.CpuMilliCores * 1_000_000L),
                "Container CPU quota does not match the server-owned profile.");
            RequireEqual(RequireInt64(host, "PidsLimit"), (long)limits.PidsLimit,
                "Container process-count limit does not match the server-owned profile.");

            RequireEmptyArrayOrNull(host, "Binds", "Host bind mounts are not permitted for isolated workers.");
            RequireEmptyArrayOrNull(host, "CapAdd", "Additional Linux capabilities are not permitted.");
            RequireArrayContains(host, "CapDrop", "ALL", StringComparison.OrdinalIgnoreCase,
                "The container did not drop all Linux capabilities.");
            RequireArrayContains(host, "SecurityOpt", "no-new-privileges", StringComparison.OrdinalIgnoreCase,
                "The container did not apply no-new-privileges.", allowPrefix: true);

            ValidateTmpfs(host, limits.WritableWorkspaceBytes);
            ValidateMounts(container);
        }

        private static void ValidateTmpfs(JsonElement host, long expectedBytes)
        {
            if (!host.TryGetProperty("Tmpfs", out var tmpfs) || tmpfs.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("A bounded writable /tmp tmpfs is required.");

            var properties = tmpfs.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "/tmp" || properties[0].Value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("The isolated worker may expose only the bounded /tmp tmpfs.");

            var tokens = (properties[0].Value.GetString() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var set = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
            var expectedSize = "size=" + expectedBytes.ToString(CultureInfo.InvariantCulture);
            foreach (var required in new[] { "noexec", "nosuid", "nodev", expectedSize })
                if (!set.Contains(required))
                    throw new InvalidOperationException("The /tmp tmpfs does not match the sealed-closure workspace contract.");
            if (set.Contains("exec") || set.Contains("suid") || set.Contains("dev") ||
                tokens.Count(token => token.StartsWith("size=", StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("The /tmp tmpfs contains a conflicting writable-workspace option.");
        }

        private static void ValidateMounts(JsonElement container)
        {
            if (!container.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind == JsonValueKind.Null)
                return;
            if (mounts.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Container mount inspection has an unexpected shape.");

            foreach (var mount in mounts.EnumerateArray())
            {
                if (mount.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Container mount inspection has an unexpected shape.");
                var type = RequireString(mount, "Type");
                var destination = RequireString(mount, "Destination");
                if (!string.Equals(type, "tmpfs", StringComparison.OrdinalIgnoreCase) || destination != "/tmp")
                    throw new InvalidOperationException("Host or persistent mounts are not permitted for isolated workers.");
            }
        }

        private static JsonElement RequireObject(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Container inspection is missing object '{name}'.");
            return value;
        }

        private static string RequireString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"Container inspection is missing string '{name}'.");
            return value.GetString() ?? string.Empty;
        }

        private static bool RequireBoolean(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException($"Container inspection is missing boolean '{name}'.");
            return value.GetBoolean();
        }

        private static long RequireInt64(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
                throw new InvalidOperationException($"Container inspection is missing integer '{name}'.");
            return result;
        }

        private static void RequireEmptyArrayOrNull(JsonElement parent, string name, string message)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                return;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0)
                throw new InvalidOperationException(message);
        }

        private static void RequireArrayContains(
            JsonElement parent,
            string name,
            string expected,
            StringComparison comparison,
            string message,
            bool allowPrefix = false)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(message);
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var text = item.GetString() ?? string.Empty;
                if (string.Equals(text, expected, comparison) ||
                    (allowPrefix && text.StartsWith(expected + ":", comparison)))
                    return;
            }
            throw new InvalidOperationException(message);
        }

        private static void RequireEqual<T>(T actual, T expected, string message) where T : IEquatable<T>
        {
            if (!actual.Equals(expected)) throw new InvalidOperationException(message);
        }
    }
}
