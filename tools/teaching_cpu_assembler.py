#!/usr/bin/env python3
"""Assembler and small independent ISA reference model for teaching_cpu."""

from __future__ import annotations

import ast
import re
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PROGRAM_DIR = ROOT / "circuits" / "programs"
ROM_WORDS = 512
RAM_BYTES = 256
MASK8 = 0xFF
MASK9 = 0x1FF
MASK12 = 0xFFF


class AssemblyError(ValueError):
    pass


@dataclass
class Program:
    id: str
    name: str
    description: str
    words: list[int]
    initial_ram: list[int]
    expected_text: str = ""
    expected_cycles: int | None = None
    source: str = ""


OPCODES = [
    {"code": 0, "mnemonic": "LDI", "format": "opcode+imm8", "description": "ACC ← imm8；更新 Z，清除 C"},
    {"code": 1, "mnemonic": "ADDI", "format": "opcode+imm8", "description": "ACC ← ACC + imm8；更新 Z/C，C 为进位"},
    {"code": 2, "mnemonic": "ANDI", "format": "opcode+imm8", "description": "ACC ← ACC AND imm8；更新 Z，清除 C"},
    {"code": 3, "mnemonic": "ORI", "format": "opcode+imm8", "description": "ACC ← ACC OR imm8；更新 Z，清除 C"},
    {"code": 4, "mnemonic": "XORI", "format": "opcode+imm8", "description": "ACC ← ACC XOR imm8；更新 Z，清除 C"},
    {"code": 5, "mnemonic": "OUT", "format": "opcode", "description": "输出 ACC"},
    {"code": 6, "mnemonic": "JMP", "format": "opcode+addr8", "description": "跳转到低 8 位绝对地址"},
    {"code": 7, "mnemonic": "JZ", "format": "opcode+addr8", "description": "Z=1 时跳转到低 8 位绝对地址"},
    {"code": 8, "mnemonic": "LD", "format": "opcode+addr8", "description": "ACC ← RAM[addr]；更新 Z，保留 C"},
    {"code": 9, "mnemonic": "ST", "format": "opcode+addr8", "description": "RAM[addr] ← ACC；保留标志"},
    {"code": 10, "mnemonic": "ADD", "format": "opcode+addr8", "description": "ACC ← ACC + RAM[addr]；C 为进位"},
    {"code": 11, "mnemonic": "SUB", "format": "opcode+addr8", "description": "ACC ← ACC - RAM[addr]；C=1 表示无借位"},
    {"code": 12, "mnemonic": "ADC", "format": "opcode+addr8", "description": "ACC ← ACC + RAM[addr] + C"},
    {"code": 13, "mnemonic": "SBC", "format": "opcode+addr8", "description": "ACC ← ACC - RAM[addr] - (1-C)；C=1 表示无借位"},
    {"code": 14, "mnemonic": "EXT", "format": "opcode+subop4", "description": "扩展指令；长跳转和 CALL 后跟一个 12 位地址字"},
    {"code": 15, "mnemonic": "HALT", "format": "opcode", "description": "停机"},
]

