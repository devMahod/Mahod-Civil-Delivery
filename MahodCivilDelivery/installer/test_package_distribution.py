"""Reject stale or synthetic installer evidence before any distribution writes."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("package_distribution", Path(__file__).with_name("package-distribution.py"))
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class ReceiptTests(unittest.TestCase):
    def setUp(self):
        self.receipt = {
            "schema": "mahod-setup-payload-verification/v1", "synthetic": False,
            "version": "1.4.2", "command": "--self-test-verify-payload-dir", "exitCode": 0,
            "setupSha256": "A" * 64, "payloadManifestSha256": "B" * 64, "guideSha256": "C" * 64,
        }

    def validate(self, receipt):
        package.validate_receipt(receipt, "1.4.2", "a" * 64, "b" * 64, "c" * 64)

    def test_current_real_receipt_accepted(self):
        self.validate(self.receipt)

    def test_stale_artifact_or_invalid_receipt_rejected(self):
        replacements = {"schema": "unknown", "synthetic": True, "version": "1.4.1",
                        "command": "synthetic-check", "exitCode": 1,
                        "setupSha256": "D" * 64, "payloadManifestSha256": "D" * 64,
                        "guideSha256": "D" * 64}
        for field, value in replacements.items():
            with self.subTest(field=field):
                with self.assertRaises(ValueError):
                    self.validate(dict(self.receipt, **{field: value}))

    def test_missing_evidence_rejected(self):
        for field in self.receipt:
            with self.subTest(field=field):
                incomplete = dict(self.receipt)
                del incomplete[field]
                with self.assertRaises(ValueError):
                    self.validate(incomplete)


if __name__ == "__main__":
    unittest.main()
