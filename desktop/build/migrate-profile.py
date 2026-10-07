#!/usr/bin/env python3
"""Copy an explicitly chosen former desktop profile into Myra without changing it."""
import argparse
import json
from pathlib import Path
import shutil
import tempfile


def migrate(source: Path, destination: Path, store_filename: str) -> None:
    source = source.resolve(strict=True)
    destination = destination.absolute()
    if not source.is_dir():
        raise ValueError("Source must be a profile directory")
    if Path(store_filename).name != store_filename or store_filename in ("", ".", ".."):
        raise ValueError("Store filename must be a single filename")
    if destination.exists() or destination.is_symlink():
        raise FileExistsError("Destination exists; it will not be overwritten")
    if source == destination.resolve() or source in destination.resolve().parents:
        raise ValueError("Destination must be outside the source directory")
    if any(path.is_symlink() for path in source.rglob("*")):
        raise ValueError("Profile contains symbolic links; copy them manually after review")
    store = source / store_filename
    document = json.loads(store.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise ValueError("Profile store must contain a JSON object")
    if store_filename != "Myra.json" and (source / "Myra.json").exists():
        raise FileExistsError("Source already contains Myra.json")
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".myra-profile-", dir=destination.parent))
    try:
        shutil.copytree(source, staging, dirs_exist_ok=True)
        if store_filename != "Myra.json":
            (staging / store_filename).rename(staging / "Myra.json")
        # Both applications must be closed while copying SQLite and its journal files.
        if destination.exists() or destination.is_symlink():
            raise FileExistsError("Destination appeared while copying; aborting")
        staging.rename(destination)
    finally:
        if staging.exists():
            shutil.rmtree(staging)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--store-filename", required=True)
    args = parser.parse_args()
    migrate(args.source, args.destination, args.store_filename)
    print("Profile copied. Original profile and configured download paths are unchanged.")
