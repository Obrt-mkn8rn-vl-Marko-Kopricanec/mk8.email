"""Exercise the real workflow analyzer block with controlled dotnet stand-ins."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


REPOSITORY = Path(__file__).resolve().parents[2]


def analyzer_block():
    workflow = (REPOSITORY / ".github/workflows/ci.yml").read_text()
    step = workflow.split("      - name: Enforce all analyzer gates\n", 1)[1]
    body = step.split("        run: |\n", 1)[1].split("\n      - name:", 1)[0]
    return "\n".join(line[10:] for line in body.splitlines() if line.strip()) + "\n"


class AnalyzerGateTests(unittest.TestCase):
    def run_gate(self, project_count, fail_project=None):
        with tempfile.TemporaryDirectory(prefix="mk8-analyzer-gate-") as temporary:
            root = Path(temporary)
            tools = root / "tools"
            tools.mkdir()
            log = root / "calls.jsonl"
            dotnet = tools / "dotnet"
            dotnet.write_text(f"#!{sys.executable}\n" + """
import json, os, sys
with open(os.environ['MK8_GATE_TEST_LOG'], 'a') as output:
    output.write(json.dumps(sys.argv[1:]) + '\\n')
if sys.argv[2] == os.environ.get('MK8_GATE_FAIL_PROJECT'):
    sys.exit(23)
""")
            dotnet.chmod(0o700)
            projects = []
            for index in range(project_count):
                name = f"project{index:02d}"
                folder = root / name
                folder.mkdir()
                # A missing style marker must not silently remove an actual project.
                (folder / (name + ".csproj")).write_text("<Project />\n")
                projects.append(f"{name}/{name}.csproj")
            environment = dict(os.environ, PATH=str(tools), MK8_GATE_TEST_LOG=str(log))
            if fail_project is not None:
                environment["MK8_GATE_FAIL_PROJECT"] = projects[fail_project]
            result = subprocess.run(["/usr/bin/bash", "--noprofile", "--norc", "-c", analyzer_block()],
                                    cwd=root, env=environment, capture_output=True, text=True, timeout=10)
            calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
            return result, projects, calls

    def test_all_twenty_projects_run_without_ripgrep_or_style_filter(self):
        result, projects, calls = self.run_gate(20)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("Analyzing 20 projects.", result.stdout)
        self.assertEqual(projects, [call[1] for call in calls])
        for call in calls:
            self.assertEqual("build", call[0])
            self.assertIn("--target:Rebuild", call)
            self.assertIn("--property:RunAnalyzers=true", call)
            self.assertIn("--property:TreatWarningsAsErrors=true", call)
            self.assertIn("--property:ContinuousIntegrationBuild=true", call)

    def test_empty_project_set_fails_closed(self):
        result, _, calls = self.run_gate(0)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], calls)

    def test_failed_analyzer_build_stops_the_gate(self):
        result, projects, calls = self.run_gate(20, fail_project=4)
        self.assertEqual(23, result.returncode)
        self.assertEqual(projects[:5], [call[1] for call in calls])


if __name__ == "__main__":
    unittest.main()
