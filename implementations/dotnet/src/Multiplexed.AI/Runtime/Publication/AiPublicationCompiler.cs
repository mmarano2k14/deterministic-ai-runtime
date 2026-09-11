using System.Text.Json;
using System.Text.Json.Nodes;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Freezes caller-owned data and compiles content-addressed code bindings. It performs
    /// no I/O, evaluates no policy and does not create a worker or a second execution plan.
    /// </summary>
    internal static class AiPublicationCompiler
    {
        internal sealed record Captured(string DefinitionJson, IReadOnlyList<AiPublicationFunctionUpload> Functions);
        internal sealed record Write(AiPublicationDocument Document, string Kind, string Json);
        internal sealed record Compilation(AiPipelineDefinition Definition, AiPipelinePublication Publication, IReadOnlyList<Write> Writes);
        internal sealed record Slot(AiPublicationCallSite Site, string LogicalName, string Language, JsonObject Invocation);

        // Copy before the first await. Later mutations of the caller's files/config cannot alter the publication.
        internal static Captured Capture(AiPipelinePublicationUpload upload, AiPublicationOptions options)
        {
            ArgumentNullException.ThrowIfNull(upload);
            ArgumentNullException.ThrowIfNull(upload.Definition);
            ArgumentNullException.ThrowIfNull(upload.Functions);
            var definition = AiPublicationJson.Serialize(upload.Definition);
            AiPublicationJson.ValidateJson(definition, AiPublicationJson.MaxManifestBytes);
            if (upload.Functions.Count > options.MaxFunctions) throw new InvalidOperationException("Too many published functions.");
            long bytes = 0; var fileCount = 0;
            AiPublicationFileUpload[] CopyFiles(IReadOnlyList<AiPublicationFileUpload> files)
            {
                ArgumentNullException.ThrowIfNull(files);
                if (files.Count == 0 || files.Count > options.MaxFiles) throw new InvalidOperationException("A nonempty bounded file list is required.");
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var copied = new List<AiPublicationFileUpload>(files.Count);
                foreach (var file in files)
                {
                    ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(file.Content);
                    AiPublicationJson.Path(file.Path);
                    if (!paths.Add(file.Path)) throw new InvalidOperationException("Duplicate or case-colliding publication file paths.");
                    if (file.Content.Length > options.MaxFileBytes || (bytes += file.Content.Length) > options.MaxTotalBytes ||
                        ++fileCount > options.MaxFiles) throw new InvalidOperationException("Publication file size/count limit exceeded.");
                    copied.Add(new AiPublicationFileUpload(file.Path, file.Content.ToArray()));
                }
                return copied.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
            }
            var functions = new List<AiPublicationFunctionUpload>();
            var sites = new HashSet<AiPublicationCallSite>();
            foreach (var function in upload.Functions)
            {
                ArgumentNullException.ThrowIfNull(function); ValidateSite(function.Site);
                if (!sites.Add(function.Site)) throw new InvalidOperationException("Duplicate function declaration site.");
                AiPublicationJson.Text(function.EnvironmentRef, "EnvironmentRef");
                AiPublicationJson.Path(function.EntryPointPath);
                AiPublicationJson.Text(function.EntryPointSymbol, "EntryPointSymbol");
                ValidateEntryPointSymbol(function.EntryPointSymbol);
                var sources = CopyFiles(function.Sources);
                if (!sources.Any(f => f.Path == function.EntryPointPath)) throw new InvalidOperationException("Entry-point file is missing from sources.");
                ArgumentNullException.ThrowIfNull(function.Dependencies);
                if (function.Dependencies.Count > 64) throw new InvalidOperationException("Too many dependency declarations.");
                var dependencies = new List<AiPublicationDependencyUpload>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dependency in function.Dependencies)
                {
                    ArgumentNullException.ThrowIfNull(dependency);
                    AiPublicationJson.Text(dependency.Name, "DependencyName");
                    if (dependency.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or '@' or '/')) ||
                        dependency.Name.Contains("..", StringComparison.Ordinal) || !names.Add(dependency.Name))
                        throw new InvalidOperationException("Invalid or duplicate dependency name.");
                    AiPublicationJson.Version(dependency.Version);
                    dependencies.Add(dependency with { Files = CopyFiles(dependency.Files) });
                }
                functions.Add(function with { Sources = sources,
                    Dependencies = dependencies.OrderBy(d => d.Name, StringComparer.Ordinal).ToArray() });
            }
            return new Captured(definition, functions.ToArray());
        }

        internal static Compilation Compile(Captured captured, AiPublicationPartition partition, IAiPublicationEnvironmentCatalog catalog)
        {
            var root = JsonNode.Parse(captured.DefinitionJson)!.AsObject();
            // Temporary references only make the existing metadata resolver usable. They never escape compilation.
            var slots = FindSlots(root, fillTemporaryReferences: true);
            if (slots.Count != captured.Functions.Count) throw new InvalidOperationException("Every custom declaration needs exactly one attached code definition.");
            var uploads = captured.Functions.ToDictionary(f => f.Site);
            var writes = new Dictionary<string, Write>(StringComparer.Ordinal);
            AiPublicationDocument Add(string kind, object value)
            {
                var json = AiPublicationJson.Serialize(value); var hash = AiPublicationJson.Hash(json);
                var descriptor = new AiPublicationDocument(AiPublicationJson.Key(partition, kind, hash), hash, AiPublicationJson.Utf8.GetByteCount(json));
                writes.TryAdd(descriptor.Key, new Write(descriptor, kind, json)); return descriptor;
            }
            AiPublicationFile File(AiPublicationFileUpload file)
            {
                var hash = AiPublicationJson.HashBytes(file.Content);
                var envelope = Add("file", new FileEnvelope(1, hash, file.Content.Length, AiPublicationJson.EncodeBytes(file.Content)));
                return new AiPublicationFile(file.Path, hash, file.Content.Length, envelope);
            }
            var functions = new List<AiPublicationFunction>();
            foreach (var slot in slots)
            {
                if (!uploads.TryGetValue(slot.Site, out var source)) throw new InvalidOperationException("Missing code for a custom declaration.");
                var runtime = catalog.Find(source.EnvironmentRef) ?? throw new InvalidOperationException("The exact host environment is unavailable.");
                AiPublicationJson.ValidateEnvironment(runtime);
                if (runtime.Reference != source.EnvironmentRef || runtime.ExecutionLanguage != slot.Language)
                    throw new InvalidOperationException("Host environment does not match the declared reference and effective language.");
                var dependencies = source.Dependencies.Select(d => new AiPublicationDependency(d.Name, d.Version, d.Files.Select(File).ToArray())).ToArray();
                var environment = Add("environment", new AiPublicationEnvironmentSnapshot(1, runtime, dependencies));
                var implementation = Add("implementation", new AiPublicationImplementation(1, slot.Language,
                    source.EntryPointPath, source.EntryPointSymbol, source.Sources.Select(File).ToArray(), environment));
                var reference = "impl-" + implementation.Sha256;
                slot.Invocation["implementationRef"] = reference;
                functions.Add(new AiPublicationFunction(slot.Site, slot.LogicalName, slot.Language, reference, implementation, environment));
            }
            var definition = AiPublicationJson.Read<AiPipelineDefinition>(root.ToJsonString());
            var definitionDocument = Add("definition", definition);
            var manifest = new AiPipelinePublicationManifest(1, partition, definition.Name, definition.Version!, definitionDocument,
                functions.OrderBy(f => AiPublicationJson.Serialize(f.Site), StringComparer.Ordinal).ToArray());
            var manifestJson = AiPublicationJson.Serialize(manifest);
            AiPublicationJson.ValidateJson(manifestJson, AiPublicationJson.MaxManifestBytes);
            var manifestDocument = Add("manifest", manifest);
            return new Compilation(definition, new AiPipelinePublication("pub-" + manifestDocument.Sha256, manifestDocument.Sha256, manifest),
                writes.Values.Where(w => w.Kind != "manifest").Append(writes[manifestDocument.Key]).ToArray());
        }

        internal static IReadOnlyList<Slot> FindSlots(JsonObject root, bool fillTemporaryReferences)
        {
            var definitions = new List<(AiPublicationCallSite Site, string Name, JsonObject Invocation)>();
            var handled = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
            var raw = AiPublicationJson.Read<AiPipelineDefinition>(root.ToJsonString());
            if (raw.ExecutionMode != AiExecutionMode.Dag) throw new NotSupportedException("Published durable execution requires an explicit DAG.");
            AiPublicationJson.Text(raw.Name, "PipelineName"); AiPublicationJson.Text(raw.Version, "PipelineVersion");
            if (raw.Steps.Count is < 1 or > 1024) throw new InvalidOperationException("A bounded nonempty DAG is required.");
            var steps = root["Steps"]!.AsArray();
            void Attach(AiPublicationCallSite site, string name, JsonObject invocation)
            {
                if (fillTemporaryReferences)
                {
                    if (Get(invocation, "implementationRef") is not null)
                        throw new InvalidOperationException("Attach code to the declaration; implementation references are generated by publication.");
                    invocation["implementationRef"] = "publication-pending";
                }
                handled.Add(invocation); definitions.Add((site, name, invocation));
            }
            void Policies(JsonObject? config, string? stepName)
            {
                if (config?["concurrency"] is not JsonObject section) return;
                var list = Get(section, "policies") as JsonArray;
                if (list is null) return;
                for (var index = 0; index < list.Count; index++)
                {
                    var policy = AiPublicationJson.Read<AiConfiguredPolicyDefinition>(list[index]!.ToJsonString());
                    if (policy.Invocation?.Kind != AiInvocationKind.Custom) continue;
                    if (policy.Kind is not null && !string.Equals(policy.Kind, "Concurrency", StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException("Only concurrency policy publication is supported by this checkpoint.");
                    // Canonicalize custom policy aliases with the existing converter, not an alternate parser.
                    var canonical = JsonNode.Parse(AiPublicationJson.Serialize(policy))!.AsObject();
                    list[index] = canonical;
                    Attach(new AiPublicationCallSite(AiPublicationFunctionKind.ConcurrencyPolicy, stepName, index), policy.Name,
                        canonical["invocation"]!.AsObject());
                }
            }
            foreach (var node in steps)
            {
                var step = node!.AsObject();
                var declaration = AiPublicationJson.Read<AiPipelineStepDefinition>(step.ToJsonString());
                if (declaration.Invocation?.Kind == AiInvocationKind.Custom)
                    Attach(new AiPublicationCallSite(AiPublicationFunctionKind.Step, declaration.Name), declaration.StepKey, step["Invocation"]!.AsObject());
                Policies(step["Config"] as JsonObject, declaration.Name);
            }
            Policies(root["Config"] as JsonObject, null);
            // Recognizable custom invocation descriptors outside supported sites must not remain unpinned.
            void RejectUnhandled(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    foreach (var property in obj)
                    {
                        if (property.Key.Equals("invocation", StringComparison.OrdinalIgnoreCase) && property.Value is JsonObject descriptor &&
                            Get(descriptor, "kind") is JsonValue kind &&
                            (kind.ToJsonString().Equals("\"Custom\"", StringComparison.OrdinalIgnoreCase) || kind.ToJsonString() == "1") &&
                            !handled.Contains(descriptor))
                            throw new NotSupportedException("A custom invocation exists outside supported publication declaration sites.");
                        RejectUnhandled(property.Value);
                    }
                }
                else if (node is JsonArray array) foreach (var child in array) RejectUnhandled(child);
            }
            RejectUnhandled(root);
            ValidateEmbeddedChildren(root, 0);
            var prepared = AiPublicationJson.Read<AiPipelineDefinition>(root.ToJsonString());
            var resolver = new AiInvocationBindingResolver(); resolver.ValidatePipelineLanguage(prepared);
            var result = new List<Slot>();
            foreach (var entry in definitions)
            {
                var owner = entry.Site.StepName is null ? null : prepared.Steps.Single(s => s.Name == entry.Site.StepName);
                var binding = entry.Site.Kind == AiPublicationFunctionKind.Step
                    ? resolver.ResolveStep(prepared, owner!)
                    : resolver.ResolvePolicy(prepared,
                        new DefaultAiConcurrencyDefinitionResolver().ReadPolicyDeclarations(owner?.Config ?? prepared.Config)[entry.Site.PolicyIndex!.Value],
                        owner is null ? AiPolicyBindingScope.Pipeline : AiPolicyBindingScope.Step, owner).Invocation;
                result.Add(new Slot(entry.Site, entry.Name, binding.ExecutionLanguage!, entry.Invocation));
            }
            return result;
        }
        // Existing native Child DAGs can keep their exact inline definitions. Name-only lookup
        // and custom child publications are not silently treated as part of the root code closure.
        private static void ValidateEmbeddedChildren(JsonObject pipeline, int depth)
        {
            if (depth > 16) throw new NotSupportedException("Embedded native Child DAG nesting exceeds the publication bound.");
            foreach (var node in pipeline["Steps"]!.AsArray())
            {
                var step = node!.AsObject();
                if (step["StepKey"]?.GetValue<string>() != ExecuteChildDagStep.StepKey) continue;
                var config = step["Config"] as JsonObject;
                if (config?[ExecuteChildDagStep.ChildDagDefinitionConfigKey] is not JsonObject child)
                    throw new NotSupportedException("Published native Child DAGs require an exact inline child definition, not a mutable provider lookup.");
                var childDefinition = AiPublicationJson.Read<AiPipelineDefinition>(child.ToJsonString());
                if (childDefinition.ExecutionMode != AiExecutionMode.Dag ||
                    config[ExecuteChildDagStep.ChildDagIdConfigKey]?.GetValue<string>() != childDefinition.Name ||
                    config[ExecuteChildDagStep.ChildDagVersionConfigKey]?.GetValue<string>() != childDefinition.Version)
                    throw new InvalidOperationException("Embedded Child DAG identity/version does not match its invocation.");
                ValidateEmbeddedChildren(child, depth + 1);
            }
        }

        // Preserve the historical simple-symbol grammar while also allowing the explicit
        // CLR TypeName::MethodName form used by the hosted .NET worker. Colons are never
        // accepted outside that exact form, so publication still rejects ambiguous symbols.
        private static void ValidateEntryPointSymbol(string symbol)
        {
            if (symbol.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '$' or ':')))
                throw new InvalidOperationException("Invalid portable entry-point symbol.");

            if (!symbol.Contains(':', StringComparison.Ordinal)) return;

            var separator = symbol.IndexOf("::", StringComparison.Ordinal);
            if (separator <= 0 || separator != symbol.LastIndexOf("::", StringComparison.Ordinal) ||
                separator + 2 >= symbol.Length || symbol.Count(c => c == ':') != 2 ||
                !IsQualifiedIdentifier(symbol[..separator]) || !IsIdentifier(symbol[(separator + 2)..]))
                throw new InvalidOperationException("Invalid portable entry-point symbol.");
        }

        private static bool IsQualifiedIdentifier(string value) =>
            value.Split('.').All(IsIdentifier);

        private static bool IsIdentifier(string value)
        {
            if (value.Length == 0 || !IsIdentifierStart(value[0])) return false;
            return value.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$');
        }

        private static bool IsIdentifierStart(char value) =>
            char.IsAsciiLetter(value) || value is '_' or '$';

        internal static void ValidateSite(AiPublicationCallSite site)
        {
            ArgumentNullException.ThrowIfNull(site);
            if (!Enum.IsDefined(site.Kind) || site.Kind == AiPublicationFunctionKind.Step && (site.StepName is null || site.PolicyIndex is not null) ||
                site.Kind == AiPublicationFunctionKind.ConcurrencyPolicy && (site.PolicyIndex is null || site.PolicyIndex < 0))
                throw new InvalidOperationException("Invalid publication declaration site.");
            if (site.StepName is not null) AiPublicationJson.Text(site.StepName, "StepName");
        }
        private static JsonNode? Get(JsonObject value, string name)
        {
            var matches = value.Where(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous casing in a publication declaration.");
            return matches.Length == 0 ? null : matches[0].Value;
        }
        internal sealed record FileEnvelope(int SchemaVersion, string ContentSha256, long SizeBytes, string Base64Url);
    }
}
