"""Generate self-contained update assets from the package's install allowlist.

Changed files for every --legacy-baseline remain standalone assets for older clients.
Content shared by all those baselines is grouped into verified ZIP packs. The schema-1
extension is optional: older clients skip matching files before resolving their assets.
The z-update- prefix keeps the complete MabiAgent ZIP first in release asset ordering.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import zipfile
from pathlib import Path, PurePosixPath

APP_ID = "MabiAgent"
BASELINE = "release-manifest.json"
ASSET_PREFIX = "z-update-"
MANIFEST = "update-manifest.json"


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


def load_hashes(path: Path) -> dict[str, str]:
    rows = json.loads(path.read_text(encoding="utf-8-sig")).get("files")
    if not isinstance(rows, list) or not rows:
        raise SystemExit(f"基线没有文件列表：{path}")
    result = {}
    for row in rows:
        name, digest = row.get("path"), row.get("sha256")
        if not isinstance(name, str) or not isinstance(digest, str) or len(digest) != 64:
            raise SystemExit(f"基线条目无效：{path}")
        key = name.casefold()
        if key in result:
            raise SystemExit(f"基线路径重复：{name}")
        result[key] = digest
    return result


def write_pack(package: Path, output: Path, group: list[dict], dry_run: bool) -> dict:
    # Name from the ordered content list; fixed ZIP metadata makes retries reproducible.
    identity = hashlib.sha256("\n".join(e["sha256"] for e in group).encode()).hexdigest()
    name = f"{ASSET_PREFIX}pack-{identity}.zip"
    for entry in group:
        entry["pack"] = name
    if dry_run:
        return {"asset": name, "size": 0, "sha256": "0" * 64}
    target = output / name
    temporary = output / (name + ".tmp")
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for entry in group:
                info = zipfile.ZipInfo(entry["blob"], date_time=(2020, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                with archive.open(info, "w") as member, (package / entry["path"]).open("rb") as source:
                    shutil.copyfileobj(source, member)
        temporary.replace(target)
    finally:
        if temporary.exists(): temporary.unlink()
    return {"asset": name, "size": target.stat().st_size, "sha256": sha256(target)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--previous", type=Path)
    parser.add_argument("--legacy-baseline", action="append", type=Path, default=[])
    parser.add_argument("--pack-bytes", type=int, default=16 << 20)
    parser.add_argument("--max-assets", type=int, default=1000)
    parser.add_argument("--channel", default="stable")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    package = args.package.resolve()
    if not package.is_dir():
        raise SystemExit(f"package directory not found: {package}")
    output = args.output.resolve()
    if output.is_relative_to(package):
        raise SystemExit("更新资产目录必须位于安装目录外")
    if args.pack_bytes <= 0 or args.max_assets < 4:
        raise SystemExit("资源包大小或附件上限无效")
    if not args.dry_run:
        output.mkdir(parents=True, exist_ok=True)

    entries = load_baseline(package)
    baseline = package / BASELINE
    entries.append({"path": BASELINE, "size": baseline.stat().st_size, "sha256": sha256(baseline)})
    for entry in entries:
        entry["blob"] = f"{ASSET_PREFIX}blob-{entry['sha256']}.bin"

    previous: dict[str, str] = {}
    if args.previous and args.previous.exists():
        previous = load_hashes(args.previous)

    baselines = [load_hashes(path) for path in args.legacy_baseline]
    direct_hashes = {e["sha256"] for e in entries if not baselines or
                     any(b.get(e["path"].casefold()) != e["sha256"] for b in baselines)}
    unique = {e["sha256"]: e for e in reversed(entries)}
    groups, group, count = [], [], 0
    for digest, entry in sorted(unique.items()):
        if digest in direct_hashes: continue
        if group and count + entry["size"] > args.pack_bytes:
            groups.append(group); group, count = [], 0
        group.append(entry); count += entry["size"]
    if group: groups.append(group)
    # Reserve the complete ZIP, checksum file and manifest before writing any assets.
    asset_count = len(direct_hashes) + len(groups) + 3
    if asset_count > args.max_assets:
        raise SystemExit(f"发布附件{asset_count}个，超过上限{args.max_assets}；补充旧版基线以合并共同资源")

    packs = [write_pack(package, output, group, args.dry_run) for group in groups]
    packed = {e["sha256"]: e["pack"] for group in groups for e in group}
    for entry in entries:
        if entry["sha256"] in packed: entry["pack"] = packed[entry["sha256"]]

    written = reused = unchanged = 0
    for entry in entries:
        if previous.get(entry["path"].casefold()) == entry["sha256"]:
            unchanged += 1
        if "pack" in entry: continue
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
    if packs: manifest["packs"] = packs
    if not args.dry_run:
        (output / MANIFEST).write_text(
            json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8"
        )
        missing = [e.get("pack", e["blob"]) for e in entries if not (output / e.get("pack", e["blob"])).is_file()]
        if missing:
            raise SystemExit(f"发布资产不自包含，缺少 {len(missing)} 个 blob：{missing[:3]}")
        expected = {MANIFEST} | {e.get("pack", e["blob"]) for e in entries}
        stale = {p.name for p in output.iterdir()} - expected
        if stale:
            raise SystemExit(f"资产目录含旧版或无关文件，请使用独立输出目录：{sorted(stale)[:3]}")
    summary = {
        "package": str(package),
        "version": args.version,
        "files": len(entries),
        "bytes": sum(e["size"] for e in entries),
        "blobs_written": written,
        "blobs_reused": reused,
        "unchanged_since_previous": unchanged,
        "packs": len(packs),
        "release_asset_count": asset_count,
        "legacy_baselines": len(baselines),
        "self_contained": not args.dry_run,
        "previous_manifest": str(args.previous) if args.previous else None,
        "dry_run": args.dry_run,
        "output": str(output),
    }
    print(json.dumps(summary, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
