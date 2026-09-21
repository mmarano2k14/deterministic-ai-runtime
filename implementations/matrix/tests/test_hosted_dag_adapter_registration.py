"""Source-layout regression guards. These do not execute C# or prove live worker execution."""
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[3]
DOTNET = ROOT / "implementations" / "dotnet"
REGISTRATION = DOTNET / "src/Multiplexed.AI.McpServer.Host/Bootstrap/HostedInvocationHostRegistration.cs"
DAG_DI = DOTNET / "src/Multiplexed.AI/Runtime/Invocation/Durable/DI/AiDurableInvocationDagServiceCollectionExtensions.cs"
PROFILE = DOTNET / "Tests/Multiplexed.AI.McpServer.Tests.Integration/Scenarios/Production/Providers/Http/KubernetesPool/HttpKubernetesPoolExternalSdkMatrixProfileTests.cs"
CSHARP_TESTS = DOTNET / "Tests/Multiplexed.AI.McpServer.Tests.Integration/Bootstrap/HostedInvocationHostRegistrationTests.cs"


def method_body(source: str, signature: str) -> str:
    """Extract the balanced declaration blocks used by these source-layout guards."""
    start = source.index("{", source.index(signature))
    depth = 1
    for end in range(start + 1, len(source)):
        depth += (source[end] == "{") - (source[end] == "}")
        if depth == 0:
            return source[start + 1:end]
    raise AssertionError("Unbalanced source block")


def core_is_unconditional_for_enabled_host(source: str) -> bool:
    body = method_body(source, "public static void Configure(")
    statement = "services.AddAiDurableInvocationDag();"
    if body.count(statement) != 1:
        return False
    prefix = body[:body.index(statement)]
    return (
        prefix.count("{") == prefix.count("}")
        and "if (!options.Enabled)" in prefix
        and "return;" in prefix
        and body.index(statement) < body.index("if (options.EnableDagReconciliation)")
    )


class HostedDagAdapterRegistrationTests(unittest.TestCase):
    def test_adapters_are_registered_independently_of_reconciliation(self) -> None:
        self.assertTrue(core_is_unconditional_for_enabled_host(REGISTRATION.read_text(encoding="utf-8-sig")))

    def test_guard_detects_the_previous_missing_core_registration(self) -> None:
        source = REGISTRATION.read_text(encoding="utf-8-sig")
        previous = source.replace("services.AddAiDurableInvocationDag();", "", 1)
        self.assertFalse(core_is_unconditional_for_enabled_host(previous))

    def test_guard_rejects_core_registration_nested_under_reconciliation(self) -> None:
        source = REGISTRATION.read_text(encoding="utf-8-sig")
        nested = source.replace("services.AddAiDurableInvocationDag();", "", 1)
        marker = "if (options.EnableDagReconciliation)\n            {"
        self.assertIn(marker, nested)
        nested = nested.replace(marker, marker + "\n                services.AddAiDurableInvocationDag();", 1)
        self.assertFalse(core_is_unconditional_for_enabled_host(nested))

    def test_reconciliation_loop_remains_guarded_by_its_own_flag(self) -> None:
        source = REGISTRATION.read_text(encoding="utf-8-sig")
        block = method_body(source, "if (options.EnableDagReconciliation)")
        self.assertEqual(1, source.count("services.AddAiDurableInvocationDagReconciliation("))
        self.assertIn("services.AddAiDurableInvocationDagReconciliation(", block)
        self.assertNotIn("options.EnableDagReconciliation =", source)

    def test_existing_core_extension_does_not_start_a_reconciliation_loop(self) -> None:
        source = DAG_DI.read_text(encoding="utf-8-sig")
        core = method_body(source, "public static IServiceCollection AddAiDurableInvocationDag(")
        self.assertIn('new[] { "python", "typescript", "dotnet" }', core)
        self.assertIn("IAiStepInvocationAdapterFactory", core)
        self.assertNotIn("AddHostedService", core)
        self.assertNotIn("AddAiDurableInvocationDagReconciliation(", core)

    def test_kubernetes_child_role_flags_are_preserved(self) -> None:
        profile = PROFILE.read_text(encoding="utf-8-sig")
        self.assertIn('Child(settings, "AiHostedInvocation__EnableDagReconciliation", "false")', profile)
        self.assertIn('Child(settings, "AiHostedInvocation__EnableWorkerPolling", "true")', profile)
        self.assertIn('Child(settings, "AiHostedInvocation__EnableLocalWorkerProfiles", "true")', profile)
        self.assertIn('settings["AiHostedInvocation:EnableDagReconciliation"] = "true"', profile)
        self.assertIn('settings["AiHostedInvocation:EnableWorkerPolling"] = "false"', profile)

    def test_behavioral_regressions_use_real_host_registration_and_resolver(self) -> None:
        tests = CSHARP_TESTS.read_text(encoding="utf-8-sig")
        self.assertIn("HostedInvocationHostRegistration.Configure(", tests)
        self.assertIn("new AiPipelineResolver(", tests)
        self.assertIn("Runtime_Host_Resolves_Custom_Dag_Without_Dag_Reconciliation", tests)
        self.assertIn("Disabled_Hosted_Invocation_Still_Rejects_Custom_Without_Native_Fallback", tests)
        self.assertIn("Adapter_Registration_Does_Not_Enable_Sequential_Custom_Execution", tests)
        self.assertIn("Preinstalled_Core_Adapters_Are_Not_Duplicated", tests)
        self.assertIn("AssertHostedRoles", tests)


if __name__ == "__main__":
    unittest.main()
