"""Build per-file update assets for a GitHub release.

GitHub releases accept any number of assets, so a real per-file update needs no object
storage: the release carries ``update-manifest.json`` plus one ``blob-<sha256>.bin`` per
shipped file, and a client downloads only the blobs whose hash differs from its install.

The file list comes from the package's own ``release-manifest.json`` (written by
``tools/native_portable.py``), never from a directory walk: that keeps user data, private
files and stray build leftovers out of the release.

Every blob referenced by the manifest is written, even when ``--previous`` is given, so a
release is always self-contained. ``--previous`` only annotates how many files are
unchanged, which is useful when writing release notes.

Usage:
    python tools/build_update_assets.py --package 发布包/.../MabiAgent \
        --version 0.6.0 --output build/update-assets --previous old/update-manifest.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
from pathlib import Path, PurePosixPath

APP_ID = "MabiAgent"
BASELINE = "release-manifest.json"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _safe_relative(value: object, package: Path) -> PurePosixPath:
    """Accepts only an install-relative POSIX path that resolves inside the package."""
    if not isinstance(value, str) or not value or "\\" in value or ":" in value:
        raise SystemExit(f"安装清单路径无效：{value!r}")
    relative = PurePosixPath(value)
    if relative.is_absolute() or str(relative) != value or any(part in (".", "..") for part in relative.parts):
        raise SystemExit(f"安装清单路径越界：{value}")
    protected = {"data", "用户数据", "清理待删", "待清理"}
    forbidden = '<>"|?*'
    reserved = {"con", "prn", "aux", "nul", *(f"com{i}" for i in range(1, 10)), *(f"lpt{i}" for i in range(1, 10))}
    for part in relative.parts:
        if (not part or part.endswith(('.', ' ')) or any(ord(char) < 32 for char in part)
                or any(char in forbidden for char in part) or part.split('.')[0].casefold() in reserved):
            raise SystemExit(f"安装清单含 Windows 路径别名：{value}")
        if part.casefold() in protected:
            raise SystemExit(f"安装清单不得包含用户数据：{value}")
    return relative


def load_baseline(package: Path) -> list[dict]:
    """Reads the packager's manifest and verifies it against the files on disk."""
    package = package.resolve()
    manifest = package / BASELINE
    if not manifest.is_file() or not manifest.resolve().is_relative_to(package):
        raise SystemExit(f"缺少包内正式安装清单：{manifest}（请先用 tools/native_portable.py 打包）")
    document = json.loads(manifest.read_text(encoding="utf-8-sig"))
    rows = document.get("files")
    if not isinstance(rows, list) or not rows:
        raise SystemExit(f"安装清单没有 files 列表：{manifest}")
    entries = []
    seen: set[str] = set()
    for row in rows:
        if not isinstance(row, dict) or "path" not in row or "sha256" not in row:
            raise SystemExit(f"安装清单条目格式不正确：{row!r}")
        relative = _safe_relative(row["path"], package)
        key = relative.as_posix().casefold()
        if key in seen:
            raise SystemExit(f"安装清单路径重复：{relative}")
        seen.add(key)
        target = package.joinpath(*relative.parts)
        if not target.is_file():
            raise SystemExit(f"安装清单列出的文件不存在：{relative}")
        resolved = target.resolve()
        if not resolved.is_relative_to(package):
            raise SystemExit(f"安装清单文件落在安装目录外：{relative}")
        actual_relative = PurePosixPath(resolved.relative_to(package).as_posix())
        if any(part.casefold() in {"data", "用户数据", "清理待删", "待清理"} for part in actual_relative.parts):
            raise SystemExit(f"安装清单不得包含用户数据：{relative}")
        size = target.stat().st_size
        if "bytes" in row and row["bytes"] != size:
            raise SystemExit(f"安装清单大小与文件不符：{relative}")
        if not isinstance(row["sha256"], str) or len(row["sha256"]) != 64 or any(c not in "0123456789abcdef" for c in row["sha256"]):
            raise SystemExit(f"安装清单哈希格式无效：{relative}")
        if sha256(target) != row["sha256"]:
            raise SystemExit(f"安装清单哈希与文件不符：{relative}")
        entries.append({"path": relative.as_posix(), "size": size, "sha256": row["sha256"]})
    return entries


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--previous", type=Path)
    parser.add_argument("--channel", default="stable")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    package = args.package.resolve()
    if not package.is_dir():
        raise SystemExit(f"package directory not found: {package}")
    output = args.output.resolve()
    if not args.dry_run:
        output.mkdir(parents=True, exist_ok=True)

    entries = load_baseline(package)
    baseline = package / BASELINE
    entries.append({"path": BASELINE, "size": baseline.stat().st_size, "sha256": sha256(baseline)})
    for entry in entries:
        entry["blob"] = f"blob-{entry['sha256']}.bin"

    previous: dict[str, str] = {}
    if args.previous and args.previous.exists():
        raw = json.loads(args.previous.read_text(encoding="utf-8"))
        previous = {row["path"]: row["sha256"] for row in raw.get("files", [])}

    written = reused = unchanged = 0
    for entry in entries:
        if previous.get(entry["path"]) == entry["sha256"]:
            unchanged += 1
        target = output / entry["blob"]
        if target.exists():
            if sha256(target) != entry["sha256"]:
                raise SystemExit(f"输出目录已有同名但内容不同的文件：{target.name}")
            reused += 1
            continue
        if not args.dry_run:
            shutil.copy2(package / entry["path"], target)
        written += 1

    manifest = {
        "app_id": APP_ID,
        "schema": 1,
        "version": args.version,
        "channel": args.channel,
        "files": entries,
    }
    if not args.dry_run:
        (output / "update-manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8"
        )
        missing = [e["blob"] for e in entries if not (output / e["blob"]).is_file()]
        if missing:
            raise SystemExit(f"发布资产不自包含，缺少 {len(missing)} 个 blob：{missing[:3]}")
    summary = {
        "package": str(package),
        "version": args.version,
        "files": len(entries),
        "bytes": sum(e["size"] for e in entries),
        "blobs_written": written,
        "blobs_reused": reused,
        "unchanged_since_previous": unchanged,
        "self_contained": not args.dry_run,
        "previous_manifest": str(args.previous) if args.previous else None,
        "dry_run": args.dry_run,
        "output": str(output),
    }
    print(json.dumps(summary, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
