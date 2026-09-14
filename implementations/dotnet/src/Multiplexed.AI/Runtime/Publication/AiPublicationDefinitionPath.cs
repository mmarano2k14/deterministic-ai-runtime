using System.Text.Json.Nodes;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Canonical addressing for exact inline Child DAG definitions inside one immutable publication.
    /// </summary>
    internal static class AiPublicationDefinitionPath
    {
        internal static string Append(string? parentPath, string childDagStepName)
        {
            AiPublicationJson.Text(childDagStepName, "ChildDagStepName");
            var segment = childDagStepName.Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal);
            return string.Concat(parentPath, "/", segment);
        }

        internal static void Validate(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 32768 || path[0] != '/')
                throw new InvalidOperationException("Invalid publication definition path.");

            var segments = path[1..].Split('/');
            if (segments.Length is < 1 or > 16 || segments.Any(segment => segment.Length == 0))
                throw new InvalidOperationException("Invalid publication definition path.");

            foreach (var segment in segments)
            {
                var decoded = DecodeSegment(segment);
                AiPublicationJson.Text(decoded, "DefinitionPathSegment");
                var canonical = decoded.Replace("~", "~0", StringComparison.Ordinal)
                    .Replace("/", "~1", StringComparison.Ordinal);
                if (!string.Equals(segment, canonical, StringComparison.Ordinal))
                    throw new InvalidOperationException("Publication definition paths must use canonical JSON-Pointer escaping.");
            }
        }

        internal static AiPipelineDefinition Resolve(AiPipelineDefinition root, string path)
        {
            ArgumentNullException.ThrowIfNull(root);
            Validate(path);

            var current = JsonNode.Parse(AiPublicationJson.Serialize(root))?.AsObject()
                ?? throw new InvalidOperationException("Published pipeline definition is unavailable.");

            foreach (var encodedSegment in path[1..].Split('/'))
            {
                var stepName = DecodeSegment(encodedSegment);
                var steps = Get(current, "Steps") as JsonArray
                    ?? throw new InvalidOperationException("Published Child DAG path does not reference a DAG step collection.");
                var matches = steps
                    .OfType<JsonObject>()
                    .Where(step => string.Equals(Get(step, "Name")?.GetValue<string>(), stepName, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException("Published Child DAG path does not resolve to exactly one parent call site.");

                var declaration = AiPublicationJson.Read<AiPipelineStepDefinition>(matches[0].ToJsonString());
                if (!string.Equals(declaration.StepKey, ExecuteChildDagStep.StepKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("Published Child DAG path references a non-child-DAG call site.");

                var config = Get(matches[0], "Config") as JsonObject;
                current = config?[ExecuteChildDagStep.ChildDagDefinitionConfigKey] as JsonObject
                    ?? throw new InvalidOperationException("Published Child DAG path references missing immutable child definition material.");
            }

            var definition = AiPublicationJson.Read<AiPipelineDefinition>(current.ToJsonString());
            if (definition.ExecutionMode != Multiplexed.Abstractions.AI.Execution.AiExecutionMode.Dag)
                throw new InvalidOperationException("Published Child DAG path resolved to a non-DAG definition.");
            AiPublicationJson.Text(definition.Name, "ChildPipelineName");
            AiPublicationJson.Text(definition.Version, "ChildPipelineVersion");
            return definition;
        }

        internal static bool IsSameOrDescendant(string? candidate, string path) =>
            candidate is not null &&
            (string.Equals(candidate, path, StringComparison.Ordinal) ||
             candidate.StartsWith(path + "/", StringComparison.Ordinal));

        private static string DecodeSegment(string segment)
        {
            var value = new System.Text.StringBuilder(segment.Length);
            for (var index = 0; index < segment.Length; index++)
            {
                var current = segment[index];
                if (current != '~')
                {
                    value.Append(current);
                    continue;
                }

                if (++index >= segment.Length)
                    throw new InvalidOperationException("Publication definition paths must use canonical JSON-Pointer escaping.");
                value.Append(segment[index] switch
                {
                    '0' => '~',
                    '1' => '/',
                    _ => throw new InvalidOperationException("Publication definition paths must use canonical JSON-Pointer escaping.")
                });
            }

            return value.ToString();
        }

        private static JsonNode? Get(JsonObject value, string name)
        {
            var matches = value.Where(property => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException("Ambiguous casing in a publication declaration.");
            return matches.Length == 0 ? null : matches[0].Value;
        }
    }
}
