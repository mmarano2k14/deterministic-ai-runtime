"""Source-contract checks, not a substitute for the C# RBAC or real Kubernetes runs."""
from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
MATRIX = ROOT / "implementations" / "matrix"
HOST = ROOT / "implementations" / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host"
TESTS = ROOT / "implementations" / "dotnet" / "Tests" / "Multiplexed.AI.McpServer.Tests.Integration"
SCENARIOS = TESTS / "Scenarios" / "Production" / "Providers" / "Http" / "KubernetesPool"


class KubernetesSdkRbacProjectTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.profile = (SCENARIOS / "HttpKubernetesPoolExternalSdkMatrixProfileTests.cs").read_text(encoding="utf-8-sig")
        cls.runner = (MATRIX / "runtime" / "kubernetes" / "run-kubernetes-pool-sdk-execution.ps1").read_text(encoding="utf-8-sig")
        cls.regressions = (SCENARIOS / "HttpKubernetesPoolExternalSdkRbacTests.cs").read_text(encoding="utf-8-sig")

    def test_profile_writer_applies_the_same_project_helper_exercised_by_regressions(self) -> None:
        self.assertIn("ConfigureRbacProject(settings);", self.profile)
        self.assertLess(self.profile.index("ConfigureRbacProject(settings);"), self.profile.index("var output = new"))
        self.assertIn("HttpKubernetesPoolExternalSdkMatrixProfileTests.ConfigureRbacProject(settings);", self.regressions)

    def test_one_explicit_constant_supplies_context_parent_and_child_projects(self) -> None:
        self.assertIn('private const string Project = "matrix";', self.profile)
        self.assertIn('settings["AiMatrixHarness:Project"] = Project;', self.profile)
        self.assertIn('settings["Multiplexed.Rbac.Core:Project"] = Project;', self.profile)
        self.assertIn('Child(settings, "Multiplexed.Rbac.Core__Project", Project);', self.profile)
        self.assertNotIn('settings["AiMatrixHarness:Project"] = "matrix";', self.profile)
        self.assertNotIn('Multiplexed__Rbac__Core__Project', self.profile)

    def test_runner_checks_all_three_values_before_launch_and_has_no_parent_only_override(self) -> None:
        for key in ("AiMatrixHarness:Project", "Multiplexed.Rbac.Core:Project",
                    "AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:Multiplexed.Rbac.Core__Project"):
            self.assertIn("$settings.'" + key + "'", self.runner)
        self.assertIn("[string]::IsNullOrWhiteSpace($contextProject)", self.runner)
        self.assertIn("[string]::Equals($contextProject, $controlPlaneProject, [System.StringComparison]::Ordinal)", self.runner)
        self.assertIn("[string]::Equals($contextProject, $runtimeChildProject, [System.StringComparison]::Ordinal)", self.runner)
        self.assertLess(self.runner.index("RBAC project mismatch"), self.runner.index("$startInfo = New-Object"))
        self.assertNotIn('$processArguments += "--Multiplexed.Rbac.Core:Project=matrix"', self.runner)

    def test_matrix_harness_exact_grants_include_public_execution_control_surface(self) -> None:
        bootstrap = (HOST / "Bootstrap" / "MatrixHarnessBootstrapHostedService.cs").read_text(encoding="utf-8-sig")
        grants = set(re.findall(r'Trn\(options, "([^"]+)", "([^"]+)", "([^"]+)"\)', bootstrap))
        self.assertEqual({
            ("code", "publication", "publish"), ("code", "publication", "read"),
            ("code", "publication", "execute"), ("shared-run", "execution", "submit"),
            ("execution", "control", "read"), ("execution", "control", "cancel"),
            ("execution", "control", "pause"), ("execution", "control", "resume"),
            ("execution", "control", "input"), ("replay", "execution", "run"),
            ("mcp-effect", "probe", "invoke"),
        }, grants)
        self.assertNotIn('Child(settings, "AiMatrixHarness__Enabled", "true")', self.profile)
        self.assertIn('Child(settings, "AiHostedInvocation__EnableDagReconciliation", "false")', self.profile)

    def test_csharp_regressions_use_production_authorization_and_no_mock_authorization_engine(self) -> None:
        self.assertIn("services.AddMultiplexedRbacRuntime(configuration);", self.regressions)
        self.assertIn("new AiPublicationInvocationTargetResolver(identity, options, publications)", self.regressions)
        self.assertIn("new MatrixHarnessBootstrapHostedService", self.regressions)
        self.assertIn("ExecutionContextSnapshotMapper.ToExecutionContext(snapshot)", self.regressions)
        self.assertNotIn(": IAuthorizationEngine", self.regressions)

    def test_csharp_regressions_cover_real_argument_and_environment_configuration_boundaries(self) -> None:
        self.assertIn("new AiKubernetesRuntimePoolInPodCommandLineFactory(host).Create(spec, request)", self.regressions)
        self.assertIn("AddCommandLine(arguments.ToArray())", self.regressions)
        self.assertIn("AddEnvironmentVariables(prefix)", self.regressions)
        self.assertIn("Environment.SetEnvironmentVariable(prefix + pair.Key, null);", self.regressions)

    def test_csharp_regressions_keep_missing_permission_scope_context_and_pin_failures(self) -> None:
        for name in (
            "Missing_Child_Project_Reproduces_Observed_Denial_Despite_Matrix_Snapshot",
            "Explicit_Foreign_Host_Project_Does_Not_Authorize_Matrix_Grant",
            "Aligned_Project_Does_Not_Replace_A_Missing_Execute_Grant",
            "Aligned_Project_Does_Not_Bypass_Publication_Scope_Ownership",
            "Aligned_Project_Does_Not_Create_A_Trusted_Execution_Context",
            "Restored_Owner_Passes_Execute_Guard_But_Still_Requires_An_Immutable_Pin",
        ):
            self.assertIn(name, self.regressions)
        self.assertIn("Assert.Equal(0, host.Payloads.ReadCount)", self.regressions)
        self.assertIn('No immutable publication binding was pinned before execution.', self.regressions)

    def test_documentation_distinguishes_snapshot_identity_and_host_project_configuration(self) -> None:
        readme = (MATRIX / "README.md").read_text(encoding="utf-8-sig")
        self.assertIn("### Kubernetes external-SDK RBAC project alignment", readme)
        self.assertIn("Multiplexed.Rbac.Core__Project", readme)
        self.assertIn("Snapshot.Project does not configure TrnBuilder", readme)
        self.assertIn("HttpKubernetesPoolExternalSdkRbacTests", readme)


if __name__ == "__main__":
    unittest.main()
