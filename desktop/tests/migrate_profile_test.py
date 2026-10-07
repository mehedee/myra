"""Run with: python3 -m unittest discover -s tests -p '*_test.py'."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "migration", Path(__file__).resolve().parents[1] / "build/migrate-profile.py"
)
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)


class ProfileMigrationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "previous"
        self.source.mkdir()
        self.destination = self.root / "Myra"
        self.payload = {"Categories": [{"Name": "Media"}], "Settings": {"DownloadDirectory": "/existing/media"}}
        (self.source / "previous.json").write_text(json.dumps(self.payload))

    def test_copies_history_journals_and_preserves_configured_paths_and_original(self):
        (self.source / "LibraryIndex.sqlite").write_bytes(b"index fixture")
        (self.source / "LibraryIndex.sqlite-wal").write_bytes(b"journal fixture")
        (self.source / "PlayerPreferences.json").write_text('{"resume":42}')
        migration.migrate(self.source, self.destination, "previous.json")
        self.assertEqual(json.loads((self.destination / "Myra.json").read_text()), self.payload)
        self.assertEqual((self.destination / "LibraryIndex.sqlite-wal").read_bytes(), b"journal fixture")
        self.assertEqual((self.destination / "PlayerPreferences.json").read_text(), '{"resume":42}')
        self.assertTrue((self.source / "previous.json").exists())
        self.assertFalse((self.destination / "previous.json").exists())

    def test_existing_profile_is_never_overwritten(self):
        self.destination.mkdir()
        sentinel = self.destination / "keep.txt"
        sentinel.write_text("existing profile")
        with self.assertRaises(FileExistsError):
            migration.migrate(self.source, self.destination, "previous.json")
        self.assertEqual(sentinel.read_text(), "existing profile")

    def test_invalid_json_does_not_publish_partial_profile(self):
        (self.source / "previous.json").write_text("broken")
        with self.assertRaises(json.JSONDecodeError):
            migration.migrate(self.source, self.destination, "previous.json")
        self.assertFalse(self.destination.exists())

    def test_symbolic_links_are_rejected(self):
        (self.source / "link").symlink_to(self.source / "previous.json")
        with self.assertRaises(ValueError):
            migration.migrate(self.source, self.destination, "previous.json")
        self.assertFalse(self.destination.exists())

    def test_nested_destination_and_traversal_filename_are_rejected(self):
        with self.assertRaises(ValueError):
            migration.migrate(self.source, self.source / "Myra", "previous.json")
        with self.assertRaises(ValueError):
            migration.migrate(self.source, self.destination, "../previous.json")
        self.assertFalse(self.destination.exists())
