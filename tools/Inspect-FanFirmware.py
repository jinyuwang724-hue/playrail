"""Inspect MSI packages offline; never execute an updater or access hardware.

This reports candidate fan data, not a flashable image or a persistence test.
Only the JSON report is written. Input packages and firmware remain unchanged.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import zipfile


MAX_PACKAGE_BYTES = 128 * 1024 * 1024
MAX_MEMBER_BYTES = 64 * 1024 * 1024
KNOWN_THRESHOLDS = bytes([47, 50, 57, 64, 71, 78, 85])


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def inspect_binary(name, data):
    result = {"name": name, "size": len(data), "sha256": sha256(data)}
    gamepad = b"MSI_GamePad_FW_Updater" in data or b"HJC.MSIGamePad.Core" in data
    versions = list(re.finditer(rb"1T91EMS1\.[0-9A-Z]{3}", data))
    if gamepad:
        result.update(kind="gamepad_controller_updater", fanPersistenceInterface=False,
                      reason="USB HID gamepad updater, not a thermal EC firmware writer")
        return result
    # Only recognize the observed MS-1T91 ROM layout. Never guess from a .cap suffix.
    if len(data) == 0x1800000 and data[:3] == b"\x02\x0f\x27" and versions:
        matches = [v for v in versions if v.start() == 0x9010]
        if len(matches) != 1:
            result.update(kind="unknown", reason="Ambiguous EC version/header layout")
            return result
        ec = data[:0x80000]
        candidates = []
        for match in re.finditer(re.escape(KNOWN_THRESHOLDS), ec):
            offset = match.end()
            duties = list(ec[offset:offset + 7])
            if len(duties) == 7 and all(0 < n <= 100 for n in duties):
                candidates.append({"thresholdOffset": hex(match.start()),
                                   "thresholdsC": list(KNOWN_THRESHOLDS),
                                   "dutyOffset": hex(offset), "duties": duties,
                                   "role": "candidate_only_not_proven_boot_initializer"})
        result.update(
            kind="ms_1t91_bios_with_ec_region",
            ecVersion=matches[0].group().decode("ascii"),
            ecRegion={"offset": 0, "length": len(ec), "sha256": sha256(ec)},
            fanDataCandidates=candidates,
            firmwareResourceId="7448b592-edee-4f18-8c38-27f34462f2af",
            persistenceVerified=False,
            flashReady=False,
            missingValidation=["boot initializer and active table selection",
                               "modified firmware integrity/authentication acceptance",
                               "device-specific complete backup and recovery path",
                               "cold boot and temperature response without host fan writes"],
        )
    else:
        result.update(kind="unknown", reason="No supported firmware identity and layout")
    return result


def inspect_package(path):
    path = Path(path)
    if path.stat().st_size > MAX_PACKAGE_BYTES:
        raise ValueError("Package exceeds inspection size limit")
    data = path.read_bytes()
    report = {"input": str(path.resolve()), "sha256": sha256(data),
              "size": len(data), "offlineOnly": True, "hardwareWrites": 0}
    if zipfile.is_zipfile(io.BytesIO(data)):
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = archive.infolist()
            # Bound total inflation too, including packages with repeated entries.
            if sum(e.file_size for e in entries) > MAX_PACKAGE_BYTES:
                raise ValueError("Uncompressed package exceeds inspection size limit")
            report["members"] = []
            for entry in entries:
                if entry.is_dir():
                    continue
                if entry.file_size > MAX_MEMBER_BYTES:
                    raise ValueError("Archive member exceeds inspection size limit")
                # Inspect in memory; archive paths can never write to the filesystem.
                if Path(entry.filename).suffix.lower() in (".exe", ".cap", ".bin", ".ufw"):
                    report["members"].append(inspect_binary(entry.filename, archive.read(entry)))
    else:
        report["members"] = [inspect_binary(path.name, data)]
    report["flashReady"] = False
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--compare", type=Path, help="Also inspect an older official package")
    parser.add_argument("--output", type=Path, help="Write JSON only; never firmware")
    args = parser.parse_args()
    try:
        if args.output:
            inputs = [args.package] + ([args.compare] if args.compare else [])
            same_path = args.output.resolve() in [p.resolve() for p in inputs]
            same_file = args.output.exists() and any(args.output.samefile(p) for p in inputs)
            if same_path or same_file:
                raise ValueError("Output must not overwrite an input package")
        report = inspect_package(args.package)
        if args.compare:
            report["comparison"] = inspect_package(args.compare)
        serialized = json.dumps(report, ensure_ascii=True, indent=2)
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(serialized + "\n", encoding="utf-8")
        else:
            print(serialized)
    except (OSError, ValueError, zipfile.BadZipFile, RuntimeError) as error:
        parser.exit(2, "Inspection failed: " + str(error) + "\n")


if __name__ == "__main__":
    main()
