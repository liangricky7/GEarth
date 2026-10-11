"""Synthetic workflow tests: failed remote analysis must never publish a report."""
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from analytics.export import export_run
from analytics.report import build_report
from analytics.tests.test_pipeline import fake_fetch, fixture
from ops.analyze_run import DatabricksCli, run_analysis


class FakeCli:
    def __init__(self, success):
        self.success = success
        self.calls = []
        self.report = None

    def call(self, *args):
        self.calls.append(args)
        if args[:2] == ("current-user", "me"):
            return {"userName": "synthetic@example.test"}
        if args[:2] == ("jobs", "submit"):
            return {"run_id": 123}
        if args[:2] == ("jobs", "get-run"):
            return {"state": {"life_cycle_state": "TERMINATED", "result_state": "SUCCESS" if self.success else "FAILED"}}
        if args[:2] == ("fs", "cp") and str(args[2]).startswith("dbfs:"):
            Path(args[3]).write_text(json.dumps(self.report))
        return None


class AnalyzeRunTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.destination = Path(self.temp.name) / "export"

    def tearDown(self):
        self.temp.cleanup()

    def export(self, api, run_id, destination):
        metadata, records = fixture()
        return export_run(api, run_id, destination, fetch=fake_fetch(metadata, records))

    def test_failed_databricks_job_never_downloads_or_publishes(self):
        cli = FakeCli(False)
        with patch("ops.analyze_run.export_run", side_effect=self.export), patch("ops.analyze_run.subprocess.run") as publish:
            with self.assertRaisesRegex(RuntimeError, "did not succeed"):
                run_analysis("synthetic-test-run", self.destination, api="http://localhost", cli=cli, storage="local")
            publish.assert_not_called()
        self.assertTrue((self.destination / "manifest.json").exists())
        self.assertFalse((self.destination / "report.json").exists())

    def test_success_requires_matching_website_manifest(self):
        cli = FakeCli(True)

        def exported(*args):
            result = self.export(*args)
            cli.report = build_report(self.destination / "manifest.json")
            return result

        def website(*args, **kwargs):
            return io.BytesIO(json.dumps({"run_id": "synthetic-test-run", "source_manifest_sha256": "0" * 64}).encode())

        with patch("ops.analyze_run.export_run", side_effect=exported), patch("ops.analyze_run.subprocess.run", return_value=subprocess.CompletedProcess([], 0)), patch("urllib.request.urlopen", side_effect=website):
            with self.assertRaisesRegex(RuntimeError, "different report"):
                run_analysis("synthetic-test-run", self.destination, api="http://localhost", cli=cli, storage="local")

    def test_cli_errors_do_not_echo_secrets(self):
        leaked = "postgresql://user:private-password@database.example/db"
        with patch("ops.analyze_run.subprocess.run", return_value=subprocess.CompletedProcess([], 1, stdout=leaked, stderr=leaked)):
            with self.assertRaises(RuntimeError) as error:
                DatabricksCli("databricks", "synthetic").call("jobs", "submit")
            self.assertNotIn(leaked, str(error.exception))
            self.assertNotIn("private-password", str(error.exception))


if __name__ == "__main__":
    unittest.main()