EXT_OPS = [
    {"subopcode": 0, "mnemonic": "SHL", "description": "ACC 左移一位，C=移出的 bit7"},
    {"subopcode": 1, "mnemonic": "SHR", "description": "ACC 右移一位，C=移出的 bit0"},
    {"subopcode": 2, "mnemonic": "TAX", "description": "X ← ACC"},
    {"subopcode": 3, "mnemonic": "TXA", "description": "ACC ← X；更新 Z，保留 C"},
    {"subopcode": 4, "mnemonic": "LDAX", "description": "ACC ← RAM[X]；更新 Z，保留 C"},
    {"subopcode": 5, "mnemonic": "STAX", "description": "RAM[X] ← ACC；保留标志"},
    {"subopcode": 6, "mnemonic": "INX", "description": "X ← X+1；更新 Z，保留 C"},
    {"subopcode": 7, "mnemonic": "DEX", "description": "X ← X-1；更新 Z，保留 C"},
    {"subopcode": 8, "mnemonic": "ADDX", "description": "ACC ← ACC+X；更新 Z/C"},
    {"subopcode": 9, "mnemonic": "SUBX", "description": "ACC ← ACC-X；C=1 表示无借位"},
    {"subopcode": 10, "mnemonic": "JC", "description": "C=1 时读取第二字并跳转到 9 位地址"},
    {"subopcode": 11, "mnemonic": "JNC", "description": "C=0 时读取第二字并跳转到 9 位地址"},
    {"subopcode": 12, "mnemonic": "JMP9", "description": "读取第二字并跳转到 9 位地址"},
    {"subopcode": 13, "mnemonic": "JZ9", "description": "Z=1 时读取第二字并跳转到 9 位地址"},
    {"subopcode": 14, "mnemonic": "CALL", "description": "调用第二字的 9 位地址；将返回地址压入 RAM 栈"},
    {"subopcode": 15, "mnemonic": "RET", "description": "从 RAM 栈弹出 9 位返回地址"},
]


ISA = {
    "instructionBits": 12,
    "programAddressBits": 9,
    "programWords": ROM_WORDS,
    "dataAddressBits": 8,
    "ramBytes": RAM_BYTES,
    "registers": {"acc": 8, "x": 8, "sp": 8, "pc": 9, "out": 8},
    "flags": {"zero": "zero result", "carry": "addition carry or subtraction no-borrow"},
    "opcodes": OPCODES,
    "extensions": EXT_OPS,
}


def _strip_comment(line: str) -> str:
    quoted = False
    escaped = False
    for index, char in enumerate(line):
        if escaped:
            escaped = False
        elif char == "\\" and quoted:
            escaped = True
        elif char == "'":
            quoted = not quoted
        elif char == ";" and not quoted:
            return line[:index]
    return line


def _parse_literal(value: str, symbols: dict[str, int]) -> int:
    value = value.strip()
    if value in symbols:
        return symbols[value]
    match = re.fullmatch(r"([A-Za-z_][A-Za-z0-9_]*)(?:\s*([+-])\s*(.+))?", value)
    if match and match.group(1) in symbols:
        result = symbols[match.group(1)]
        if match.group(2):
            delta = _parse_literal(match.group(3), symbols)
            result = result + delta if match.group(2) == "+" else result - delta
        return result
    try:
        if value.startswith("'"):
            parsed = ast.literal_eval(value)
            if len(parsed) != 1:
                raise AssemblyError(f"Expected one character, got {value!r}")
            return ord(parsed)
        return int(value, 0)
    except (ValueError, SyntaxError, TypeError) as exc:
        raise AssemblyError(f"Unknown constant or label {value!r}") from exc


