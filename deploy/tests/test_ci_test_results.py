"""Exercise the real CI evidence verifier, including forged green counters."""

import copy
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from xml.etree import ElementTree as ET

from verify_ci_test_results import COUNTERS, NAMESPACE, NS, verify_report


def report():
    root = ET.Element(f"{{{NAMESPACE}}}TestRun", id="run")
    results = ET.SubElement(root, f"{{{NAMESPACE}}}Results")
    definitions = ET.SubElement(root, f"{{{NAMESPACE}}}TestDefinitions")
    entries = ET.SubElement(root, f"{{{NAMESPACE}}}TestEntries")
    for index in range(2):
        test, execution = f"test{index}", f"execution{index}"
        ET.SubElement(results, f"{{{NAMESPACE}}}UnitTestResult", testId=test,
                      executionId=execution, testName=test, outcome="Passed")
        definition = ET.SubElement(definitions, f"{{{NAMESPACE}}}UnitTest", id=test)
        ET.SubElement(definition, f"{{{NAMESPACE}}}Execution", id=execution)
        ET.SubElement(entries, f"{{{NAMESPACE}}}TestEntry", testId=test, executionId=execution)
    summary = ET.SubElement(root, f"{{{NAMESPACE}}}ResultSummary", outcome="Completed")
    counters = {name: "0" for name in COUNTERS}
    counters.update(total="2", executed="2", passed="2")
    ET.SubElement(summary, f"{{{NAMESPACE}}}Counters", counters)
    return root


class TestEvidenceTests(unittest.TestCase):
    def check(self, root):
        with tempfile.TemporaryDirectory(prefix="mk8-ci-trx-") as temporary:
            path = Path(temporary) / "test.trx"
            ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
            return verify_report(path)

    def test_complete_individual_results_pass(self):
        self.assertEqual(2, self.check(report()))

    def test_empty_inconsistent_or_bad_counters_refuse(self):
        for name in COUNTERS:
            for value in ("-1", "invalid", "1", "3"):
                with self.subTest(name=name, value=value):
                    root = report()
                    root.find("t:ResultSummary/t:Counters", NS).set(name, value)
                    with self.assertRaises((ValueError, KeyError)):
                        self.check(root)
        for name in ("total", "executed", "passed", "failed"):
            with self.subTest(missing=name):
                root = report()
                del root.find("t:ResultSummary/t:Counters", NS).attrib[name]
                with self.assertRaises(KeyError):
                    self.check(root)
        root = report()
        counters = root.find("t:ResultSummary/t:Counters", NS)
        counters.set("total", "0")
        counters.set("passed", "0")
        counters.set("executed", "0")
        with self.assertRaises(ValueError):
            self.check(root)

    def test_green_counters_cannot_hide_nonpassed_individual_outcomes(self):
        for outcome in ("Failed", "NotExecuted", "Skipped", "Timeout", "Error", "Inconclusive", "PassedButRunAborted", ""):
            with self.subTest(outcome=outcome):
                root = report()
                root.find("t:Results/t:UnitTestResult", NS).set("outcome", outcome)
                with self.assertRaises(ValueError):
                    self.check(root)

    def test_missing_extra_unknown_or_duplicate_result_nodes_refuse(self):
        for mode in ("missing", "extra", "unknown", "duplicate-test", "duplicate-execution", "empty-test", "empty-execution"):
            with self.subTest(mode=mode):
                root = report()
                results = root.find("t:Results", NS)
                if mode == "missing":
                    results.remove(results[0])
                elif mode == "extra":
                    results.append(copy.deepcopy(results[0]))
                elif mode == "unknown":
                    results[0].tag = f"{{{NAMESPACE}}}UnknownResult"
                else:
                    name = "testId" if mode.endswith("test") else "executionId"
                    results[0].set(name, "" if mode.startswith("empty") else results[1].get(name))
                with self.assertRaises(ValueError):
                    self.check(root)

    def test_definitions_and_entries_must_bind_every_result_once(self):
        for container, child, attribute in (("TestDefinitions", "UnitTest", "id"), ("TestEntries", "TestEntry", "testId")):
            for mode in ("missing", "extra", "identity", "execution"):
                with self.subTest(container=container, mode=mode):
                    root = report()
                    nodes = root.find("t:" + container, NS)
                    if mode == "missing":
                        nodes.remove(nodes[0])
                    elif mode == "extra":
                        nodes.append(copy.deepcopy(nodes[0]))
                    elif mode == "identity":
                        nodes.find("t:" + child, NS).set(attribute, "foreign")
                    elif container == "TestDefinitions":
                        nodes[0].find("t:Execution", NS).set("id", "foreign")
                    else:
                        nodes[0].set("executionId", "foreign")
                    with self.assertRaises(ValueError):
                        self.check(root)

    def test_run_completion_namespace_and_container_cardinality_are_required(self):
        for mode in ("namespace", "missing-summary", "duplicate-summary", "aborted", "missing-counters", "duplicate-counters", "missing-results", "duplicate-results", "runner-error"):
            with self.subTest(mode=mode):
                root = report()
                summary = root.find("t:ResultSummary", NS)
                if mode == "namespace":
                    root.tag = "TestRun"
                elif mode == "missing-summary":
                    root.remove(summary)
                elif mode == "duplicate-summary":
                    root.append(copy.deepcopy(summary))
                elif mode == "aborted":
                    summary.set("outcome", "Aborted")
                elif mode == "missing-counters":
                    summary.remove(summary[0])
                elif mode == "duplicate-counters":
                    summary.append(copy.deepcopy(summary[0]))
                elif mode == "missing-results":
                    root.remove(root.find("t:Results", NS))
                elif mode == "duplicate-results":
                    root.append(copy.deepcopy(root.find("t:Results", NS)))
                else:
                    infos = ET.SubElement(summary, f"{{{NAMESPACE}}}RunInfos")
                    ET.SubElement(infos, f"{{{NAMESPACE}}}RunInfo", outcome="Error")
                with self.assertRaises(ValueError):
                    self.check(root)

    def test_cli_requires_each_suite_and_does_not_accept_selected_union(self):
        verifier = Path(__file__).with_name("verify_ci_test_results.py")
        with tempfile.TemporaryDirectory(prefix="mk8-ci-cli-") as temporary:
            root = Path(temporary)
            argv = [sys.executable, str(verifier), str(root)]
            for name in ("application", "infrastructure", "messaging"):
                result = subprocess.run(argv, capture_output=True, timeout=10)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(name.encode(), result.stderr)
                ET.ElementTree(report()).write(root / (name + ".trx"), encoding="utf-8")
            result = subprocess.run(argv, capture_output=True, timeout=10)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(3, result.stdout.count(b"complete TRX verified"))
            (root / "application.trx").write_text("truncated XML")
            self.assertNotEqual(0, subprocess.run(argv, capture_output=True, timeout=10).returncode)
            selected = subprocess.run([*argv, "messaging"], capture_output=True, timeout=10)
            self.assertEqual(0, selected.returncode, selected.stderr)
            self.assertEqual(1, selected.stdout.count(b"complete TRX verified"))
            invalid = subprocess.run([*argv, "unknown"], capture_output=True, timeout=10)
            self.assertNotEqual(0, invalid.returncode)


if __name__ == "__main__":
    unittest.main()
