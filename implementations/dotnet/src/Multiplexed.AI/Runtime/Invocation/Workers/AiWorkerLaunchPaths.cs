using System.Security.Cryptography;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>
    /// Checked paths, not a hostile-code sandbox. Rejects aliases and links at validation time;
    /// deployment ownership is still required against concurrent directory replacement and hard links.
    /// </summary>
    public static class AiWorkerLaunchPaths
    {
        public static void ValidateCodePaths(AiWorkerCodeBundle code)
        {
            ArgumentNullException.ThrowIfNull(code);
            ValidateFiles(code.Sources);
            AiPublicationJson.Path(code.EntryPointPath);
            if (!code.Sources.Any(f => f.Path == code.EntryPointPath))
                throw new InvalidOperationException("The entry point is outside the validated source closure.");
            ArgumentNullException.ThrowIfNull(code.Dependencies);
            foreach (var dependency in code.Dependencies)
            {
                ArgumentNullException.ThrowIfNull(dependency);
                ValidateFiles(dependency.Files);
            }
        }

        private static void ValidateFiles(IReadOnlyList<AiWorkerFile> files)
        {
            ArgumentNullException.ThrowIfNull(files);
            if (files.Count is < 1 or > 4096) throw new InvalidOperationException("A bounded nonempty source closure is required.");
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                ArgumentNullException.ThrowIfNull(file); AiPublicationJson.Path(file.Path);
                if (!complete.Add(file.Path)) throw new InvalidOperationException("Duplicate or case-colliding execution file paths.");
                var segments = file.Path.Split('/'); var prefix = string.Empty;
                foreach (var segment in segments)
                {
                    prefix = prefix.Length == 0 ? segment : prefix + "/" + segment;
                    if (paths.TryGetValue(prefix, out var original) && original != prefix)
                        throw new InvalidOperationException("Case-colliding execution directory paths.");
                    paths[prefix] = prefix;
                }
            }
            foreach (var path in complete)
            {
                var separator = path.IndexOf('/');
                while (separator >= 0)
                {
                    if (complete.Contains(path[..separator])) throw new InvalidOperationException("An execution file is also used as a directory.");
                    separator = path.IndexOf('/', separator + 1);
                }
            }
        }

        public static void ValidateProfile(AiWorkerProcessProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (profile.ExecutionDescriptor is null) return; // Explicitly legacy deployment-controlled path.
            if (profile.ApprovedLaunchRoots.Count == 0) throw new InvalidOperationException("Missing approved execution roots.");
            var roots = profile.ApprovedLaunchRoots.Select(root => ValidateHostPath(root, directory: true)).ToArray();
            foreach (var root in roots)
                if (string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("An entire filesystem or drive is not an approved execution root.");

            void Within(string path, bool directory)
            {
                var full = ValidateHostPath(path, directory);
                if (!roots.Any(root => Contains(root, full)))
                    throw new InvalidOperationException("Worker launch path is outside its approved roots.");
            }
            Within(profile.ExecutablePath, false); Within(profile.WorkingDirectory, true);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(profile.ExecutablePath) };
            foreach (var file in profile.VerifiedHostFiles)
            {
                var full = Path.GetFullPath(file.Key);
                if (!names.Add(full) && !(full == Path.GetFullPath(profile.ExecutablePath) && file.Value == profile.ExecutableSha256))
                    throw new InvalidOperationException("Case-colliding or inconsistent host launch file aliases.");
                Within(file.Key, false);
            }
        }

        private static bool Contains(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            return !Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static string ValidateHostPath(string path, bool directory)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidOperationException("A fully qualified deployment-owned execution path is required.");
            var pathRoot = Path.GetPathRoot(path)!;
            var declaredParts = path[pathRoot.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (declaredParts.Any(part => part is "." or ".."))
                throw new InvalidOperationException("Dot segments are not allowed in approved launch-path declarations.");
            if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\", StringComparison.Ordinal) ||
                declaredParts.Any(part => part.Contains(':') || part.EndsWith('.') || part.EndsWith(' '))))
                throw new InvalidOperationException("Device, network, alternate-stream and trailing-alias launch paths are not supported.");
            var full = Path.GetFullPath(path);
            var current = Path.GetPathRoot(full)!;
            Check(current, true);
            var parts = full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                current = Path.Combine(current, parts[index]);
                Check(current, index < parts.Length - 1 || directory);
            }
            return full;
        }

        private static void Check(string path, bool directory)
        {
            var attributes = File.GetAttributes(path); // Missing/inaccessible paths fail closed.
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symbolic links and reparse points are not accepted in approved launch paths.");
            FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is not null)
                throw new InvalidOperationException("A linked launch path is not accepted.");
            if (((attributes & FileAttributes.Directory) != 0) != directory)
                throw new InvalidOperationException("An execution path has an unexpected file type.");
        }
    }

    /// <summary>
    /// Keeps verified launch-file read handles for the exchange. This reduces replacement exposure
    /// on systems honoring file sharing; it does not seal a POSIX workspace or mutable ancestors.
    /// </summary>
    internal sealed class AiWorkerVerifiedLaunchFiles : IAsyncDisposable
    {
        private readonly List<FileStream> _files = new();
        internal static async Task<AiWorkerVerifiedLaunchFiles> OpenAsync(AiWorkerProcessProfile profile, CancellationToken token)
        {
            var result = new AiWorkerVerifiedLaunchFiles();
            try
            {
                AiWorkerLaunchPaths.ValidateProfile(profile);
                // Preserve the historical executable-first verification order.
                var files = new Dictionary<string, string>(StringComparer.Ordinal)
                    { [profile.ExecutablePath] = profile.ExecutableSha256 };
                foreach (var declared in profile.VerifiedHostFiles)
                {
                    if (files.TryGetValue(declared.Key, out var expected) && expected != declared.Value)
                        throw new InvalidOperationException("Conflicting approved executable digests.");
                    files[declared.Key] = declared.Value;
                }
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var stream = new FileStream(file.Key, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    result._files.Add(stream);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
                    if (hash != file.Value) throw new InvalidOperationException("An installed worker host file differs from its approved digest.");
                    if (profile.ExecutionDescriptor is null)
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                        result._files.Remove(stream);
                    }
                }
                AiWorkerLaunchPaths.ValidateProfile(profile);
                return result;
            }
            catch { await result.DisposeAsync().ConfigureAwait(false); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var file in _files) await file.DisposeAsync().ConfigureAwait(false);
            _files.Clear();
        }
    }
}