def assemble(source: str, *, program_id: str = "program", name: str | None = None,
             description: str = "", expected_text: str = "", initial_ram: list[int] | None = None) -> Program:
    """Assemble a readable subset of the teaching CPU ISA into all 512 ROM words."""
    symbols: dict[str, int] = {}
    constants: dict[str, int] = {}
    entries: list[tuple[int, str, str, int]] = []
    pc = 0
    cleaned = [_strip_comment(line).strip() for line in source.splitlines()]

    def estimate_words(mnemonic: str) -> int:
        return 2 if mnemonic in {"JC", "JNC", "JMP9", "JZ9", "CALL"} else 0 if mnemonic == ".equ" else 1

    for line_number, raw in enumerate(cleaned, 1):
        line = raw
        if not line:
            continue
        if ":" in line:
            label, line = line.split(":", 1)
            label = label.strip()
            if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", label) or label in symbols or label in constants:
                raise AssemblyError(f"Invalid or duplicate label at line {line_number}: {label!r}")
            symbols[label] = pc
            line = line.strip()
            if not line:
                continue
        if line.lower().startswith(".equ"):
            _, rest = line.split(None, 1)
            key, value = (part.strip() for part in rest.split(",", 1))
            if key in constants or key in symbols:
                raise AssemblyError(f"Duplicate symbol at line {line_number}: {key}")
            constants[key] = _parse_literal(value, constants)
            continue
        if line.lower().startswith(".org"):
            pc = _parse_literal(line.split(None, 1)[1], {**constants, **symbols})
            if not 0 <= pc < ROM_WORDS:
                raise AssemblyError(f".org out of range at line {line_number}")
            continue
        mnemonic, _, operand = line.partition(" ")
        mnemonic = mnemonic.upper()
        size = estimate_words(mnemonic)
        if not size:
            raise AssemblyError(f"Unknown directive at line {line_number}: {line}")
        entries.append((pc, mnemonic, operand.strip(), line_number))
        pc += size
        if pc > ROM_WORDS:
            raise AssemblyError(f"Program exceeds the {ROM_WORDS}-word ROM")

    all_symbols = {**constants, **symbols}
    words = [0] * ROM_WORDS
    for address, mnemonic, operand, line_number in entries:
        value = _parse_literal(operand, all_symbols) if operand else 0
        base_ops = {"LDI": 0, "ADDI": 1, "ANDI": 2, "ORI": 3, "XORI": 4,
                    "OUT": 5, "JMP": 6, "JZ": 7, "LD": 8, "ST": 9,
                    "ADD": 10, "SUB": 11, "ADC": 12, "SBC": 13}
        ext_ops = {"SHL": 0, "SHR": 1, "TAX": 2, "TXA": 3, "LDAX": 4,
                   "STAX": 5, "INX": 6, "DEX": 7, "ADDX": 8, "SUBX": 9,
                   "JC": 10, "JNC": 11, "JMP9": 12, "JZ9": 13,
                   "CALL": 14, "RET": 15}
        if mnemonic in base_ops:
            if mnemonic in {"OUT"}:
                value = 0
            if mnemonic in {"JMP", "JZ"} and not 0 <= value <= 0xFF:
                raise AssemblyError(f"{mnemonic} target must be in 0..255 at line {line_number}")
            if mnemonic in {"LDI", "ADDI", "ANDI", "ORI", "XORI"} and not -128 <= value <= 0xFF:
                raise AssemblyError(f"{mnemonic} immediate must be in -128..255 at line {line_number}")
            if mnemonic not in {"OUT", "LDI", "ADDI", "ANDI", "ORI", "XORI"} and not 0 <= value <= 0xFF:
                raise AssemblyError(f"{mnemonic} operand must be in 0..255 at line {line_number}")
            words[address] = (base_ops[mnemonic] << 8) | (value & 0xFF)
        elif mnemonic in ext_ops:
            subop = ext_ops[mnemonic]
            if mnemonic in {"JC", "JNC", "JMP9", "JZ9", "CALL"}:
                if not 0 <= value <= MASK9:
                    raise AssemblyError(f"{mnemonic} target must be in 0..511 at line {line_number}")
                words[address] = 0xE00 | (subop << 4)
                words[address + 1] = value
            else:
                if operand:
                    raise AssemblyError(f"{mnemonic} takes no operand at line {line_number}")
                words[address] = 0xE00 | (subop << 4)
        elif mnemonic == "HALT":
            words[address] = 0xF00
        else:
            raise AssemblyError(f"Unknown instruction {mnemonic!r} at line {line_number}")

    ram = list(initial_ram or [0] * RAM_BYTES)
    if len(ram) != RAM_BYTES or any(not 0 <= byte <= 0xFF for byte in ram):
        raise AssemblyError("initial_ram must contain 256 bytes")
    return Program(program_id, name or program_id, description, words, ram,
                   expected_text, None, source)


def _normal_byte(value: int) -> int:
    return value & MASK8


