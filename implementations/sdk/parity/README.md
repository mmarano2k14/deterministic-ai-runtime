# External SDK cross-language parity

This directory contains the canonical fixtures used to validate the independently consumable .NET, TypeScript and Python SDKs against the same public protocol boundary.

`fixtures/sdk-parity-v1.json` is intentionally language-neutral. Each SDK must consume the same values rather than maintaining a private copy of operation names, schema versions, enum literals or request examples.

## What parity means

The parity suite locks the following behavior:

- protocol version and the five public operation names;
- request and response schema versions;
- string enum literals;
- request JSON shape for publication, submission, observation, result retrieval and cancellation;
- contract defaults for minimal publication, submission and cancellation requests;
- omission of absent optional fields;
- JSON `null` preservation inside explicit JSON payload objects;
- optional top-level execution input `null` is treated as absent consistently across all three SDKs;
- normalized error semantics;
- absence of runtime/control-plane identity from external SDK sources.

JSON object property order is not part of the contract. Structural JSON equality is used by the tests.

## Validation

Run the language suites from the repository root.

### .NET

```cmd
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Sdk.Tests\Multiplexed.AI.Sdk.Tests.csproj
```

### TypeScript

```cmd
cd implementations\node\sdk
npm run build
npm test
npm run validate:foundation
cd ..\..\..
```

### Python

```cmd
cd implementations\python\sdk
python -m unittest discover -s tests -p "test_*.py" -v
python tests\validate.py
cd ..\..\..
```

## Package smoke validation

Package publication is not performed by this validation. `package_smoke.py` builds local package artifacts, consumes them from temporary applications, imports the public SDK surface and removes the temporary consumers afterwards. .NET and TypeScript package restore may access configured public package feeds for third-party transport dependencies.

Run all package smokes from the repository root:

```cmd
python .\implementations\sdk\parity\package_smoke.py --language all
```

A single package can also be checked with `--language dotnet`, `--language typescript` or `--language python`.

Live client-to-server transport execution is deliberately outside this parity suite. It belongs to the final multi-language runtime matrix, where the external client language is crossed with hosted worker language and runtime/provider behavior.
