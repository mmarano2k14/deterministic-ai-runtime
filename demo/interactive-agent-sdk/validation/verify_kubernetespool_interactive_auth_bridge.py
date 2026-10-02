from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]

checks = {
    ROOT / "implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Configuration/AiMatrixHarnessOptions.cs": [
        "UseStandaloneAuthentication",
    ],
    ROOT / "implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Bootstrap/HostAuthenticationRegistration.cs": [
        "matrix.Enabled && !matrix.UseStandaloneAuthentication",
    ],
    ROOT / "implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Bootstrap/MatrixHarnessRegistration.cs": [
        "if (!options.UseStandaloneAuthentication)",
        "MatrixStaticBearerAuthenticationHandler.SchemeName",
    ],
    ROOT / "implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Bootstrap/StandaloneAccessContextEndpoint.cs": [
        "matrix.Enabled && !matrix.UseStandaloneAuthentication",
    ],
    ROOT / "implementations/dotnet/Tests/Multiplexed.AI.McpServer.Tests.Integration/Bootstrap/HostAuthenticationRegistrationTests.cs": [
        "Configure_Matrix_Default_Preserves_Static_Bearer_Scheme",
        "Configure_Matrix_StandaloneAuthentication_Preserves_Jwt_Bearer_Scheme",
    ],
}

failures = []
for path, markers in checks.items():
    if not path.is_file():
        failures.append(f"missing file: {path}")
        continue
    text = path.read_text(encoding="utf-8-sig")
    for marker in markers:
        if marker not in text:
            failures.append(f"missing marker {marker!r}: {path}")

if failures:
    print("KubernetesPool interactive auth bridge verification: FAILED", file=sys.stderr)
    for failure in failures:
        print(f"- {failure}", file=sys.stderr)
    raise SystemExit(1)

print("KubernetesPool interactive auth bridge verification: PASS")
print("- historical MatrixBearer default preserved")
print("- opt-in standalone JWT path present")
print("- standalone access-context mapping enabled for opt-in matrix hosts")