class CpuReference:
    """Independent architectural cycle model used to audit RTL and synthesized traces."""

    def __init__(self, program: Program):
        self.program = program
        self.ram = list(program.initial_ram)
        self.reset()

    def reset(self, *, reset_ram: bool = False):
        if reset_ram:
            self.ram = list(self.program.initial_ram)
        self.pc = self.instruction = self.phase = self.acc = self.x = self.sp = 0
        self.out = self.terminal_sequence = 0
        self.zero, self.carry, self.halt = 1, 0, 0
        self.terminal_write = 0
        self.long_target = 0
        self.return_high = 0

    def step(self, reset: int = 0) -> dict[str, int]:
        if reset:
            self.reset()
            return self.state()
        terminal_write = int(self.phase == 1 and not self.halt and self.instruction >> 8 == 5)
        if terminal_write:
            self.out = self.acc
            self.terminal_sequence = (self.terminal_sequence + 1) & MASK8
        self.terminal_write = terminal_write
        if self.halt:
            return self.state()
        phase = self.phase
        opcode, operand = self.instruction >> 8, self.instruction & 0xFF
        subop = operand >> 4
        next_pc = (self.pc + 1) & MASK9

        def set_acc(value: int, *, c: int | None = None):
            self.acc = value & MASK8
            self.zero = int(self.acc == 0)
            if c is not None:
                self.carry = c & 1

        if phase == 0:
            self.instruction = self.program.words[self.pc] & MASK12
            self.phase = 1
        elif phase == 1:
            if opcode == 15:
                self.halt = 1
            elif opcode == 14 and 10 <= subop <= 14:
                self.pc = next_pc
                self.phase = 2
            elif opcode == 6 or opcode == 7 and self.zero:
                self.pc = operand
                self.phase = 0
            elif opcode == 8:
                set_acc(self.ram[operand])
                self.pc, self.phase = next_pc, 0
            elif opcode == 9:
                self.ram[operand] = self.acc
                self.pc, self.phase = next_pc, 0
            elif opcode == 5:
                self.pc, self.phase = next_pc, 0
            elif opcode in (0, 1, 2, 3, 4, 10, 11, 12, 13):
                rhs = operand if opcode <= 4 else self.ram[operand]
                if opcode in (0,):
                    set_acc(rhs, c=0)
                elif opcode in (1, 10, 12):
                    cin = self.carry if opcode in (12,) else 0
                    wide = self.acc + rhs + cin
                    set_acc(wide, c=wide >> 8)
                elif opcode in (2, 3, 4):
                    set_acc((self.acc & rhs) if opcode == 2 else (self.acc | rhs) if opcode == 3 else (self.acc ^ rhs), c=0)
                else:
                    cin = self.carry if opcode == 13 else 1
                    wide = self.acc + ((~rhs) & MASK8) + cin
                    set_acc(wide, c=wide >> 8)
                self.pc, self.phase = next_pc, 0
            elif opcode == 14 and subop == 15:
                self.phase = 5
            elif opcode == 14:
                if subop == 2:
                    self.x = self.acc
                elif subop == 3:
                    set_acc(self.x)
                elif subop == 4:
                    set_acc(self.ram[self.x])
                elif subop == 5:
                    self.ram[self.x] = self.acc
                elif subop == 6:
                    self.x = (self.x + 1) & MASK8
                    self.zero = int(self.x == 0)
                elif subop == 7:
                    self.x = (self.x - 1) & MASK8
                    self.zero = int(self.x == 0)
                elif subop in (0, 1):
                    old = self.acc
                    value = ((old << 1) & MASK8) if subop == 0 else old >> 1
                    c = (old >> 7) & 1 if subop == 0 else old & 1
                    set_acc(value, c=c)
                elif subop == 8:
                    wide = self.acc + self.x
                    set_acc(wide, c=wide >> 8)
                elif subop == 9:
                    wide = self.acc + ((~self.x) & MASK8) + 1
                    set_acc(wide, c=wide >> 8)
                self.pc, self.phase = next_pc, 0
            else:
                self.pc, self.phase = next_pc, 0
        elif phase == 2:
            self.long_target = self.program.words[self.pc] & MASK9
            self.phase = 3
        elif phase == 3:
            if opcode == 14 and subop == 10 and self.carry:
                self.pc, self.phase = self.long_target, 0
            elif opcode == 14 and subop == 11 and not self.carry:
                self.pc, self.phase = self.long_target, 0
            elif opcode == 14 and subop == 12:
                self.pc, self.phase = self.long_target, 0
            elif opcode == 14 and subop == 13 and self.zero:
                self.pc, self.phase = self.long_target, 0
            elif opcode == 14 and subop == 14:
                return_pc = next_pc
                self.ram[self.sp] = return_pc & MASK8
                self.phase = 4
            else:
                self.pc, self.phase = next_pc, 0
        elif phase == 4:
            return_pc = next_pc
            self.ram[(self.sp + 1) & MASK8] = (return_pc >> 8) & 1
            self.sp = (self.sp + 2) & MASK8
            self.pc, self.phase = self.long_target, 0
        elif phase == 5:
            self.return_high = self.ram[(self.sp - 1) & MASK8] & 1
            self.phase = 6
        elif phase == 6:
            low = self.ram[(self.sp - 2) & MASK8]
            self.pc = (self.return_high << 8) | low
            self.sp = (self.sp - 2) & MASK8
            self.phase = 0
        return self.state()

    def state(self) -> dict[str, int]:
        return {"pc": self.pc, "instruction": self.instruction, "phase": self.phase,
                "acc": self.acc, "x": self.x, "sp": self.sp, "out": self.out,
                "zero": self.zero, "carry": self.carry, "halt": self.halt,
                "terminal_data": self.out, "terminal_write": self.terminal_write,
                "terminal_sequence": self.terminal_sequence}

    def buses(self, reset: int = 0) -> dict[str, int]:
        opcode, operand = self.instruction >> 8, self.instruction & 0xFF
        subop = operand >> 4
        read_address = operand
        if opcode == 14 and subop in (4, 5):
            read_address = self.x
        if self.phase == 5:
            read_address = (self.sp - 1) & MASK8
        elif self.phase == 6:
            read_address = (self.sp - 2) & MASK8

        raw_write = False
        write_address, write_data = operand, self.acc
        if self.phase == 1 and not self.halt and opcode == 9:
            raw_write = True
        elif self.phase == 1 and not self.halt and opcode == 14 and subop == 5:
            raw_write = True
            write_address = self.x
        elif self.phase == 3 and opcode == 14 and subop == 14:
            raw_write = True
            write_address = self.sp
            write_data = (self.pc + 1) & MASK8
        elif self.phase == 4:
            raw_write = True
            write_address = (self.sp + 1) & MASK8
            write_data = ((self.pc + 1) >> 8) & 1

        return {"rom_address": self.pc, "rom_data": self.program.words[self.pc] & MASK12,
                "ram_read_address": read_address, "ram_read_data": self.ram[read_address],
                "ram_write_address": write_address, "ram_write_data": write_data,
                "ram_write_enable": int(bool(raw_write) and not reset)}


def load_programs() -> list[Program]:
    manifest = ast.literal_eval((PROGRAM_DIR / "catalog.pydata").read_text())
    programs = []
    for item in manifest:
        source_path = PROGRAM_DIR / item["source"]
        initial_ram = [0] * RAM_BYTES
        for address, value in item.get("initialRam", {}).items():
            address = int(address, 0) if isinstance(address, str) else int(address)
            value = int(value, 0) if isinstance(value, str) else int(value)
            initial_ram[address] = value
        program = assemble(source_path.read_text(), program_id=item["id"], name=item["name"],
                           description=item["description"], expected_text=item.get("expectedText", ""),
                           initial_ram=initial_ram)
        program.expected_cycles = item.get("expectedCycles")
        programs.append(program)
    return programs


def main() -> None:
    import argparse
    import json

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--id", default="program")
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()
    program = assemble(args.source.read_text(), program_id=args.id, name=args.id)
    if args.json:
        args.json.write_text(json.dumps({"words": program.words, "labels": {}}, indent=2) + "\n")
    else:
        print("\n".join(f"{i:03x}: {word:03x}" for i, word in enumerate(program.words) if word))


if __name__ == "__main__":
    main()
