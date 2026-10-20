"""Run the known C8EX ROM fan-table loader entirely in memory.

This is a bounded instruction-level check, not a full EC emulator or a hardware
test. No driver, I/O port, WMI, firmware installer or flash API is used. Optional
curve changes exist only in a Python bytearray; only JSON can be written.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import zipfile


EC_HASH = "c03b80ac1cb8d1c7053d4e2a16ee86c715ecc4d0f4d5b4c02dccbd89521f27db"
WINDOW_BASE = 0x18000
LOADER = 0x5303


class LoaderMachine:
    """Only implements the instruction subset used by this exact loader.

    Unsupported opcodes and calls outside the loader/helpers fail closed.
    The hardware register bank and peripherals are deliberately not emulated.
    """
    def __init__(self, image, mode, argument=0):
        self.rom = image[:0x4000] + image[WINDOW_BASE:WINDOW_BASE + 0x4000] + image[0x8000:0x10000]
        self.direct = bytearray(256)
        self.xram = bytearray(65536)
        self.xram[0xF4D4] = mode
        self.xram[0xF472:0xF479] = bytes([0x77] * 7)
        self.xram[0xF48A:0xF491] = bytes([0x77] * 7)
        self.direct[7] = argument
        self.pc = LOADER
        self.calls = []
        self.writes = []
        self.code_reads = []
        self.last_read = None
        self.steps = 0
        self.stopped_before_peripheral = False

    @property
    def a(self):
        return self.direct[0xE0]

    @a.setter
    def a(self, value):
        self.direct[0xE0] = value & 255

    @property
    def carry(self):
        return self.direct[0xD0] >> 7

    @carry.setter
    def carry(self, value):
        self.direct[0xD0] = (self.direct[0xD0] & 127) | (int(bool(value)) << 7)

    @property
    def dptr(self):
        return (self.direct[0x83] << 8) | self.direct[0x82]

    @dptr.setter
    def dptr(self, value):
        self.direct[0x83] = (value >> 8) & 255
        self.direct[0x82] = value & 255

    def immediate(self):
        value = self.rom[self.pc]
        self.pc += 1
        return value

    def relative(self, take):
        distance = self.immediate()
        if take:
            self.pc = (self.pc + (distance if distance < 128 else distance - 256)) & 65535

    def add(self, value, carry=0):
        result = self.a + value + carry
        self.a = result
        self.carry = result > 255

    def run(self, stop_before_peripheral=False):
        while self.steps < 20000:
            pc = self.pc
            if stop_before_peripheral and pc == 0x55F3:
                # Startup uses R7=1. The table-loading prefix is complete here;
                # 55F3 calls 77C9, which eventually touches PWM/peripheral state.
                self.stopped_before_peripheral = True
                return self
            if not (0x5303 <= pc <= 0x5605 or 0x58EF <= pc <= 0x5B42):
                raise ValueError("Execution left the audited loader/helpers at " + hex(pc))
            op = self.immediate()
            self.steps += 1
            if op == 0x90:
                self.dptr = (self.immediate() << 8) | self.immediate()
            elif op == 0x74:
                self.a = self.immediate()
            elif 0x78 <= op <= 0x7F:
                self.direct[op - 0x78] = self.immediate()
            elif 0xE8 <= op <= 0xEF:
                self.a = self.direct[op - 0xE8]
            elif 0xF8 <= op <= 0xFF:
                self.direct[op - 0xF8] = self.a
            elif 0xA8 <= op <= 0xAF:
                self.direct[op - 0xA8] = self.direct[self.immediate()]
            elif 0x88 <= op <= 0x8F:
                self.direct[self.immediate()] = self.direct[op - 0x88]
            elif op == 0xF5:
                self.direct[self.immediate()] = self.a
            elif op == 0xE0:
                self.a = self.xram[self.dptr]
            elif op == 0xF0:
                self.xram[self.dptr] = self.a
                self.writes.append({"pc": hex(pc), "address": hex(self.dptr),
                                    "value": self.a, "lastRomRead": self.last_read})
            elif op == 0x93:
                address = (self.dptr + self.a) & 65535
                self.a = self.rom[address]
                self.last_read = hex(address)
                self.code_reads.append({"pc": hex(pc), "address": hex(address), "value": self.a})
            elif op == 0xA3:
                self.dptr += 1
            elif op == 0xE4:
                self.a = 0
            elif op == 0x04:
                self.a += 1
            elif 0x08 <= op <= 0x0F:
                index = op - 8
                self.direct[index] = (self.direct[index] + 1) & 255
            elif op == 0x24:
                self.add(self.immediate())
            elif op == 0x34:
                self.add(self.immediate(), self.carry)
            elif 0x28 <= op <= 0x2F:
                self.add(self.direct[op - 0x28])
            elif 0x38 <= op <= 0x3F:
                self.add(self.direct[op - 0x38], self.carry)
            elif op == 0x25:
                self.add(self.direct[self.immediate()])
            elif 0xC8 <= op <= 0xCF:
                index = op - 0xC8
                self.a, self.direct[index] = self.direct[index], self.a
            elif op == 0x44:
                self.a |= self.immediate()
            elif op == 0x54:
                self.a &= self.immediate()
            elif op == 0x60:
                self.relative(self.a == 0)
            elif op == 0x70:
                self.relative(self.a != 0)
            elif op == 0x80:
                self.relative(True)
            elif op in (0x20, 0x30):
                bit = self.immediate()
                address = (bit & 0xF8) if bit >= 128 else (0x20 + (bit >> 3))
                is_set = bool(self.direct[address] & (1 << (bit & 7)))
                self.relative(is_set if op == 0x20 else not is_set)
            elif op == 0xB4:
                value = self.immediate()
                self.carry = self.a < value
                self.relative(self.a != value)
            elif op in (0x02, 0x12):
                target = (self.immediate() << 8) | self.immediate()
                if op == 0x12:
                    self.calls.append(self.pc)
                self.pc = target
            elif op == 0x22:
                if not self.calls:
                    return self
                self.pc = self.calls.pop()
            else:
                raise ValueError("Unaudited opcode %02x at %04x" % (op, pc))
        raise ValueError("Instruction budget exceeded")


def load_image(path):
    if path.stat().st_size > 128 * 1024 * 1024:
        raise ValueError("Input exceeds size limit")
    data = path.read_bytes()
    if zipfile.is_zipfile(io.BytesIO(data)):
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            members = [e for e in archive.infolist() if e.filename.lower().endswith(".cap")]
            if len(members) != 1 or members[0].file_size != 0x1800000:
                raise ValueError("Expected one original MS-1T91 CAP image")
            data = archive.read(members[0])
    if len(data) not in (0x80000, 0x1800000):
        raise ValueError("Unsupported image size")
    image = data[:0x80000]
    if hashlib.sha256(image).hexdigest() != EC_HASH:
        raise ValueError("Only the exact original 1T91EMS1.103 EC region is supported")
    return image


def curves(value):
    result = [int(v) for v in value.split(",")]
    if len(result) != 6 or not all(0 < v <= 94 for v in result) or result != sorted(result):
        raise argparse.ArgumentTypeError("Use six nonzero, nondecreasing values up to 94")
    return result


def simulate(image, mode, argument=0):
    machine = LoaderMachine(image, mode, argument).run(stop_before_peripheral=argument == 1)
    return {"initialMode": hex(mode), "finalMode": hex(machine.xram[0xF4D4]),
            "loaderArgumentR7": argument, "stoppedBeforePeripheral": machine.stopped_before_peripheral,
            "steps": machine.steps, "cpu": list(machine.xram[0xF472:0xF479]),
            "gpu": list(machine.xram[0xF48A:0xF491]),
            "dutyWrites": [w for w in machine.writes if
                           0xF472 <= int(w["address"], 16) <= 0xF478 or
                           0xF48A <= int(w["address"], 16) <= 0xF490],
            "temperatureCpu": list(machine.xram[0xF469:0xF470]),
            "temperatureGpu": list(machine.xram[0xF481:0xF488]),
            "romReads": machine.code_reads}


def verify(image, cpu=None, gpu=None):
    original = simulate(image, 0x0D)
    for start, offset in ((0xF472, 0xFCFD), (0xF48A, 0xFD2D)):
        writes = [w for w in original["dutyWrites"] if start <= int(w["address"], 16) < start + 7]
        if [(w["lastRomRead"], w["value"]) for w in writes] != [
                (hex(offset + i), image[offset + i]) for i in range(7)]:
            raise ValueError("ROM-to-XRAM source correspondence failed")
    custom = simulate(image, 0x8D)
    if custom["dutyWrites"] or custom["steps"] > 10:
        raise ValueError("Custom-mode preservation guard failed")
    report = {"ecSha256": EC_HASH, "verification": "offline_loader_only",
              "hardwareWrites": 0, "flashReady": False, "originalAuto": original,
              "customModePreserved": True, "customModeSteps": custom["steps"]}
    startup = simulate(image, 0x0D, argument=1)
    if not startup["stoppedBeforePeripheral"] or startup["cpu"] != original["cpu"] or startup["gpu"] != original["gpu"]:
        raise ValueError("Startup argument did not load the same ROM tables")
    report["startupLoadingPrefix"] = startup
    report["notEmulated"] = "Full reset sequence, peripheral/PWM calls, temperature response, actual flash and power-cycle persistence"
    if cpu is not None and gpu is not None:
        modified = bytearray(image)
        modified[0xFCFD:0xFD03] = bytes(cpu)
        modified[0xFD2D:0xFD33] = bytes(gpu)
        modified = bytes(modified)
        demonstration = simulate(modified, 0x0D)
        startup_modified = simulate(modified, 0x0D, argument=1)
        if demonstration["cpu"] != cpu + [94] or demonstration["gpu"] != gpu + [94]:
            raise ValueError("In-memory curve substitution failed")
        if (demonstration["temperatureCpu"] != original["temperatureCpu"] or
                demonstration["temperatureGpu"] != original["temperatureGpu"] or
                demonstration["finalMode"] != "0xd"):
            raise ValueError("Substitution changed temperature nodes or mode")
        if startup_modified["cpu"] != demonstration["cpu"] or startup_modified["gpu"] != demonstration["gpu"]:
            raise ValueError("Startup prefix did not load the substituted tables")
        report.update(inMemorySubstitution=demonstration,
                      inMemoryStartupPrefix=startup_modified,
                      changedOffsets=[hex(i) for i, (a, b) in enumerate(zip(image, modified)) if a != b],
                      modifiedFirmwareWrittenToDisk=False)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("--cpu", type=curves)
    parser.add_argument("--gpu", type=curves)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        if (args.cpu is None) != (args.gpu is None):
            raise ValueError("Supply both --cpu and --gpu for an in-memory demonstration")
        if args.output and (args.output.resolve() == args.image.resolve() or
                            (args.output.exists() and args.output.samefile(args.image))):
            raise ValueError("Output must not overwrite the firmware input")
        report = verify(load_image(args.image), args.cpu, args.gpu)
        serialized = json.dumps(report, indent=2) + "\n"
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(serialized, encoding="utf-8")
        else:
            print(serialized)
    except (ValueError, OSError, RuntimeError, zipfile.BadZipFile) as error:
        parser.exit(2, "Offline verification failed: " + str(error) + "\n")


if __name__ == "__main__":
    main()
