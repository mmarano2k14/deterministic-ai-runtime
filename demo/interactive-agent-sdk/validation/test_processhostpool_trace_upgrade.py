from __future__ import annotations

import importlib.util
import re
import unittest
from pathlib import Path

PATCHER = Path(__file__).with_name('apply_processhostpool_bridge.py')
_spec = importlib.util.spec_from_file_location('trace_upgrade_patcher', PATCHER)
assert _spec and _spec.loader
patcher = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(patcher)

# Synthetic C# fixture for the exact original injection boundaries.
BASE = '''namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    internal sealed class Child
    {
        public Child()
        {
            if (redirectOutput)
            {
                this.standardOutputDrain =
                    process.StandardOutput.ReadToEndAsync();

                this.standardErrorDrain =
                    process.StandardError.ReadToEndAsync();
            }
        }

        /// <summary>
        /// Observes process completion and creates one typed completion result.
        /// </summary>
        private void Complete() { }
    }
}
'''
ORIGINAL_TERMS = ('plan', 'openai', 'exception', 'error', 'fail', 'retry', 'invocation')
OLDER_EMITTER = '$"[INTERACTIVE CHILD TRACE] RuntimeInstanceId={runtimeInstanceId} Stream={stream} {line}"'
CURRENT_EMITTER = ('$"[INTERACTIVE CHILD TRACE] ObservedAtUtc={System.DateTimeOffset.UtcNow:O} '
                   'RuntimeInstanceId={runtimeInstanceId} Stream={stream} {line}"')


def legacy_source(*, mongo: bool = False, qualified: bool = True) -> str:
    current = patcher._inject_child_runtime_trace(BASE)
    terms = ORIGINAL_TERMS + ((
        'MONGO SNAPSHOT STORE', 'AiEngine__Snapshots',
        'ConnectionStrings__Mongo', 'Mongo__DatabaseName',
    ) if mongo else ())
    body = 'return ' + ' ||\n                   '.join(
        f'line.Contains("{term}", StringComparison.OrdinalIgnoreCase)' for term in terms
    ) + ';'
    current = re.sub(
        r'(private static bool IsInteractiveDiagnosticLine\(string line\)\s*\{)[^{}]*(\})',
        lambda m: m.group(1) + '\n            ' + body + '\n        ' + m.group(2),
        current,
    ).replace(CURRENT_EMITTER, OLDER_EMITTER)
    if not qualified:
        current = current.replace('System.Environment.GetEnvironmentVariable(',
                                  'Environment.GetEnvironmentVariable(')
    return current


class TraceUpgradeTests(unittest.TestCase):
    def assert_current(self, source: str) -> None:
        self.assertIn('System.Environment.GetEnvironmentVariable(', source)
        self.assertNotIn('System.System.Environment', source)
        for term in patcher._CHILD_DIAGNOSTIC_TERMS:
            self.assertIn(f'line.Contains("{term}", StringComparison.OrdinalIgnoreCase)', source)
        self.assertEqual(source.count('private static bool IsInteractiveDiagnosticLine('), 1)
        self.assertEqual(source.count('private static async Task<string> DrainInteractiveTraceAsync('), 1)
        self.assertIn(CURRENT_EMITTER, source)
        self.assertIn(': process.StandardOutput.ReadToEndAsync();', source)
        self.assertIn(': process.StandardError.ReadToEndAsync();', source)
        self.assertNotIn('line.Contains("ConnectionStrings__Mongo"', source)
        self.assertNotIn('line.Contains("Mongo__DatabaseName"', source)

    def test_fresh_install_and_repeat(self):
        once = patcher._inject_child_runtime_trace(BASE)
        self.assert_current(once)
        self.assertEqual(once, patcher._inject_child_runtime_trace(once))

    def test_pre_mongo_trace_upgrade_with_namespace_hotfix(self):
        old = legacy_source(qualified=True)
        updated = patcher._inject_child_runtime_trace(old)
        self.assertNotEqual(updated, old)
        self.assert_current(updated)
        self.assertEqual(updated, patcher._inject_child_runtime_trace(updated))

    def test_pre_mongo_trace_upgrade_without_namespace_hotfix(self):
        updated = patcher._inject_child_runtime_trace(legacy_source(qualified=False))
        self.assert_current(updated)

    def test_fresh_mongo_trace_upgrade(self):
        updated = patcher._inject_child_runtime_trace(legacy_source(mongo=True, qualified=False))
        self.assert_current(updated)
        self.assertEqual(updated, patcher._inject_child_runtime_trace(updated))

    def test_marker_without_helpers_fails_closed(self):
        with self.assertRaisesRegex(RuntimeError, 'No diagnostic patch applied'):
            patcher._inject_child_runtime_trace(BASE + '// MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE')

    def test_unknown_predicate_edit_fails_closed(self):
        altered = legacy_source().replace('line.Contains("plan", StringComparison.OrdinalIgnoreCase)',
                                         'line.Contains("custom-user-rule", StringComparison.OrdinalIgnoreCase)')
        with self.assertRaisesRegex(RuntimeError, 'unrecognized edits'):
            patcher._inject_child_runtime_trace(altered)

    def test_unknown_predicate_code_fails_closed(self):
        altered = legacy_source().replace('return line.Contains', 'return CustomCheck(line) || line.Contains')
        with self.assertRaisesRegex(RuntimeError, 'unrecognized edits'):
            patcher._inject_child_runtime_trace(altered)

    def test_ambiguous_predicate_fails_closed(self):
        altered = legacy_source() + '\nprivate static bool IsInteractiveDiagnosticLine(string line) { return true; }'
        with self.assertRaisesRegex(RuntimeError, 'ambiguous'):
            patcher._inject_child_runtime_trace(altered)

    def test_snapshot_lines_match_after_upgrade(self):
        old = legacy_source()
        updated = patcher._inject_child_runtime_trace(old)
        for source, expected in ((old, False), (updated, True)):
            body = re.search(r'private static bool IsInteractiveDiagnosticLine\(string line\)\s*\{([^{}]*)\}', source).group(1)
            terms = re.findall(r'line.Contains\("([^"]+)"', body)
            for line in (
                '[MONGO SNAPSHOT STORE] Database=runtime',
                '[MONGO SNAPSHOT STORE UPSERT] ExecutionId=published-test Status=Completed',
                '[MONGO SNAPSHOT STORE UPSERT RESULT] MatchedCount=0 UpsertedId=test',
                '[AI SNAPSHOT] Skipped for execution because snapshots are disabled.',
            ):
                self.assertEqual(any(term.casefold() in line.casefold() for term in terms), expected, line)

    def test_repeated_upgrade_does_not_duplicate_helpers(self):
        source = legacy_source()
        for _ in range(10):
            source = patcher._inject_child_runtime_trace(source)
        self.assert_current(source)


if __name__ == '__main__':
    unittest.main(verbosity=2)
