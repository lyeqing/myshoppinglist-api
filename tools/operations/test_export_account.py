import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from subprocess import CompletedProcess
from export_account import export_sql, prepare


class ExportTests(unittest.TestCase):
    def test_schema_and_identity_are_validated_and_query_is_read_only(self):
        for schema, account in [('public"; DROP SCHEMA public;--', 1), ("public", -1), ("public", 0), ("public", 2**64)]:
            with self.assertRaises(ValueError):
                export_sql(schema, account)
        sql = export_sql("public", 42)
        self.assertIn("REPEATABLE READ READ ONLY", sql)
        self.assertIn('WHERE "UserAccountId"=42', sql)
        self.assertIn("password|token|claim|hash|salt", sql)
        self.assertNotIn("OpenIddictTokens", sql)

    def test_missing_account_never_writes_a_file_and_existing_exports_are_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "export.json"
            with patch("export_account.subprocess.run", return_value=CompletedProcess([], 0, '{"table":"Account","rows":[]}\n', '')):
                with self.assertRaises(ValueError):
                    prepare("psql", "public", 1, output)
            self.assertFalse(output.exists())
            with patch("export_account.subprocess.run", return_value=CompletedProcess([], 0, '{"table":"Account","rows":[{"Id":1}]}\n', '')):
                result = prepare("psql", "public", 1, output)
                self.assertTrue(result["supportReviewRequired"])
                with self.assertRaises(FileExistsError):
                    prepare("psql", "public", 1, output)


if __name__ == "__main__":
    unittest.main()
