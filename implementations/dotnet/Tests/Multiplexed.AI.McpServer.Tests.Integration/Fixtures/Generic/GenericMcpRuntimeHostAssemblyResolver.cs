using System;
using System.IO;
using System.Linq;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Resolves the real MCP server host assembly used by process-based runtime host scenarios.
    /// </summary>
    public static class GenericMcpRuntimeHostAssemblyResolver
    {
        /// <summary>
        /// The MCP server host assembly file name.
        /// </summary>
        private const string RuntimeHostAssemblyFileName = "Multiplexed.AI.McpServer.Host.dll";

        /// <summary>
        /// Resolves the real MCP server host assembly path.
        /// </summary>
        /// <returns>The resolved MCP server host assembly path.</returns>
        public static string ResolveRuntimeHostAssemblyPath()
        {
            var baseDirectory = AppContext.BaseDirectory;
            var directCandidate = Path.Combine(baseDirectory, RuntimeHostAssemblyFileName);

            if (IsValidRuntimeHostAssemblyPath(directCandidate))
            {
                return directCandidate;
            }

            var repositoryRoot = FindRepositoryRoot(baseDirectory);

            var candidates =
                Directory
                    .EnumerateFiles(repositoryRoot, RuntimeHostAssemblyFileName, SearchOption.AllDirectories)
                    .Where(IsValidRuntimeHostAssemblyPath)
                    .OrderByDescending(path => path.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(File.GetLastWriteTimeUtc)
                    .ToArray();

            var resolved =
                candidates.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            throw new FileNotFoundException(
                $"Could not resolve real MCP runtime host assembly '{RuntimeHostAssemblyFileName}' from base directory '{baseDirectory}'. Build the Multiplexed.AI.McpServer.Host project first.");
        }

        /// <summary>
        /// Determines whether a candidate path points to the real MCP server host assembly.
        /// </summary>
        /// <param name="path">The candidate path.</param>
        /// <returns><c>true</c> when the candidate points to the real MCP server host assembly; otherwise, <c>false</c>.</returns>
        private static bool IsValidRuntimeHostAssemblyPath(
            string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            if (!path.EndsWith(RuntimeHostAssemblyFileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!path.Contains($"{Path.DirectorySeparatorChar}Multiplexed.AI.McpServer.Host{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (path.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Finds the repository root by walking up from a start directory.
        /// </summary>
        /// <param name="startDirectory">The start directory.</param>
        /// <returns>The repository root directory.</returns>
        private static string FindRepositoryRoot(
            string startDirectory)
        {
            var directory =
                new DirectoryInfo(startDirectory);

            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    Directory.Exists(Path.Combine(directory.FullName, "implementations")))
                {
                    return directory.FullName;
                }

                directory =
                    directory.Parent;
            }

            return startDirectory;
        }
    }
}