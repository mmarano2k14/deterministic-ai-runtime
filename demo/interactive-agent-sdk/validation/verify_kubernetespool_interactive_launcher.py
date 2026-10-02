from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]

runner = ROOT / "implementations/matrix/runtime/kubernetes/run-kubernetes-pool-sdk-execution.ps1"
launcher = ROOT / "demo/interactive-agent-sdk/validation/launch_kubernetespool_matrix.py"
matrix = ROOT / "demo/interactive-agent-sdk/validation/run_kubernetespool_interactive_matrix.py"
attestor = ROOT / "demo/interactive-agent-sdk/validation/attest_kubernetespool.py"
attestor_tests = ROOT / "demo/interactive-agent-sdk/validation/test_kubernetespool_attestation.py"
common = ROOT / "demo/interactive-agent-sdk/validation/run_e2e.py"
runtime_registration = ROOT / "implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Bootstrap/AiRuntimeServiceRegistration.cs"
child_dag_registration = ROOT / "implementations/dotnet/src/Multiplexed.AI/Runtime/Execution/Composition/ChildDag/DI/AiChildDagCompositionServiceCollectionExtensions.cs"

failures: list[str] = []


def require(path: Path, *markers: str) -> str:
    if not path.is_file():
        failures.append(f"missing file: {path}")
        return ""
    text = path.read_text(encoding="utf-8-sig")
    for marker in markers:
        if marker not in text:
            failures.append(f"missing marker {marker!r}: {path}")
    return text


runner_text = require(
    runner,
    "[switch]$InteractiveAgent",
    "AiMatrixHarness:UseStandaloneAuthentication",
    "AiMcpAuthentication:Enabled",
    "AiChildDagComposition:Enabled",
    "AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:AiChildDagComposition__Enabled",
    "$settings.PSObject.Properties.Remove('AiKubernetesRuntimePoolHost:OpenAiApiKey')",
    '$startInfo.EnvironmentVariables["AiKubernetesRuntimePoolHost__OpenAiApiKey"] = $env:OPENAI_API_KEY',
    "run_kubernetespool_interactive_matrix.py",
    'foreach ($sdk in @("dotnet", "typescript", "python"))',
    '("kubernetespool-http-{0}.json" -f $sdk)',
    "No dedicated interactive client log is produced",
    "Interactive external SDK orchestration failed before KubernetesPool closure",
    "PASS: interactive KubernetesPool/HTTP 3/3",
)

# PSCustomObject settings created by ConvertFrom-Json must be extended with Add-Member.
# A direct assignment to a missing property would fail at runtime under StrictMode.
for forbidden in (
    "$settings.'AiMatrixHarness:UseStandaloneAuthentication' =",
    "$settings.'AiMcpAuthentication:Enabled' =",
    "$settings.'AiMcpAuthentication:RequireHttpsMetadata' =",
    "$settings.'AiMcpAuthentication:ExecutionContextTtlSeconds' =",
):
    if forbidden in runner_text:
        failures.append(f"unsafe direct PSCustomObject property assignment remains: {forbidden}")

# The historical placeholder must not survive in the command-line settings when the
# environment-supplied real provider key is intended to win.
remove_index = runner_text.find("$settings.PSObject.Properties.Remove('AiKubernetesRuntimePoolHost:OpenAiApiKey')")
args_index = runner_text.find("$processArguments = @($controlPlaneHostDll)")
if remove_index < 0 or args_index < 0 or remove_index > args_index:
    failures.append("OpenAI placeholder is not removed before control-plane command-line arguments are composed")


# Child DAG composition must be enabled before command-line arguments are materialized,
# and must be projected to RuntimeInstanceOnly children as well as the control plane.
child_parent_index = runner_text.find("AiChildDagComposition:Enabled")
child_runtime_index = runner_text.find("AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:AiChildDagComposition__Enabled")
if child_parent_index < 0 or child_runtime_index < 0 or args_index < 0:
    failures.append("interactive Child DAG registration markers are incomplete")
elif child_parent_index > args_index or child_runtime_index > args_index:
    failures.append("interactive Child DAG settings are applied after control-plane arguments are composed")

require(
    launcher,
    "-InteractiveAgent",
    'env["AI_DEMO_PROJECT"] = "matrix"',
    'env["AI_DEMO_TENANT_ID"] = "matrix-tenant"',
    'env["AI_DEMO_TENANT_GROUP_ID"] = "matrix-group"',
    'env["AI_RUNTIME_ENDPOINT"] = "http://127.0.0.1:8081/mcp"',
    "OPENAI_API_KEY is required by the KubernetesPool runtime",
    "PASS: KubernetesPool/HTTP 3/3",
)

require(
    matrix,
    'SDKS = ("dotnet", "typescript", "python")',
    '"AI_VALIDATION_RUNTIME_PROVIDER": "KubernetesPool"',
    "build_live_attestation",
    "_attach_topology",
    "verify_topology",
    "External SDK consumer exited before KubernetesPool became live-attestable",
    'default=300',
)

require(
    attestor,
    '"source": "kubernetes-cluster-inspection"',
    '"multiplexed.ai/pool-id"',
    '"multiplexed.ai/runtime-instance-count"',
    '"pool-service-count"',
    '"runtime-image"',
)

require(
    attestor_tests,
    "test_verified_live_pool",
    "test_not_ready_fails_closed",
    "test_wrong_image_fails_closed",
    "test_missing_service_fails_closed",
)

require(
    runtime_registration,
    'configuration.GetValue<bool>("AiChildDagComposition:Enabled")',
    "services.AddAiChildDagComposition();",
    "options.Snapshots.Enabled",
    "options.Snapshots.Mongo.Enabled",
)

require(
    child_dag_registration,
    "IAiChildExecutionRelationStore",
    "MongoAiChildExecutionRelationStore",
    "TryAddSingleton<IAiChildExecutionRelationStore, MongoAiChildExecutionRelationStore>()",
)

common_text = require(
    common,
    '"OPENAI_API_KEY"',
    'env.pop(secret_name, None)',
    'env["AI_RUNTIME_TOKEN"] = token',
)

# Ensure the external consumer secret firewall remains in the function that actually
# launches SDK processes rather than merely in bootstrap preparation.
consumer_section = common_text[common_text.find("def _consumer_environment"): common_text.find("def _attach_topology")]
if '"OPENAI_API_KEY"' not in consumer_section:
    failures.append("external consumer environment no longer strips OPENAI_API_KEY")

if failures:
    print("KubernetesPool interactive launcher verification: FAILED", file=sys.stderr)
    for failure in failures:
        print(f"- {failure}", file=sys.stderr)
    raise SystemExit(1)

print("KubernetesPool interactive launcher verification: PASS")
print("- historical KubernetesPool runner remains the lifecycle authority")
print("- interactive JWT/access-context mode is opt-in")
print("- real OpenAI key overrides the historical placeholder without profile persistence")
print("- interactive Child DAG composition is enabled on the control plane and Runtime Pool children")
print("- early consumer exit is reported directly instead of being masked as a topology-attestation timeout")
print("- live topology attestation is exact-pool Pod/Service/image based")
print("- .NET / TypeScript / Python consumers retain the server-secret firewall")
