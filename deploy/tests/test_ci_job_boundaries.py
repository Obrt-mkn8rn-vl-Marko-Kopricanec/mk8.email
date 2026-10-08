"""Check the actual workflow's deadlines, prerequisite fence and failure paths."""

import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest


REPOSITORY = Path(__file__).resolve().parents[2]
WORKFLOW = (REPOSITORY / ".github/workflows/ci.yml").read_text()


def job(name):
    return re.split(r"\n  [a-z][a-z-]*:\n", WORKFLOW.split(f"  {name}:\n", 1)[1], maxsplit=1)[0]


def step(job_name, name):
    return job(job_name).split(f"      - name: {name}\n", 1)[1].split("\n      - name:", 1)[0]


def script(job_name, name):
    body = step(job_name, name).split("        run: |\n", 1)[1]
    return "\n".join(line[10:] for line in body.splitlines() if line.strip()) + "\n"


class JobBoundaryTests(unittest.TestCase):
    def test_independent_jobs_keep_existing_deadlines_and_required_check_name(self):
        self.assertEqual(["validate", "test", "build-test-package"],
                         re.findall(r"^  ([a-z][a-z-]*):$", WORKFLOW.split("\njobs:\n", 1)[1], re.MULTILINE))
        for name in ("validate", "test", "build-test-package"):
            self.assertIn("    timeout-minutes: 30\n", job(name))
            self.assertIn("    runs-on: ubuntu-24.04\n", job(name))
            self.assertIn("dotnet restore mk8.email.slnx --locked-mode", job(name))
            self.assertIn("--property:TreatWarningsAsErrors=true", job(name))
            self.assertIn("--property:ContinuousIntegrationBuild=true", job(name))
        self.assertNotIn("continue-on-error", WORKFLOW)
        self.assertNotIn("dotnet test", job("validate"))
        self.assertNotIn("dotnet test", job("build-test-package"))
        self.assertNotIn("dotnet publish", job("test"))
        self.assertIn("--property:RunAnalyzers=true", job("validate"))

    def test_matrix_runs_all_suites_and_failure_does_not_cancel_siblings(self):
        self.assertIn("      fail-fast: false\n", job("test"))
        self.assertEqual([("application", "Application"), ("infrastructure", "Infrastructure"), ("messaging", "Messaging")],
                         re.findall(r"- suite: (\w+)\n\s+project: mk8.email.(\w+).Tests/mk8.email.\w+.Tests.csproj", job("test")))
        self.assertIn("matrix.suite != 'messaging'", step("test", "Test application or infrastructure"))
        messaging = step("test", "Test distributed messaging with PostgreSQL and Azure Blob")
        self.assertIn("matrix.suite == 'messaging'", messaging)
        self.assertIn("--blame-hang-timeout 10m --blame-hang-dump-type none", messaging)
        self.assertIn("MK8_EMAIL_TEST_POSTGRES:", messaging)
        self.assertIn("MK8_EMAIL_TEST_AZURE_BLOB_CONNECTION: UseDevelopmentStorage=true", messaging)
        self.assertIn("MK8_EMAIL_TEST_PG_DUMP: /usr/lib/postgresql/17/bin/pg_dump", messaging)
        self.assertIn("trap 'docker stop", messaging)
        self.assertIn("test \"$azurite_ready\" = true", messaging)
        self.assertNotIn("--filter", job("test"))

    def test_aggregate_check_fails_for_every_nonsuccess_prerequisite(self):
        aggregate = job("build-test-package")
        self.assertIn("    needs: [validate, test]\n", aggregate)
        self.assertIn("    if: always()\n", aggregate)
        gate = script("build-test-package", "Require successful validation and every test suite")
        for validation in ("success", "failure", "cancelled", "skipped", ""):
            for tests in ("success", "failure", "cancelled", "skipped", ""):
                with self.subTest(validation=validation, tests=tests):
                    environment = dict(os.environ, VALIDATION_RESULT=validation, TEST_RESULT=tests)
                    result = subprocess.run(["/usr/bin/bash", "-euo", "pipefail", "-c", gate],
                                            env=environment, capture_output=True, timeout=10)
                    self.assertEqual(validation == tests == "success", result.returncode == 0)

    def test_test_failure_is_not_masked_by_tee_and_console_evidence_survives(self):
        body = script("test", "Test application or infrastructure")
        body = body.replace("${{ matrix.project }}", "controlled.csproj").replace("${{ matrix.suite }}", "application")
        with tempfile.TemporaryDirectory(prefix="mk8-ci-pipe-") as temporary:
            root = Path(temporary)
            tools = root / "tools"
            tools.mkdir()
            dotnet = tools / "dotnet"
            dotnet.write_text(f"#!{sys.executable}\nimport os,sys\nprint('controlled test output')\nsys.exit(int(os.environ['MK8_PIPE_TEST_EXIT']))\n")
            dotnet.chmod(0o700)
            for code in (0, 23):
                with self.subTest(code=code):
                    environment = dict(os.environ, PATH=str(tools) + os.pathsep + os.defpath, MK8_PIPE_TEST_EXIT=str(code))
                    result = subprocess.run(["/usr/bin/bash", "-c", body], cwd=root, env=environment,
                                            capture_output=True, timeout=10)
                    self.assertEqual(code, result.returncode, result.stderr)
                    self.assertIn("controlled test output", (root / "artifacts/test-results/application.log").read_text())

    def test_raw_evidence_upload_is_unconditional_and_fenced_to_current_run(self):
        save = step("test", "Save test evidence")
        self.assertIn("        if: always()\n", save)
        self.assertIn("name: tests-${{ matrix.suite }}-${{ github.sha }}", save)
        self.assertIn("path: artifacts/test-results/*", save)
        download = step("build-test-package", "Download this run's test evidence")
        self.assertIn("pattern: tests-*-${{ github.sha }}", download)
        self.assertIn("merge-multiple: true", download)
        self.assertNotIn("run-id:", download)
        self.assertNotIn("github-token:", download)
        aggregate = job("build-test-package")
        self.assertLess(aggregate.index("Require all three complete passing reports"), aggregate.index("Publish management command"))
        self.assertIn("python3 deploy/tests/verify_ci_test_results.py artifacts/test-results\n", aggregate)
        self.assertIn('artifacts/test-results "${{ matrix.suite }}"', job("test"))
        for action in re.findall(r"uses: (\S+)", WORKFLOW):
            self.assertRegex(action, r"^[\w/-]+@[0-9a-f]{40}$")

    def test_all_release_components_and_existing_archive_gate_remain_mandatory(self):
        aggregate = job("build-test-package")
        for name, project in (("management command", "mk8.email.CLI/mk8.email.Application.CLI.csproj"),
                              ("administrator dashboard", "mk8.email.Gateway/mk8.email.Gateway.csproj"),
                              ("Application Worker", "mk8.email.Application.Worker/mk8.email.Application.Worker.csproj"),
                              ("Wake supervisor", "mk8.email.Wake/mk8.email.Wake.csproj")):
            self.assertIn("dotnet publish " + project, step("build-test-package", "Publish " + name))
        packaging = step("build-test-package", "Package release")
        self.assertIn("--directory=artifacts cli admin worker wake", packaging)
        self.assertIn("python3 deploy/tests/release_archive_smoke.py", packaging)
        self.assertIn("sha256sum artifacts/mk8email-linux-x64.tar.gz", packaging)
        self.assertIn("if-no-files-found: error", step("build-test-package", "Save evidence and release"))


if __name__ == "__main__":
    unittest.main()
