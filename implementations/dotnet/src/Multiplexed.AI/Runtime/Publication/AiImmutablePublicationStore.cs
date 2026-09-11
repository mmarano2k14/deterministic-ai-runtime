using System.Text.Json.Nodes;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Reuses the configured immutable payload store. No additional MongoClient, mutable catalog,
    /// TTL, cache or storage engine. All entry points are called after server authorization.
    /// </summary>
    public sealed partial class AiImmutablePublicationStore
    {
        private readonly IAiPayloadStoreResolver _resolver;
        private readonly IAiPublicationEnvironmentCatalog _catalog;
        private readonly AiPublicationOptions _options;
        public AiImmutablePublicationStore(IAiPayloadStoreResolver resolver, IAiPublicationEnvironmentCatalog catalog, AiPublicationOptions options)
        { _resolver = resolver; _catalog = catalog; _options = options; }
        private IAiImmutablePayloadStore Resolve() => _resolver.Resolve() as IAiImmutablePayloadStore
            ?? throw new NotSupportedException("Publication requires the existing immutable payload-store capability.");

        internal async Task SaveAsync(AiPublicationCompiler.Compilation compilation, AiPublicationIdentity.Guard guard, CancellationToken token)
        {
            // The manifest is deliberately last. A partial write is not a published version.
            foreach (var write in compilation.Writes)
                await SaveExactAsync(write.Document.Key, write.Json, "publication-" + write.Kind, null, guard, token).ConfigureAwait(false);
        }
        internal async Task SavePinAsync(AiPublicationRunPin pin, AiPublicationIdentity.Guard guard, CancellationToken token)
        {
            ValidatePin(pin, guard.Partition, pin.ExecutionId);
            await SaveExactAsync(AiPublicationJson.PinKey(guard.Partition, pin.ExecutionId), AiPublicationJson.Serialize(pin),
                "publication-run-pin", null, guard, token).ConfigureAwait(false);
        }
        internal async Task<AiPublicationRunPin?> ReadPinAsync(string executionId, AiPublicationIdentity.Guard guard, CancellationToken token)
        {
            AiPublicationJson.Text(executionId, nameof(executionId)); guard.RequireCurrent(); token.ThrowIfCancellationRequested();
            var json = await Resolve().LoadAsync(AiPublicationJson.PinKey(guard.Partition, executionId), token).ConfigureAwait(false);
            guard.RequireCurrent(); token.ThrowIfCancellationRequested();
            if (json is null) return null;
            var pin = AiPublicationJson.Read<AiPublicationRunPin>(json);
            ValidatePin(pin, guard.Partition, executionId);
            if (pin.UserId != guard.UserId) throw new UnauthorizedAccessException("The run pin belongs to another execution identity.");
            return pin;
        }
        private async Task SaveExactAsync(string key, string json, string kind, string? executionId,
            AiPublicationIdentity.Guard guard, CancellationToken token)
        {
            guard.RequireCurrent(); token.ThrowIfCancellationRequested(); var store = Resolve();
            var saved = await store.SaveImmutableAsync(key, json, new AiPayloadMetadata
                { Kind = kind, ExecutionId = executionId, Reason = "immutable-publication" }, token).ConfigureAwait(false);
            guard.RequireCurrent(); token.ThrowIfCancellationRequested();
            if (saved != key) throw new InvalidOperationException("Immutable storage changed the requested document key.");
            var persisted = await store.LoadAsync(key, token).ConfigureAwait(false);
            guard.RequireCurrent(); token.ThrowIfCancellationRequested();
            if (persisted != json) throw new InvalidOperationException("Immutable storage did not preserve the exact publication content.");
        }

        internal async Task<(AiPipelinePublication Publication, AiPipelineDefinition Definition)> ReadVerifiedAsync(
            string publicationRef, AiPublicationIdentity.Guard guard, CancellationToken token)
        {
            var hash = AiPublicationJson.ReferenceHash(publicationRef, "pub-");
            var partition = guard.Partition;
            var manifestJson = await LoadAsync(AiPublicationJson.Key(partition, "manifest", hash), hash, null, guard, token).ConfigureAwait(false);
            AiPublicationJson.ValidateJson(manifestJson, AiPublicationJson.MaxManifestBytes);
            var manifest = AiPublicationJson.Read<AiPipelinePublicationManifest>(manifestJson);
            if (manifest.SchemaVersion != 1 || manifest.Partition != partition || manifest.Functions is null ||
                manifest.Functions.Count > _options.MaxFunctions)
                throw new InvalidOperationException("Unsupported or foreign publication manifest.");
            var definitionJson = await DocumentAsync(manifest.Definition, "definition", guard, token).ConfigureAwait(false);
            var definition = AiPublicationJson.Read<AiPipelineDefinition>(definitionJson);
            if (definition.Name != manifest.PipelineName || definition.Version != manifest.PipelineVersion)
                throw new InvalidOperationException("Publication definition identity mismatch.");
            var slots = AiPublicationCompiler.FindSlots(JsonNode.Parse(definitionJson)!.AsObject(), fillTemporaryReferences: false);
            if (slots.Count != manifest.Functions.Count || manifest.Functions.Select(f => f.Site).Distinct().Count() != manifest.Functions.Count)
                throw new InvalidOperationException("Publication function coverage is inconsistent.");
            long totalBytes = 0; var totalFiles = 0;
            // Cache is local to one authorized read and never outlives its partition or request.
            var content = new Dictionary<string, string>(StringComparer.Ordinal);
            async Task<string> ReadDocument(AiPublicationDocument document, string kind)
            {
                ValidateDocument(document, kind, partition);
                if (!content.TryGetValue(document.Key, out var json))
                    content[document.Key] = json = await DocumentAsync(document, kind, guard, token).ConfigureAwait(false);
                if (AiPublicationJson.Utf8.GetByteCount(json) != document.SizeBytes || AiPublicationJson.Hash(json) != document.Sha256)
                    throw new InvalidOperationException("Inconsistent duplicate publication descriptor.");
                return json;
            }
            async Task VerifyFiles(IReadOnlyList<AiPublicationFile> files)
            {
                if (files is null || files.Count is < 1 || files.Count > _options.MaxFiles) throw new InvalidOperationException("Invalid publication file list.");
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    ArgumentNullException.ThrowIfNull(file); AiPublicationJson.Path(file.Path); AiPublicationJson.ValidateHash(file.ContentSha256);
                    if (!paths.Add(file.Path) || file.SizeBytes < 0 || file.SizeBytes > _options.MaxFileBytes ||
                        (totalBytes += file.SizeBytes) > _options.MaxTotalBytes || ++totalFiles > _options.MaxFiles)
                        throw new InvalidOperationException("Publication file bounds or uniqueness are invalid.");
                    var envelope = AiPublicationJson.Read<AiPublicationCompiler.FileEnvelope>(await ReadDocument(file.Payload, "file").ConfigureAwait(false));
                    if (envelope.SchemaVersion != 1 || envelope.SizeBytes != file.SizeBytes || envelope.ContentSha256 != file.ContentSha256 ||
                        envelope.Base64Url is null || envelope.Base64Url.Length > ((_options.MaxFileBytes + 2) / 3) * 4)
                        throw new InvalidOperationException("Publication byte envelope is inconsistent.");
                    var bytes = AiPublicationJson.DecodeBytes(envelope.Base64Url);
                    if (bytes.Length != file.SizeBytes || AiPublicationJson.HashBytes(bytes) != file.ContentSha256)
                        throw new InvalidOperationException("Publication file bytes do not match their immutable digest.");
                }
            }
            foreach (var function in manifest.Functions)
            {
                var slot = slots.SingleOrDefault(s => s.Site == function.Site)
                    ?? throw new InvalidOperationException("Publication contains an unknown function site.");
                if (slot.Language != function.ExecutionLanguage || slot.LogicalName != function.LogicalName ||
                    slot.Invocation["implementationRef"]?.GetValue<string>() != function.ImplementationRef ||
                    AiPublicationJson.ReferenceHash(function.ImplementationRef, "impl-") != function.Implementation.Sha256)
                    throw new InvalidOperationException("Publication function does not match its exact declaration.");
                var implementation = AiPublicationJson.Read<AiPublicationImplementation>(await ReadDocument(function.Implementation, "implementation").ConfigureAwait(false));
                if (implementation.SchemaVersion != 1 || implementation.ExecutionLanguage != function.ExecutionLanguage ||
                    implementation.Environment != function.Environment)
                    throw new InvalidOperationException("Publication implementation metadata is inconsistent.");
                AiPublicationJson.Path(implementation.EntryPointPath); AiPublicationJson.Text(implementation.EntryPointSymbol, "EntryPointSymbol");
                await VerifyFiles(implementation.Sources).ConfigureAwait(false);
                if (!implementation.Sources.Any(s => s.Path == implementation.EntryPointPath))
                    throw new InvalidOperationException("Publication entry point is missing.");
                var environment = AiPublicationJson.Read<AiPublicationEnvironmentSnapshot>(await ReadDocument(function.Environment, "environment").ConfigureAwait(false));
                AiPublicationJson.ValidateEnvironment(environment.Runtime);
                if (environment.SchemaVersion != 1 || environment.Runtime.ExecutionLanguage != function.ExecutionLanguage ||
                    _catalog.Find(environment.Runtime.Reference) != environment.Runtime || environment.Dependencies is null || environment.Dependencies.Count > 64)
                    throw new InvalidOperationException("The exact pinned host runtime is unavailable or incompatible.");
                var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dependency in environment.Dependencies)
                {
                    AiPublicationJson.Text(dependency.Name, "DependencyName"); AiPublicationJson.Version(dependency.Version);
                    if (!dependencies.Add(dependency.Name)) throw new InvalidOperationException("Duplicate pinned dependency.");
                    await VerifyFiles(dependency.Files).ConfigureAwait(false);
                }
            }
            guard.RequireCurrent(); token.ThrowIfCancellationRequested();
            return (new AiPipelinePublication(publicationRef, hash, manifest), definition);
        }

        private Task<string> DocumentAsync(AiPublicationDocument document, string kind, AiPublicationIdentity.Guard guard, CancellationToken token)
        {
            ValidateDocument(document, kind, guard.Partition);
            return LoadAsync(document.Key, document.Sha256, document.SizeBytes, guard, token);
        }
        private static void ValidateDocument(AiPublicationDocument document, string kind, AiPublicationPartition partition)
        {
            ArgumentNullException.ThrowIfNull(document); AiPublicationJson.ValidateHash(document.Sha256);
            if (document.Key != AiPublicationJson.Key(partition, kind, document.Sha256) ||
                document.SizeBytes is < 1 or > AiPublicationJson.MaxDocumentBytes)
                throw new InvalidOperationException("Foreign or malformed publication document reference.");
        }
        private async Task<string> LoadAsync(string key, string hash, long? size, AiPublicationIdentity.Guard guard, CancellationToken token)
        {
            guard.RequireCurrent(); token.ThrowIfCancellationRequested();
            var json = await Resolve().LoadAsync(key, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Required immutable publication content is unavailable.");
            guard.RequireCurrent(); token.ThrowIfCancellationRequested();
            var actualSize = AiPublicationJson.Utf8.GetByteCount(json);
            if (actualSize > AiPublicationJson.MaxDocumentBytes || size is not null && size != actualSize || AiPublicationJson.Hash(json) != hash)
                throw new InvalidOperationException("Publication document content does not match its immutable digest.");
            AiPublicationJson.ValidateJson(json, AiPublicationJson.MaxDocumentBytes);
            return json;
        }
        private static void ValidatePin(AiPublicationRunPin pin, AiPublicationPartition partition, string executionId)
        {
            AiPublicationJson.Text(pin.RunKey, "RunKey"); AiPublicationJson.Text(pin.UserId, "UserId");
            AiPublicationJson.ValidateHash(pin.DefinitionSha256); AiPublicationJson.ValidateHash(pin.PublicationSha256);
            if (pin.SchemaVersion != 1 || pin.Partition != partition || pin.ExecutionId != executionId ||
                AiPublicationJson.ExecutionId(partition, pin.RunKey) != executionId ||
                AiPublicationJson.ReferenceHash(pin.PublicationRef, "pub-") != pin.PublicationSha256 ||
                AiDurableInvocationJson.Normalize(pin.InputsJson, true) != pin.InputsJson || AiPublicationJson.Hash(pin.InputsJson) != pin.InputsSha256)
                throw new InvalidOperationException("Immutable run pin is inconsistent.");
        }
    }
}
