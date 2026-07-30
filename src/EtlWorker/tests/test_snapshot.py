from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path
from zoneinfo import ZoneInfo

from etl_worker.service import compute_file_hash, create_or_reuse_snapshot


class SnapshotTests(unittest.TestCase):
    def test_fixed_export_name_creates_and_reuses_hash_snapshot(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            source = root / "source" / "export.xlsx"
            archive = root / "archive"
            source.parent.mkdir()
            source.write_bytes(b"first SAP export")
            os.utime(source, (1_785_366_000, 1_785_366_000))

            file_hash = compute_file_hash(source)
            snapshot, created = create_or_reuse_snapshot(
                source,
                archive,
                file_hash,
                ZoneInfo("Asia/Ho_Chi_Minh"),
            )

            self.assertTrue(created)
            self.assertEqual(source.read_bytes(), snapshot.read_bytes())
            self.assertRegex(
                snapshot.name,
                rf"^export_\d{{8}}_\d{{6}}_{file_hash}\.xlsx$",
            )

            reused_snapshot, created_again = create_or_reuse_snapshot(
                source,
                archive,
                file_hash,
                ZoneInfo("Asia/Ho_Chi_Minh"),
            )
            self.assertFalse(created_again)
            self.assertEqual(snapshot, reused_snapshot)
            self.assertEqual(1, len(list(archive.glob("*.xlsx"))))

    def test_changed_fixed_export_creates_a_second_snapshot(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            source = root / "source" / "export.xlsx"
            archive = root / "archive"
            source.parent.mkdir()

            source.write_bytes(b"day one")
            first_hash = compute_file_hash(source)
            first_snapshot, _ = create_or_reuse_snapshot(
                source, archive, first_hash, ZoneInfo("UTC")
            )

            source.write_bytes(b"day two")
            second_hash = compute_file_hash(source)
            second_snapshot, created = create_or_reuse_snapshot(
                source, archive, second_hash, ZoneInfo("UTC")
            )

            self.assertTrue(created)
            self.assertNotEqual(first_snapshot, second_snapshot)
            self.assertEqual(b"day one", first_snapshot.read_bytes())
            self.assertEqual(b"day two", second_snapshot.read_bytes())
            self.assertEqual(2, len(list(archive.glob("*.xlsx"))))

    def test_timestamped_source_name_is_preserved_in_snapshot_name(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            source = root / "export_20260730_010000.xlsx"
            archive = root / "archive"
            source.write_bytes(b"timestamped export")
            file_hash = compute_file_hash(source)

            snapshot, _ = create_or_reuse_snapshot(
                source, archive, file_hash, ZoneInfo("UTC")
            )

            self.assertEqual(
                f"export_20260730_010000_{file_hash}.xlsx",
                snapshot.name,
            )


if __name__ == "__main__":
    unittest.main()
