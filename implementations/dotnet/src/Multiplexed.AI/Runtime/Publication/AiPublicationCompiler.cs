using System.Text.Json;
using System.Text.Json.Nodes;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Retry;
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
                var environment = Add("environment", AiPublicationExecutionDescriptors.Capture(runtime, dependencies, catalog));
                var implementation = Add("implementation", new AiPublicationImplementation(1, slot.Language,
                    source.EntryPointPath, source.EntryPointSymbol, source.Sources.Select(File).ToArray(), environment));
                var reference = "impl-" + implementation.Sha256;
                slot.Invocation["implementationRef"] = reference;
                functions.Add(new AiPublicationFunction(slot.Site, slot.LogicalName, slot.Language, reference, implementation, environment));
            }
            var definition = AiPublicationJson.Read<AiPipelineDefinition>(root.ToJsonString());
            var definitionDocument = Add("definition", definition);
            var manifestSchemaVersion = functions.Any(function => function.Site.DefinitionPath is not null) ? 2 : 1;
            var manifest = new AiPipelinePublicationManifest(manifestSchemaVersion, partition, definition.Name, definition.Version!, definitionDocument,
                functions.OrderBy(f => AiPublicationJson.Serialize(f.Site), StringComparer.Ordinal).ToArray());
            var manifestJson = AiPublicationJson.Serialize(manifest);
            AiPublicationJson.ValidateJson(manifestJson, AiPublicationJson.MaxManifestBytes);
            var manifestDocument = Add("manifest", manifest);
            return new Compilation(definition, new AiPipelinePublication("pub-" + manifestDocument.Sha256, manifestDocument.Sha256, manifest),
                writes.Values.Where(w => w.Kind != "manifest").Append(writes[manifestDocument.Key]).ToArray());
        }

        internal static IReadOnlyList<Slot> FindSlots(JsonObject root, bool fillTemporaryReferences)
        {
            var result = new List<Slot>();
            var handled = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
            var resolver = new AiInvocationBindingResolver();

            void Attach(
                AiPublicationCallSite site,
                string name,
                JsonObject invocation,
                List<(AiPublicationCallSite Site, string Name, JsonObject Invocation)> definitions)
            {
                ValidateSite(site);
                if (fillTemporaryReferences)
                {
                    if (Get(invocation, "implementationRef") is not null)
                        throw new InvalidOperationException("Attach code to the declaration; implementation references are generated by publication.");
                    invocation["implementationRef"] = "publication-pending";
                }

                handled.Add(invocation);
                definitions.Add((site, name, invocation));
            }

            void CollectPipeline(JsonObject pipeline, string? definitionPath, int depth)
            {
                if (depth > 16)
                    throw new NotSupportedException("Embedded Child DAG nesting exceeds the publication bound.");

                var definitions = new List<(AiPublicationCallSite Site, string Name, JsonObject Invocation)>();
                var raw = AiPublicationJson.Read<AiPipelineDefinition>(pipeline.ToJsonString());
                if (raw.ExecutionMode != AiExecutionMode.Dag)
                    throw new NotSupportedException("Published durable execution requires an explicit DAG.");
                AiPublicationJson.Text(raw.Name, "PipelineName");
                AiPublicationJson.Text(raw.Version, "PipelineVersion");
                if (raw.Steps.Count is < 1 or > 1024)
                    throw new InvalidOperationException("A bounded nonempty DAG is required.");

                var steps = pipeline["Steps"]?.AsArray()
                    ?? throw new InvalidOperationException("Published DAG steps are required.");

                void Policies(
                    JsonObject? config,
                    string? stepName,
                    string configKey,
                    AiPolicyKind family,
                    AiPublicationFunctionKind functionKind)
                {
                    if (config is null || Get(config, configKey) is not JsonObject section) return;
                    var list = Get(section, "policies") as JsonArray;
                    if (list is null) return;

                    var capability = AiCustomPolicyFamilyCapabilities.Get(family);
                    for (var index = 0; index < list.Count; index++)
                    {
                        var policy = AiPublicationJson.Read<AiConfiguredPolicyDefinition>(list[index]!.ToJsonString());
                        if (policy.Invocation?.Kind != AiInvocationKind.Custom) continue;

                        if (!capability.SupportsCustomPublication)
                        {
                            throw new NotSupportedException(
                                $"Custom {family} policy publication is not supported by the current family capability contract.");
                        }

                        if (policy.Kind is not null && !string.Equals(policy.Kind, family.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            throw new NotSupportedException(
                                $"Policy '{policy.Name}' declares family '{policy.Kind}' inside the '{configKey}' checkpoint.");
                        }

                        // Canonicalize custom policy aliases with the existing converter, not an alternate parser.
                        var canonical = JsonNode.Parse(AiPublicationJson.Serialize(policy))!.AsObject();
                        list[index] = canonical;
                        Attach(
                            new AiPublicationCallSite(functionKind, stepName, index)
                            {
                                DefinitionPath = definitionPath
                            },
                            policy.Name,
                            canonical["invocation"]!.AsObject(),
                            definitions);
                    }
                }

                foreach (var node in steps)
                {
                    var step = node!.AsObject();
                    var declaration = AiPublicationJson.Read<AiPipelineStepDefinition>(step.ToJsonString());
                    if (declaration.Invocation?.Kind == AiInvocationKind.Custom)
                    {
                        Attach(
                            new AiPublicationCallSite(AiPublicationFunctionKind.Step, declaration.Name)
                            {
                                DefinitionPath = definitionPath
                            },
                            declaration.StepKey,
                            step["Invocation"]!.AsObject(),
                            definitions);
                    }

                    Policies(step["Config"] as JsonObject, declaration.Name, "concurrency", AiPolicyKind.Concurrency, AiPublicationFunctionKind.ConcurrencyPolicy);
                    Policies(step["Config"] as JsonObject, declaration.Name, "retry", AiPolicyKind.Retry, AiPublicationFunctionKind.RetryPolicy);
                    if (declaration.StepKey == ExecuteChildDagStep.StepKey)
                    {
                        Policies(step["Config"] as JsonObject, declaration.Name, "delegation", AiPolicyKind.Delegation, AiPublicationFunctionKind.DelegationPolicy);
                    }
                }

                Policies(pipeline["Config"] as JsonObject, null, "concurrency", AiPolicyKind.Concurrency, AiPublicationFunctionKind.ConcurrencyPolicy);
                Policies(pipeline["Config"] as JsonObject, null, "retry", AiPolicyKind.Retry, AiPublicationFunctionKind.RetryPolicy);
                Policies(pipeline["Config"] as JsonObject, null, "delegation", AiPolicyKind.Delegation, AiPublicationFunctionKind.DelegationPolicy);

                var prepared = AiPublicationJson.Read<AiPipelineDefinition>(pipeline.ToJsonString());
                resolver.ValidatePipelineLanguage(prepared);
                foreach (var entry in definitions)
                {
                    var owner = entry.Site.StepName is null
                        ? null
                        : prepared.Steps.Single(step => step.Name == entry.Site.StepName);
                    var binding = entry.Site.Kind == AiPublicationFunctionKind.Step
                        ? resolver.ResolveStep(prepared, owner!)
                        : resolver.ResolvePolicy(
                            prepared,
                            ReadPolicyDeclaration(
                                entry.Site.Kind,
                                owner?.Config ?? prepared.Config,
                                entry.Site.PolicyIndex!.Value),
                            owner is null ? AiPolicyBindingScope.Pipeline : AiPolicyBindingScope.Step,
                            owner).Invocation;
                    result.Add(new Slot(entry.Site, entry.Name, binding.ExecutionLanguage!, entry.Invocation));
                }

                foreach (var node in steps)
                {
                    var step = node!.AsObject();
                    var declaration = AiPublicationJson.Read<AiPipelineStepDefinition>(step.ToJsonString());
                    if (declaration.StepKey != ExecuteChildDagStep.StepKey) continue;

                    var config = step["Config"] as JsonObject;
                    if (config?[ExecuteChildDagStep.ChildDagDefinitionConfigKey] is not JsonObject child)
                    {
                        throw new NotSupportedException(
                            "Published Child DAGs require an exact inline child definition, not a mutable provider lookup.");
                    }

                    var childDefinition = AiPublicationJson.Read<AiPipelineDefinition>(child.ToJsonString());
                    if (childDefinition.ExecutionMode != AiExecutionMode.Dag ||
                        config[ExecuteChildDagStep.ChildDagIdConfigKey]?.GetValue<string>() != childDefinition.Name ||
                        config[ExecuteChildDagStep.ChildDagVersionConfigKey]?.GetValue<string>() != childDefinition.Version)
                    {
                        throw new InvalidOperationException("Embedded Child DAG identity/version does not match its invocation.");
                    }

                    AiPublicationJson.Text(declaration.Name, "ChildDagStepName");
                    CollectPipeline(child, AiPublicationDefinitionPath.Append(definitionPath, declaration.Name), depth + 1);
                }
            }

            CollectPipeline(root, definitionPath: null, depth: 0);

            // Recognizable custom invocation descriptors outside supported sites must not remain unpinned.
            void RejectUnhandled(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    foreach (var property in obj)
                    {
                        if (property.Key.Equals("invocation", StringComparison.OrdinalIgnoreCase) &&
                            property.Value is JsonObject descriptor &&
                            Get(descriptor, "kind") is JsonValue kind &&
                            (kind.ToJsonString().Equals("\"Custom\"", StringComparison.OrdinalIgnoreCase) || kind.ToJsonString() == "1") &&
                            !handled.Contains(descriptor))
                        {
                            throw new NotSupportedException("A custom invocation exists outside supported publication declaration sites.");
                        }

                        RejectUnhandled(property.Value);
                    }
                }
                else if (node is JsonArray array)
                {
                    foreach (var child in array) RejectUnhandled(child);
                }
            }

            RejectUnhandled(root);
            return result;
        }

        private static AiConfiguredPolicyDefinition ReadPolicyDeclaration(
            AiPublicationFunctionKind kind,
            IReadOnlyDictionary<string, object?> config,
            int index)
        {
            IReadOnlyList<AiConfiguredPolicyDefinition> policies = kind switch
            {
                AiPublicationFunctionKind.ConcurrencyPolicy =>
                    new DefaultAiConcurrencyDefinitionResolver().ReadPolicyDeclarations(config),
                AiPublicationFunctionKind.RetryPolicy =>
                    ReadConfiguredPolicies<AiRetryPolicyDefinition>(config, "retry", value => value.Policies),
                AiPublicationFunctionKind.DelegationPolicy =>
                    ReadConfiguredPolicies<AiChildDelegationPolicyDefinition>(config, "delegation", value => value.Policies),
                _ => throw new InvalidOperationException($"Publication site '{kind}' is not a policy declaration.")
            };

            if (index < 0 || index >= policies.Count)
            {
                throw new InvalidOperationException("Published policy index is outside its frozen declaration list.");
            }

            return policies[index];
        }

        private static IReadOnlyList<AiConfiguredPolicyDefinition> ReadConfiguredPolicies<TDefinition>(
            IReadOnlyDictionary<string, object?> config,
            string configKey,
            Func<TDefinition, IReadOnlyList<AiConfiguredPolicyDefinition>> select)
            where TDefinition : class
        {
            var matches = config
                .Where(pair => pair.Key.Equals(configKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidOperationException($"Ambiguous '{configKey}' policy configuration casing.");
            }
            if (matches.Length == 0 || matches[0].Value is null)
            {
                return Array.Empty<AiConfiguredPolicyDefinition>();
            }

            var raw = matches[0].Value;
            var definition = raw as TDefinition
                ?? JsonSerializer.Deserialize<TDefinition>(AiPublicationJson.Serialize(raw))
                ?? throw new InvalidOperationException($"Invalid '{configKey}' policy definition.");
            return select(definition);
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
            var policySite = site.Kind is AiPublicationFunctionKind.ConcurrencyPolicy
                or AiPublicationFunctionKind.RetryPolicy
                or AiPublicationFunctionKind.DelegationPolicy;
            if (!Enum.IsDefined(site.Kind) ||
                site.Kind == AiPublicationFunctionKind.Step && (site.StepName is null || site.PolicyIndex is not null) ||
                policySite && (site.PolicyIndex is null || site.PolicyIndex < 0))
                throw new InvalidOperationException("Invalid publication declaration site.");
            if (site.StepName is not null) AiPublicationJson.Text(site.StepName, "StepName");
            if (site.DefinitionPath is not null) AiPublicationDefinitionPath.Validate(site.DefinitionPath);
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
