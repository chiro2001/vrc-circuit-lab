#!/usr/bin/env python3
"""Independent mathematical/data-structure oracles for teaching CPU examples."""

from __future__ import annotations

from decimal import Decimal, localcontext
import math


def pi_text(digits: int) -> str:
    """Return 3 plus the requested truncated fractional digits, using Chudnovsky."""
    with localcontext() as context:
        context.prec = digits + 24
        m, l, x, k = 1, 13_591_409, 1, 6
        total = Decimal(l)
        for i in range(1, digits // 14 + 4):
            m = (k**3 - 16 * k) * m // (i**3)
            l += 545_140_134
            x *= -262_537_412_640_768_000
            total += Decimal(m * l) / Decimal(x)
            k += 12
        value = Decimal(426_880) * Decimal(10_005).sqrt() / total
        fixed = format(value, "." + str(digits + 8) + "f")
        return fixed[: digits + 2] + "\n"


def crc8_atm(data: bytes) -> int:
    crc = 0
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = ((crc << 1) ^ (0x07 if crc & 0x80 else 0)) & 0xFF
    return crc


def life_step(board: list[int]) -> list[int]:
    next_board = [0] * 64
    for row in range(8):
        for col in range(8):
            neighbors = 0
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    if dx == 0 and dy == 0:
                        continue
                    y, x = row + dy, col + dx
                    if 0 <= y < 8 and 0 <= x < 8:
                        neighbors += board[y * 8 + x]
            alive = board[row * 8 + col]
            next_board[row * 8 + col] = int(neighbors == 3 or (alive and neighbors == 2))
    return next_board


def verify_program(program, output: list[int], final_ram: list[int]) -> None:
    """Raise when CPU output or RAM disagrees with an algorithmic host oracle."""
    metadata = program.metadata or {}
    oracle = metadata.get("oracle")
    if oracle == "pi-spigot":
        digits = int(metadata["digits"])
        expected = pi_text(digits).encode("ascii")
        if bytes(output) != expected:
            raise AssertionError(f"{program.id}: spigot output differs from {digits}-digit Chudnovsky oracle")
        return
    if oracle == "gray4":
        expected = [value ^ (value >> 1) for value in range(16)]
        if output != expected:
            raise AssertionError(f"{program.id}: Gray-code sequence differs: {output}")
    elif oracle == "lfsr8":
        value, expected = 0xA5, []
        for _ in range(16):
            expected.append(value)
            carry = value & 1
            value >>= 1
            if carry:
                value ^= 0xB8
        if output != expected:
            raise AssertionError(f"{program.id}: LFSR sequence differs")
    elif oracle == "fibonacci16":
        a, b = 0, 1
        expected = []
        for _ in range(16):
            expected.extend((a & 0xFF, (a >> 8) & 0xFF))
            a, b = b, (a + b) & 0xFFFF
        if output != expected:
            raise AssertionError(f"{program.id}: 16-bit Fibonacci bytes differ")
        if final_ram[256 + 0x82] != 0x62 or final_ram[256 + 0x83] != 0x02:
            raise AssertionError(f"{program.id}: final Fibonacci register is not F15")
    elif oracle == "array-sort8":
        expected = sorted(metadata["inputValues"])
        if final_ram[256:264] != expected:
            raise AssertionError(f"{program.id}: sorted RAM does not match {expected}")
    elif oracle == "prime-sieve100":
        expected = [0] * 101
        for value in range(2, 101):
            expected[value] = 1
        for factor in range(2, 11):
            for multiple in range(2 * factor, 101, factor):
                expected[multiple] = 0
        if final_ram[256:357] != expected:
            raise AssertionError(f"{program.id}: sieve flags differ from the prime oracle")
    elif oracle == "gcd":
        expected = math.gcd(252, 105)
        if output != [expected] or final_ram[256 + 0x82] != expected:
            raise AssertionError(f"{program.id}: Euclidean GCD result differs")
    elif oracle == "crc8-atm-a5":
        expected = crc8_atm(b"\xA5")
        if output != [expected] or final_ram[256 + 0x80] != expected:
            raise AssertionError(f"{program.id}: CRC-8/ATM result differs")
    elif oracle == "ram-cross-page":
        if bytes(output) != b"PASS\n":
            raise AssertionError(f"{program.id}: cross-page sentinel checks failed")
        for bank, values in enumerate(((0x10, 0x11), (0x20, 0x21), (0x30, 0x31), (0x40, 0x41))):
            base = bank * 256
            if (final_ram[base + 0xFF], final_ram[base]) != values:
                raise AssertionError(f"{program.id}: page {bank} sentinel values differ")
    elif oracle == "life8x8":
        display = metadata["display"]
        board = [0] * 64
        for address, value in metadata.get("initialRam", {}).items():
            address = int(address, 0) if isinstance(address, str) else int(address)
            if 256 <= address < 320:
                board[address - 256] = int(value)
        for _ in range(int(metadata["frameCount"])):
            board = life_step(board)
        selector = int(metadata["frameCount"]) & 1
        if final_ram[display["selectorAddress"]] != selector:
            raise AssertionError(f"{program.id}: committed front-buffer selector differs")
        if final_ram[display["generationAddress"]] != int(metadata["frameCount"]):
            raise AssertionError(f"{program.id}: frame sequence differs")
        base = display["displayAlternateBase"] if selector else display["displayRamBase"]
        if final_ram[base:base + 64] != board:
            raise AssertionError(f"{program.id}: visible Life frame differs from closed-edge oracle")
        if output:
            raise AssertionError(f"{program.id}: Life program unexpectedly wrote terminal bytes")
    elif oracle is not None:
        raise AssertionError(f"No independent oracle implementation for {oracle!r}")
