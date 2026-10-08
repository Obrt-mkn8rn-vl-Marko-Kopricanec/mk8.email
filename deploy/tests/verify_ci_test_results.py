#!/usr/bin/env python3
"""Refuse missing, empty, incomplete or inconsistent CI test reports."""

import argparse
from pathlib import Path
from xml.etree import ElementTree


NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
NS = {"t": NAMESPACE}
SUITES = ("application", "infrastructure", "messaging")
COUNTERS = (
    "total", "executed", "passed", "failed", "error", "timeout", "aborted",
    "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted",
    "disconnected", "warning", "completed", "inProgress", "pending",
)


def verify_report(path):
    root = ElementTree.parse(path).getroot()
    if root.tag != f"{{{NAMESPACE}}}TestRun":
        raise ValueError(f"Not a TRX test run: {path}")
    summaries = root.findall("t:ResultSummary", NS)
    if len(summaries) != 1 or summaries[0].get("outcome") != "Completed":
        raise ValueError(f"Test run did not complete: {path}")
    counter_nodes = summaries[0].findall("t:Counters", NS)
    if len(counter_nodes) != 1:
        raise ValueError(f"Missing or ambiguous counters: {path}")
    counters = {name: int(counter_nodes[0].attrib[name]) for name in COUNTERS}
    total = counters["total"]
    if total <= 0 or any(counters[name] != total for name in ("executed", "passed")):
        raise ValueError(f"Empty or incomplete test run: {path}")
    if any(counters[name] for name in COUNTERS if name not in ("total", "executed", "passed")):
        raise ValueError(f"Tests failed or did not execute: {path}")
    result_nodes = root.findall("t:Results", NS)
    if len(result_nodes) != 1:
        raise ValueError(f"Missing or ambiguous results: {path}")
    results = list(result_nodes[0])
    if len(results) != total or any(node.tag != f"{{{NAMESPACE}}}UnitTestResult" or node.get("outcome") != "Passed" for node in results):
        raise ValueError(f"Actual outcomes disagree with counters: {path}")
    identities = [(node.get("testId"), node.get("executionId")) for node in results]
    if any(not test or not execution for test, execution in identities) or len({test for test, _ in identities}) != total or len({execution for _, execution in identities}) != total:
        raise ValueError(f"Missing or duplicate result identities: {path}")
    definitions = root.findall("t:TestDefinitions/t:UnitTest", NS)
    defined = [(node.get("id"), node.find("t:Execution", NS).get("id") if node.find("t:Execution", NS) is not None else None) for node in definitions]
    entries = root.findall("t:TestEntries/t:TestEntry", NS)
    entered = [(node.get("testId"), node.get("executionId")) for node in entries]
    if len(defined) != total or len(entered) != total or set(defined) != set(identities) or set(entered) != set(identities):
        raise ValueError(f"Test definitions/entries disagree with results: {path}")
    if summaries[0].findall("t:RunInfos/t:RunInfo[@outcome='Error']", NS):
        raise ValueError(f"Test run reported a runner error: {path}")
    return total


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument("suite", nargs="?", choices=SUITES)
    arguments = parser.parse_args()
    for suite in (arguments.suite,) if arguments.suite else SUITES:
        report = arguments.results / (suite + ".trx")
        try:
            total = verify_report(report)
        except (OSError, ValueError, KeyError, ElementTree.ParseError) as error:
            raise SystemExit(f"Invalid CI evidence for {suite}: {error}") from error
        print(f"{suite}: {total} individual tests passed; complete TRX verified.")


if __name__ == "__main__":
    main()
