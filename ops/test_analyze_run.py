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


class BrowseAnalyzeRunTests(AnalyzeRunTests):
    """Downloaded actual-row fixtures must qualify before local publication."""
    def prepare_cli(self, *, corrupt=False, unsafe=False):
        import hashlib
        from analytics.browse import DATASETS, catalog, stream_dataset
        from analytics.export import canonical
        cli = FakeCli(True)
        cli.browse_files = {}
        original = cli.call

        def exported(*args):
            result = self.export(*args)
            cli.report = build_report(self.destination / "manifest.json")
            report = cli.report
            bundle = Path(self.temp.name) / "actual-browse"
            bundle.mkdir()
            source = report["source_manifest_sha256"]
            rows = [dict(run_id=report["run_id"], source_manifest_sha256=source, payload_json='{"new":false}', real_column=None)]
            columns = [dict(name=name, data_type="string", nullable=True, non_null_count=int(rows[0][name] is not None)) for name in rows[0]]
            entry = stream_dataset((canonical(row) + b"\n" for row in rows), bundle,
                "silver.agent_daily", DATASETS["silver.agent_daily"], columns)
            entries = [entry] + [dict(dataset_id=identity, table_name=table, availability="unavailable",
                unavailable_reason="table_not_present", row_count=None, columns=[], payload_fields=[],
                field_coverage_scope="unavailable", chunks=[]) for identity, table in DATASETS.items() if identity != "silver.agent_daily"]
            manifest = dict(schema_version="gearth.analytics-browse.v1", **{key: report[key] for key in
                ("run_id", "through_revision", "through_sequence", "source_manifest_sha256", "generated_at")},
                qualification_status="pass", datasets=entries)
            chunk = entry["chunks"][0]
            if unsafe:
                chunk["path"] = "../unsafe.gz"
            data = canonical(manifest) + b"\n"
            cli.browse_files["browse-manifest.json"] = data
            if not unsafe:
                cli.browse_files[chunk["path"]] = (bundle / chunk["path"]).read_bytes() + (b"corrupt" if corrupt else b"")
            report["dataset_catalog"] = catalog(manifest, hashlib.sha256(data).hexdigest())
            return result

        def call(*args):
            if args[:2] == ("fs", "cp") and str(args[2]).startswith("dbfs:") and "/browse-" in str(args[2]):
                cli.calls.append(args)
                suffix = str(args[2]).split("/browse-", 1)[1].split("/", 1)[1]
                Path(args[3]).write_bytes(cli.browse_files[suffix])
                return None
            return original(*args)
        cli.call = call
        return cli, exported

    def test_browse_download_then_publish_and_verify_both_apis(self):
        cli, exported = self.prepare_cli()
        endpoints = []
        def website(url, **kwargs):
            endpoints.append(url)
            if url.endswith("/datasets"):
                catalog = cli.report["dataset_catalog"]
                value = dict(run_id=cli.report["run_id"], status="available", dataset_catalog=catalog,
                    bundle_manifest_sha256=catalog["bundle_manifest_sha256"])
            else:
                value = cli.report
            return io.BytesIO(json.dumps(value).encode())
        with patch("ops.analyze_run.export_run", side_effect=exported), patch("ops.analyze_run.subprocess.run",
                return_value=subprocess.CompletedProcess([], 0)) as publisher, patch("urllib.request.urlopen", side_effect=website):
            report = run_analysis("synthetic-test-run", self.destination, api="http://localhost", cli=cli,
                storage="local", analytics_browse=True)
        self.assertIn("--analytics-browse", publisher.call_args.args[0])
        self.assertTrue(endpoints[-1].endswith("/datasets"))
        self.assertEqual(report, cli.report)
        job = json.loads((self.destination / "databricks-job.json").read_text())
        self.assertEqual(job["tasks"][0]["notebook_task"]["base_parameters"]["publish_browse"], "true")

    def test_corrupt_chunks_or_unsafe_manifest_never_publish(self):
        for options in ({"corrupt": True}, {"unsafe": True}):
            with self.subTest(options=options):
                # Reset synthetic directories per workflow; exports are immutable.
                self.tearDown()
                self.setUp()
                cli, exported = self.prepare_cli(**options)
                with patch("ops.analyze_run.export_run", side_effect=exported), patch("ops.analyze_run.subprocess.run") as publisher:
                    with self.assertRaises(ValueError):
                        run_analysis("synthetic-test-run", self.destination, api="http://localhost", cli=cli,
                            storage="local", analytics_browse=True)
                    publisher.assert_not_called()
                if options.get("unsafe"):
                    downloads = [args for args in cli.calls if args[:2] == ("fs", "cp") and "/browse-" in str(args[2])]
                    self.assertEqual(len(downloads), 1)  # no unsafe chunk request

    def test_different_observer_cache_cannot_report_success(self):
        cli, exported = self.prepare_cli()
        def website(url, **kwargs):
            value = dict(run_id="synthetic-test-run", status="report_only", dataset_catalog=None) if url.endswith("/datasets") else cli.report
            return io.BytesIO(json.dumps(value).encode())
        with patch("ops.analyze_run.export_run", side_effect=exported), patch("ops.analyze_run.subprocess.run",
                return_value=subprocess.CompletedProcess([], 0)), patch("urllib.request.urlopen", side_effect=website):
            with self.assertRaisesRegex(RuntimeError, "dataset cache"):
                run_analysis("synthetic-test-run", self.destination, api="http://localhost", cli=cli,
                    storage="local", analytics_browse=True)
