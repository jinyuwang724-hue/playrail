"""Read-only Intel Endurance Gaming capability/configuration probe (Windows x64).

Uses the installed ControlLib.dll and Intel's public IGCL ABI. No settings are
written, no services are started, and no third-party DLL is downloaded.
Reference: https://github.com/intel/drivers.gpu.control-library/blob/master/include/igcl_api.h
"""
import argparse
import ctypes as C
import json
import os
from pathlib import Path
import sys
from datetime import datetime, timezone


class InitArgs(C.Structure):
    _fields_ = [("size", C.c_uint32), ("version", C.c_uint8),
                ("appVersion", C.c_uint32), ("flags", C.c_uint32),
                ("supportedVersion", C.c_uint32), ("uid", C.c_uint8 * 16)]


class FeatureValue(C.Union):
    _fields_ = [("enum", C.c_uint32), ("raw", C.c_uint64)]


class Feature(C.Structure):
    _fields_ = [("size", C.c_uint32), ("version", C.c_uint8),
                ("feature", C.c_int32), ("app", C.c_void_p),
                ("appLength", C.c_int8), ("set", C.c_bool),
                ("valueType", C.c_int32), ("value", FeatureValue),
                ("customSize", C.c_int32), ("custom", C.c_void_p)]


class Caps(C.Structure):
    _fields_ = [("size", C.c_uint32), ("version", C.c_uint8),
                ("count", C.c_uint32), ("details", C.c_void_p)]


class InfoValue(C.Union):
    _fields_ = [("raw", C.c_uint8 * 24), ("align", C.c_uint64)]


class Detail(C.Structure):
    _fields_ = [("feature", C.c_int32), ("valueType", C.c_int32),
                ("value", InfoValue), ("customSize", C.c_int32),
                ("custom", C.c_void_p), ("perApp", C.c_bool),
                ("conflicts", C.c_int64), ("misc", C.c_int16),
                ("reserved", C.c_int16 * 3)]


class EnumInfo(C.Structure):
    _fields_ = [("mask", C.c_uint64), ("default", C.c_uint32)]


class EnduranceCaps(C.Structure):
    _fields_ = [("control", EnumInfo), ("mode", EnumInfo)]


class Endurance(C.Structure):
    _fields_ = [("control", C.c_int32), ("mode", C.c_int32)]


class Endurance2(C.Structure):
    _fields_ = [("control", C.c_int32), ("mode", C.c_int32),
                ("framePacingRequired", C.c_bool), ("targetFps", C.c_double),
                ("refreshRate", C.c_double), ("reserved", C.c_uint32 * 4)]


CONTROL = {0: "off", 1: "on", 2: "auto"}
MODE = {0: "performance", 1: "balanced", 2: "battery"}
ERRORS = {0: "success", 0x40000006: "insufficient_permissions",
          0x40000009: "unsupported_version", 0x4000000A: "unsupported_feature",
          0x4000000B: "invalid_argument", 0x4000000F: "invalid_size",
          0x40000010: "unsupported_size", 0x40000014: "data_not_found",
          0x40000015: "not_implemented", 0x40000020: "platform_not_supported",
          0x40010002: "restricted_application"}


def result(code):
    return {"code": f"0x{code:08X}", "status": ERRORS.get(code, "driver_error")}


def check(code, operation):
    if code:
        raise RuntimeError(f"{operation}: {result(code)}")


def layout_checks():
    assert C.sizeof(C.c_void_p) == 8, "Run with 64-bit Python"
    for struct, size in ((InitArgs, 36), (Feature, 56), (Caps, 24),
                         (Detail, 72), (EnduranceCaps, 32), (Endurance, 8), (Endurance2, 48)):
        assert C.sizeof(struct) == size, (struct.__name__, C.sizeof(struct), size)
    assert Feature.set.offset == 25 and Feature.custom.offset == 48
    assert Detail.custom.offset == 40 and Detail.conflicts.offset == 56


def read_setting(lib, device, app, extended=False):
    encoded = app.encode("utf-8")
    if len(encoded) > 127:
        raise ValueError("Application basename must fit IGCL's signed length field")
    name = C.create_string_buffer(encoded)
    value = Endurance2() if extended else Endurance()
    # The outer ctl_3d_feature_getset_t remains version 0, as in Intel's sample;
    # ctl_endurance_gaming2_t is selected by the custom payload size.
    feature = Feature(size=C.sizeof(Feature), version=0,
                      feature=1, app=C.cast(name, C.c_void_p), appLength=len(encoded),
                      set=False, valueType=5, customSize=C.sizeof(value),
                      custom=C.cast(C.pointer(value), C.c_void_p))
    assert not feature.set
    code = lib.ctlGetSet3DFeature(device, C.byref(feature))
    output = {"application": app or "<global>", "extended": extended,
              "returnedCustomSize": feature.customSize, **result(code)}
    if not code:
        output.update(control=CONTROL.get(value.control, value.control),
                      mode=MODE.get(value.mode, value.mode))
        if extended:
            output.update(framePacingRequired=value.framePacingRequired,
                          targetFps=value.targetFps, refreshRate=value.refreshRate)
    return output


