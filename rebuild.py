"""Build the reviewed data-only Zune HD mod from the owner's stock GEM files.

Python 3.10+, standard library only. This program has no device or network I/O.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import struct
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED


def sha(data):
    return hashlib.sha256(data).hexdigest()


def members(data):
    if len(data) < 22 or data[:4] != b"XUIZ":
        raise ValueError("Not a GEM/XUIZ archive")
    version, size = struct.unpack_from(">II", data, 4)
    directory_size, count = struct.unpack_from(">IH", data, 16)
    if version != 1 or size != len(data):
        raise ValueError("Unsupported GEM header")
    cursor, next_offset = 22, 0
    start = cursor + directory_size
    result = {}
    for _ in range(count):
        if cursor + 9 > start:
            raise ValueError("Truncated GEM directory")
        length, offset, units = struct.unpack_from(">IIB", data, cursor)
        cursor += 9
        end = cursor + units * 2
        if end > start:
            raise ValueError("Truncated GEM entry name")
        name = data[cursor:end].decode("utf-16-be")
        cursor = end
        if name in result or offset != next_offset or start + offset + length > len(data):
            raise ValueError("Invalid GEM entry bounds")
        result[name] = data[start + offset:start + offset + length]
        next_offset += length
    if cursor != start or start + next_offset != len(data):
        raise ValueError("GEM directory/payload size mismatch")
    return result


def read_xus(data):
    if len(data) < 12:
        raise ValueError("Truncated XUS header")
    magic, version, size, count = struct.unpack_from(">4sHIH", data)
    if magic != b"XUIS" or version not in (0x101, 0x102) or size != len(data):
        raise ValueError("Unsupported XUS header")
    fields, cursor = [], 12
    for _ in range(count):
        if cursor + 2 > len(data):
            raise ValueError("Truncated XUS text length")
        units = struct.unpack_from(">H", data, cursor)[0]
        cursor += 2
        end = cursor + units * 2
        if end > len(data):
            raise ValueError("Truncated XUS text")
        value = data[cursor:end].decode("utf-16-be")
        cursor = end
        refs = b""
        if version == 0x101:
            if cursor >= len(data):
                raise ValueError("Truncated XUS binding count")
            end = cursor + 1 + 3 * data[cursor]
            if end > len(data):
                raise ValueError("Truncated XUS bindings")
            refs, cursor = data[cursor:end], end
        fields.append((value, refs))
    if cursor != len(data):
        raise ValueError("Unexpected trailing XUS bytes")
    return version, fields


def pack_xus(version, fields):
    body = bytearray()
    for value, refs in fields:
        encoded = value.encode("utf-16-be")
        if version == 0x102 and refs:
            raise ValueError("Dense table must not contain bindings")
        if version == 0x101 and (not refs or len(refs) != 1 + 3 * refs[0]):
            raise ValueError("Malformed sparse bindings")
        body.extend(struct.pack(">H", len(encoded) // 2))
        body.extend(encoded)
        body.extend(refs)
    return struct.pack(">4sHIH", b"XUIS", version, 12 + len(body), len(fields)) + body


def safe_relative(name):
    path = PurePosixPath(name)
    if not name or "\\" in name or ":" in name or path.is_absolute() or ".." in path.parts:
        raise ValueError("Unsafe output path")
    if path.as_posix() != name:
        raise ValueError("Noncanonical output path")
    return path


def build(stock, source_dir):
    recipe = json.loads((source_dir / "translations.json").read_text("utf-8"))
    expected = json.loads((source_dir / "expected-files.json").read_text("utf-8"))
    archives = {}
    for name, digest in recipe["stock_gem_sha256"].items():
        safe_relative(name)
        if sha(stock[name]) != digest:
            raise ValueError("Stock GEM mismatch: " + name + "; requires retail 4.5 (114)")
        archives[name] = members(stock[name])
    payload = {"manifest.json": (source_dir / "manifest.json").read_bytes()}
    for row in recipe["tables"]:
        name = row["output"]
        safe_relative(name)
        raw = archives[row["gem"]][row["entry"]]
        version, original = read_xus(raw)
        if version != row["version"] or sha(raw) != row["stock_table_sha256"]:
            raise ValueError("Stock table mismatch")
        decoded, fields = {row["entry"]: original}, []
        for edit in row["fields"]:
            if "copy_field" in edit:
                field = original[edit["copy_field"]]
            else:
                if "text_from" in edit:
                    table, index = edit["text_from"]
                    if table not in decoded:
                        decoded[table] = read_xus(archives[row["gem"]][table])[1]
                    value = decoded[table][index][0]
                else:
                    value = edit["text"]
                field = (value, bytes.fromhex(edit["bindings_hex"]))
            fields.append(field)
        blob = pack_xus(version, fields)
        if name in payload or sha(blob) != expected[name]:
            raise ValueError("Output table failed pinned validation: " + name)
        payload[name] = blob
    if set(payload) != set(expected) or any(sha(blob) != expected[name] for name, blob in payload.items()):
        raise ValueError("Generated file set differs from tested 0.2.3")
    return payload


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--stock-dir", type=Path, help="Directory of unmodified retail GEM exports")
    source.add_argument("--stock-zip", type=Path, help="Owner's preserved stock archive")
    parser.add_argument("--zip-prefix", default="原版菜单资源/", help="GEM folder inside --stock-zip")
    parser.add_argument("--out", type=Path, required=True, help="New output directory; must not exist")
    args = parser.parse_args()
    source_dir = Path(__file__).resolve().parent
    recipe = json.loads((source_dir / "translations.json").read_text("utf-8"))
    if args.out.exists():
        raise ValueError("Output directory already exists; choose a new directory")
    names = recipe["stock_gem_sha256"]
    if args.stock_dir:
        stock = {name: (args.stock_dir / name).read_bytes() for name in names}
    else:
        with ZipFile(args.stock_zip) as archive:
            stock = {name: archive.read(args.zip_prefix + name) for name in names}
    payload = build(stock, source_dir)  # Validate everything before any output write.
    args.out.mkdir(parents=True, exist_ok=False)
    mod_dir = args.out / "mod"
    for name, blob in payload.items():
        target = mod_dir.joinpath(*safe_relative(name).parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(blob)
    archive_path = args.out / "zune-hd-zh-hans-0.2.3.zmod"
    with ZipFile(archive_path, "x") as archive:
        for name, blob in sorted(payload.items()):
            info = ZipInfo(name, date_time=(2026, 11, 22, 0, 0, 0))
            info.compress_type = ZIP_DEFLATED
            archive.writestr(info, blob)
    with ZipFile(archive_path) as archive:
        if archive.testzip() is not None or set(archive.namelist()) != set(payload):
            raise ValueError("Archive integrity failure")
        if any(archive.read(name) != blob for name, blob in payload.items()):
            raise ValueError("Archive readback mismatch")
    result = {"status": "ALL_104_FILES_MATCH_TESTED_0_2_3", "files": len(payload),
              "archive_sha256": sha(archive_path.read_bytes()),
              "note": "ZIP compression may vary across Python/zlib versions; member hashes are pinned."}
    (args.out / "build-result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
