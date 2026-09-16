# Deterministic Dependency Packaging

**Status:** implemented for the selected finite package formats on the hosted Python, TypeScript/Node, and .NET execution paths.

## Purpose

Deterministic dependency packaging captures the exact dependency material required by a published custom implementation before publication becomes visible. The runtime executes only that immutable material. It does not install packages, query registries, resolve `latest`, compile tenant code, or discover a new dependency closure while a run is executing.

The packaging layer reuses the existing immutable publication, environment descriptor, run pin, published-child execution binding, worker materialization, RBAC, durable invocation journal, and hosted process boundary. It does not introduce another execution engine or another environment identity.

## Supported package kinds

| Package kind | Language | Supported boundary |
|---|---|---|
| `PythonWheelBundle` | Python | Pure-Python wheel captured as immutable bytes with a closed manifest binding wheel path, SHA-256, distribution/version, and allowed import roots. |
| `NodeLockedBundle` | TypeScript / Node.js | Closed source bundle with exact package/version, entry point, complete file inventory, and SHA-256 for every published source file. |
| `DotNetAssemblyClosure` | .NET | Precompiled managed assembly closure with exact file SHA-256 plus CLR assembly name and version for each supplied DLL. |

These are finite contracts, not aliases for the full package ecosystems of the three languages.

## Common publication model

```text
dependency input
      |
      v
package-specific validation
      |
      +-- exact identity
      +-- exact file set
      +-- exact hashes
      +-- bounded portable paths
      |
      v
immutable publication material
      |
      v
existing environment descriptor
      |
      v
publication manifest visibility
      |
      v
run pin / published-child binding
      |
      v
worker materialization + revalidation
      |
      v
existing hosted language loader
```

All required dependency documents are persisted before manifest visibility. Republishing different dependency bytes creates new immutable material; an already admitted run continues to resolve the publication and dependency closure it originally pinned.

The canonical environment-document SHA-256 keeps its existing meaning. It is not reinterpreted as a package digest, host-runtime digest, or OCI image-manifest digest.

## Python wheel contract

The Python package path supports deterministic **pure-Python** wheel bundles only.

Before publication, validation binds:

- wheel path;
- wheel SHA-256;
- distribution name;
- distribution version;
- allowed import roots;
- wheel metadata indicating a pure-Python compatible layout.

Before worker readiness, the worker revalidates the captured wheel bytes and package manifest. Execution uses the published material directly through the hosted Python loader.

The runtime does **not** perform:

- `pip install`;
- PyPI lookup;
- runtime wheel resolution;
- native-extension installation;
- namespace-package discovery;
- ambient `PYTHONPATH` substitution;
- arbitrary wheel extraction/install semantics.

Native `.pyd`, `.so`, platform/ABI-specific extensions, unsupported `.data` installation trees, traversal entries, symlinks, identity mismatches, and incompatible manifests are refused.

## Node locked-bundle contract

The Node package path supports a closed, immutable TypeScript source bundle.

The manifest binds:

- exact package name;
- exact version;
- entry point;
- complete ordered file inventory;
- SHA-256 for every source file.

Publication rejects missing, additional, reordered, mismatched, nonportable, or unsupported executable material. The worker revalidates the closed bundle before readiness and uses the existing hosted TypeScript compiler/loader path.

The runtime does **not** perform:

- `npm install`;
- `npm ci`;
- `npx`;
- yarn/pnpm installation;
- `node_modules` discovery;
- registry lookup;
- lockfile resolution at execution time;
- native add-on installation.

This contract is deterministic source closure, not general npm compatibility.

## .NET managed-assembly closure

The .NET package path supports precompiled managed assemblies only.

For every supplied dependency DLL, the manifest binds:

- portable path;
- SHA-256;
- CLR assembly name;
- CLR assembly version.

The server validates the assembly metadata against the exact captured bytes before publication. The hosted worker revalidates the closure before readiness and loads the published assemblies through the existing collectible `AssemblyLoadContext`.

The runtime does **not** perform:

- NuGet restore;
- MSBuild;
- Roslyn tenant compilation;
- package-registry lookup;
- transitive dependency discovery;
- native dependency resolution;
- ReadyToRun/native-AOT environment construction.

## Durability and republishing

Package material follows the existing publication/run-pinning rules.

A run admitted against publication A continues to use A even if publication B later contains different package bytes under the same logical dependency name/version. The same rule applies to published Child DAG executions through their immutable execution association and `DefinitionPath`.

Missing or changed pinned material fails explicitly before hosted execution rather than falling back to a newer publication, ambient package installation, or another dependency source.

## Validation status

The selected package implementations have reported passing targeted validation in their individual deliveries:

- Python pure-wheel packaging: targeted .NET tests reported green; standalone Python hosted-worker suite passed 53/53 during preparation.
- Node locked bundles: targeted .NET tests reported green; standalone Node hosted-worker suite passed 45/45 during preparation.
- .NET managed assembly closure: targeted packaging and existing .NET worker/publication regression tests reported green in the target environment.

A final cross-language closure suite defines seven bounded cases:

1. final package capability/legacy-compatibility matrix;
2. Python republish/pinning through the real hosted path;
3. Node republish/pinning through the real hosted path;
4. .NET republish/pinning through the real hosted path;
5. missing pinned Python wheel material fails before worker launch;
6. missing pinned Node bundle material fails before worker launch;
7. missing pinned .NET assembly material fails before worker launch.

This document does not claim the final seven-case closure is green until that run is explicitly recorded. Python and Node opt-in process tests must execute rather than be skipped for complete closure evidence.

## Compatibility

Historical explicit source/file dependencies remain supported. The package metadata is additive and does not reinterpret older dependency records.

The packaging work does not change:

- DAG scheduling or transitions;
- durable invocation identity, leases, epochs, or result acceptance;
- RBAC;
- recovery authority;
- shared queues;
- parent/child continuation;
- policy engine authority;
- worker-process lifecycle ownership.

## Limits

The current packaging feature is intentionally narrower than a package manager.

Not included:

- Python native extensions, broad wheel/platform compatibility, or namespace packages;
- general npm ecosystem/lockfile installation or native add-ons;
- .NET native dependencies, NuGet restore, runtime compilation, or automatic transitive discovery;
- arbitrary Internet package installation;
- sandbox/container enforcement;
- public SDK upload/build tooling.

The next isolation workstream may consume the same immutable environment/artifact material, but it must not collapse environment-document identity into OCI image identity.