def probe(applications):
    layout_checks()
    if sys.platform != "win32":
        raise RuntimeError("This probe requires Windows")
    dll = Path(os.environ["SystemRoot"]) / "System32" / "ControlLib.dll"
    lib = C.CDLL(str(dll))
    lib.ctlInit.argtypes = [C.POINTER(InitArgs), C.POINTER(C.c_void_p)]
    lib.ctlClose.argtypes = [C.c_void_p]
    lib.ctlEnumerateDevices.argtypes = [C.c_void_p, C.POINTER(C.c_uint32), C.POINTER(C.c_void_p)]
    lib.ctlGetSupported3DCapabilities.argtypes = [C.c_void_p, C.POINTER(Caps)]
    lib.ctlGetSet3DFeature.argtypes = [C.c_void_p, C.POINTER(Feature)]
    for name in ("ctlInit", "ctlClose", "ctlEnumerateDevices",
                 "ctlGetSupported3DCapabilities", "ctlGetSet3DFeature"):
        getattr(lib, name).restype = C.c_uint32
    api = C.c_void_p()
    args = InitArgs(size=C.sizeof(InitArgs), appVersion=0x10001)
    check(lib.ctlInit(C.byref(args), C.byref(api)), "ctlInit")
    try:
        count = C.c_uint32()
        check(lib.ctlEnumerateDevices(api, C.byref(count), None), "enumerate count")
        if not 0 < count.value <= 64:
            raise RuntimeError(f"Unexpected adapter count {count.value}")
        devices = (C.c_void_p * count.value)()
        check(lib.ctlEnumerateDevices(api, C.byref(count), devices), "enumerate devices")
        output = {"readOnly": True, "timestamp": datetime.now(timezone.utc).isoformat(),
                  "library": str(dll), "supportedApiVersion": f"0x{args.supportedVersion:08X}",
                  "adapters": []}
        for index, device in enumerate(devices[:count.value]):
            caps = Caps(size=C.sizeof(Caps))
            check(lib.ctlGetSupported3DCapabilities(device, C.byref(caps)), "capability count")
            if not 0 < caps.count <= 256:
                raise RuntimeError(f"Unexpected feature count {caps.count}")
            details = (Detail * caps.count)()
            caps.details = C.cast(details, C.c_void_p)
            check(lib.ctlGetSupported3DCapabilities(device, C.byref(caps)), "capabilities")
            buffers = []
            for detail in details:
                if detail.valueType == 5 and 0 < detail.customSize <= 4096:
                    buf = C.create_string_buffer(detail.customSize)
                    detail.custom = C.cast(buf, C.c_void_p)
                    buffers.append(buf)
            check(lib.ctlGetSupported3DCapabilities(device, C.byref(caps)), "custom capabilities")
            eg = next((d for d in details if d.feature == 1), None)
            adapter = {"index": index, "featureCount": caps.count,
                       "enduranceAdvertised": eg is not None, "capabilities": None,
                       "settings": [read_setting(lib, device, app) for app in ["", *applications]],
                       "extendedGlobal": read_setting(lib, device, "", extended=True)}
            if eg is not None:
                capability = {"valueType": eg.valueType, "customSize": eg.customSize,
                              "perApplication": eg.perApp, "conflictMask": f"0x{eg.conflicts:X}",
                              "graphicsApiMask": eg.misc}
                if eg.valueType == 5 and eg.custom and eg.customSize >= C.sizeof(EnduranceCaps):
                    value = C.cast(eg.custom, C.POINTER(EnduranceCaps)).contents
                    capability.update(controlMask=value.control.mask, modeMask=value.mode.mask,
                                      defaultControl=CONTROL.get(value.control.default, value.control.default),
                                      defaultMode=MODE.get(value.mode.default, value.mode.default))
                adapter["capabilities"] = capability
            output["adapters"].append(adapter)
        return output
    finally:
        lib.ctlClose(api)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", action="append", default=[], help="Optional game exe basename to read")
    parser.add_argument("--output", type=Path, help="Optional JSON report path")
    options = parser.parse_args()
    snapshot = probe(options.app)
    encoded = json.dumps(snapshot, ensure_ascii=False, indent=2, allow_nan=False)
    if options.output:
        options.output.write_text(encoded + "\n", encoding="utf-8")
    print(encoded)
