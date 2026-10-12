#!/usr/bin/env python3
"""Generate reproducible teaching-CPU examples whose assembly has loops.

The π examples are generated from the Rabinowitz–Wagon integer spigot.  The
generator emits ordinary CPU assembly; it never inserts π digits into ROM or
RAM.  Only the spigot's required initial array (all 2s) is initialized.
"""

from __future__ import annotations

import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROGRAMS = ROOT / "circuits" / "programs"


def pi_spigot_source(digits: int) -> str:
    """Emit the finite Rabinowitz–Wagon spigot for ``digits`` fractional digits."""
    if not 1 <= digits <= 100:
        raise ValueError("the spigot supports 1..100 fractional digits")
    iterations = digits + 1  # the first emitted digit is the integer 3
    length = (10 * iterations) // 3
    start_bank = 1 + ((length - 1) // 128)
    start_x = 2 * ((length - 1) % 128)
    lines: list[str] = [
        f"; CPU-generated π using the Rabinowitz–Wagon integer spigot.",
        f"; Emits 3 plus {digits} fractional digits and a newline.",
        "; For each output pass, i descends: x=10*a[i-1]+q*i; a[i-1]=x%(2*i-1); q=x//(2*i-1).",
        "; The CPU implements the product and quotient with bounded 8/16-bit add/subtract loops.",
        "; a[i] starts at 2; no π digit is stored in ROM or initial RAM.",
        f"; N={length}; two bytes per array element across RAM banks 1..3.",
        ".equ Q, 0x20", ".equ J, 0x21", ".equ I, 0x22", ".equ IH, 0x23",
        ".equ NINES, 0x24", ".equ PRED, 0x25", ".equ AL, 0x26",
        ".equ AH, 0x27", ".equ XL, 0x28", ".equ XH, 0x29",
        ".equ ML, 0x2A", ".equ MH, 0x2B", ".equ DL, 0x2C",
        ".equ DH, 0x2D", ".equ CL, 0x2E", ".equ QT, 0x2F",
        ".equ DIGIT, 0x30", ".equ PTR, 0x31", ".equ ZERO, 0x32",
        "        BANK 0",
        "        LDI 0", "        ST Q", "        ST NINES", "        ST PRED",
        "        ST ZERO", "        ST J",
        "        ADDI 1", "        ST J",
        "outer:",
        "        LDI 0", "        ST Q",
        f"        LDI {length & 0xFF}", "        ST I",
        f"        LDI {length >> 8}", "        ST IH",
        f"        BANK {start_bank}",
        f"        LDI {start_x}", "        TAX",
        f"        JMP inner{start_bank}",
    ]

    def emit(*items: str) -> None:
        lines.extend(items)

    def segment_body(bank: int, next_segment: int | None) -> None:
        emit(f"inner{bank}:", f"        BANK {bank}", "        LDAX", "        BANK 0",
             "        ST AL", f"        BANK {bank}", "        INX", "        LDAX",
             "        BANK 0", "        ST AH", "        TXA", "        ADDI -1",
             "        ST PTR", "        CALL process_cell", "        BANK 0",
             "        LD PTR", "        TAX", "        LD XL", f"        BANK {bank}",
             "        STAX", "        INX", "        BANK 0", "        LD XH",
             f"        BANK {bank}", "        STAX", "        DEX", "        DEX", "        DEX",
             "        BANK 0", "        LD I", "        ADDI -1", "        ST I",
             f"        JC index_no_borrow{bank}", "        LD IH", "        ADDI -1", "        ST IH",
             f"index_no_borrow{bank}:")
        if bank == 3:
            emit("        LD I", "        JZ switch_to_bank2", "        JMP inner3",
                 "switch_to_bank2:", "        BANK 2", "        LDI 254", "        TAX",
                 "        JMP inner2")
        elif bank == 2 and next_segment == 1:
            emit("        LD I", "        ADDI -128", "        JZ switch_to_bank1",
                 "        JMP inner2", "switch_to_bank1:", "        BANK 1",
                 "        LDI 254", "        TAX", "        JMP inner1")
        else:
            emit("        LD I", "        JZ inner_done", "        JMP inner1")

    if start_bank == 3:
        segment_body(3, 2)
        segment_body(2, 1)
        segment_body(1, None)
    else:
        segment_body(2, 1)
        segment_body(1, None)

    emit(
        "inner_done:", "        BANK 0",
        "        LD Q", "        ST QT", "        LDI 0", "        ST DIGIT",
        "divide_q_by_ten:", "        LD QT", "        ADDI -10", "        JNC quotient_ready",
        "        ST QT", "        LD DIGIT", "        ADDI 1", "        ST DIGIT",
        "        JMP9 divide_q_by_ten",
        "quotient_ready:",
        "        BANK 1", "        LDI 0", "        TAX", "        BANK 0", "        LD QT",
        "        BANK 1", "        STAX", "        INX", "        BANK 0", "        LDI 0",
        "        BANK 1", "        STAX", "        BANK 0",
        "        LD DIGIT", "        ST Q",
        "        LD Q", "        ADDI -9", "        JZ quotient_nine",
        "        LD Q", "        ADDI -10", "        JZ quotient_ten",
        "quotient_regular:", "        LD J", "        ADDI -1", "        JZ first_digit",
        "        LD PRED", "        ADDI '0'", "        OUT",
        "        LD J", "        ADDI -2", "        JZ decimal_point",
        "        JMP after_decimal_point", "decimal_point:",
        "        LDI '.'", "        OUT", "after_decimal_point:",
        "first_digit:", "        LD Q", "        ST PRED",
        "flush_nines:", "        LD NINES", "        JZ digit_complete",
        "        LDI '9'", "        OUT", "        LD NINES", "        ADDI -1",
        "        ST NINES", "        JMP9 flush_nines",
        "quotient_nine:", "        LD NINES", "        ADDI 1", "        ST NINES",
        "        JMP digit_complete",
        "quotient_ten:", "        LD PRED", "        ADDI '1'", "        OUT",
        "flush_zeros:", "        LD NINES", "        JZ ten_complete",
        "        LDI '0'", "        OUT", "        LD NINES", "        ADDI -1",
        "        ST NINES", "        JMP9 flush_zeros",
        "ten_complete:", "        LDI 0", "        ST PRED", "        ST NINES",
        "digit_complete:", "        LD J", f"        ADDI -{iterations}", "        JZ all_digits_done",
        "        LD J", "        ADDI 1", "        ST J", "        JMP outer",
        "all_digits_done:", "        LD PRED", "        ADDI '0'", "        OUT",
        "flush_final_nines:", "        LD NINES", "        JZ final_newline",
        "        LDI '9'", "        OUT", "        LD NINES", "        ADDI -1",
        "        ST NINES", "        JMP9 flush_final_nines",
        "final_newline:", "        LDI '\\n'", "        OUT", "        HALT",
        "process_cell:",
        # d = 2*i - 1 (16-bit)
        "        LD I", "        SHL", "        ST DL", "        LD IH", "        ADC IH",
        "        ST DH", "        LD DL", "        ADDI -1", "        ST DL",
        "        LD DH", "        SBC ZERO", "        ST DH",
        # x = 10*a[i] = 8*a + 2*a
        "        LD AL", "        ST ML", "        LD AH", "        ST MH",
    )
    for _ in range(3):
        emit("        LD ML", "        SHL", "        ST ML", "        LD MH", "        ADC MH", "        ST MH")
    emit("        LD ML", "        ST XL", "        LD MH", "        ST XH",
         "        LD AL", "        ST ML", "        LD AH", "        ST MH",
         "        LD ML", "        SHL", "        ST ML", "        LD MH", "        ADC MH", "        ST MH",
         "        LD XL", "        ADD ML", "        ST XL", "        LD XH", "        ADC MH", "        ST XH",
         # q*i by bounded repeated 16-bit addition (q is at most 10 here).
         "        LDI 0", "        ST ML", "        ST MH", "        LD Q", "        TAX",
         "multiply_loop:", "        TXA", "        JZ9 multiply_done",
         "        LD ML", "        ADD I", "        ST ML", "        LD MH", "        ADC IH", "        ST MH",
         "        DEX", "        JZ9 multiply_done", "        JMP9 multiply_loop", "multiply_done:",
         "        LD XL", "        ADD ML", "        ST XL", "        LD XH", "        ADC MH", "        ST XH",
         # x / d by repeated subtraction; the remainder remains 16-bit.
         "        LDI 0", "        ST Q", "divide_loop:", "        LD XL", "        SUB DL", "        ST CL",
         "        LD XH", "        SBC DH", "        JNC divide_done", "        ST XH",
         "        LD CL", "        ST XL", "        LD Q", "        ADDI 1", "        ST Q",
         "        JMP9 divide_loop", "divide_done:", "        RET")
    return "\n".join(lines) + "\n"


def gray_source() -> str:
    lines = [
        "; Compute the four-bit reflected Gray code sequence in CPU registers.",
        "; SHR extracts adjacent bits; control flow computes their XOR.",
        ".equ VALUE, 0x80", ".equ WORK, 0x81", ".equ PREV, 0x82",
        ".equ PLACE, 0x83", ".equ GRAY, 0x84",
        "        BANK 1", "        LDI 0", "        ST VALUE", "gray_loop:",
        "        LD VALUE", "        ST WORK", "        LDI 0", "        ST GRAY",
        "        LDI 1", "        ST PLACE", "        LD WORK", "        SHR", "        ST WORK",
        "        JNC bit0_clear", "        LDI 1", "        ST PREV", "        JMP bit0_ready",
        "bit0_clear:", "        LDI 0", "        ST PREV", "bit0_ready:",
    ]
    for bit in range(1, 4):
        lines.extend([
            f"gray_bit_{bit}:", "        LD WORK", "        SHR", "        ST WORK",
            f"        JNC gray_current_zero_{bit}",
            f"        LD PREV", f"        JZ gray_set_bit_{bit}",
            "        LDI 1", "        ST PREV", f"        JMP gray_bit_done_{bit}",
            f"gray_set_bit_{bit}:", "        LD GRAY", "        ADD PLACE", "        ST GRAY",
            "        LDI 1", "        ST PREV", f"        JMP gray_bit_done_{bit}",
            f"gray_current_zero_{bit}:", "        LD PREV", f"        JZ gray_bit_zero_{bit}",
            "        LD GRAY", "        ADD PLACE", "        ST GRAY", "        LDI 0", "        ST PREV",
            f"        JMP gray_bit_done_{bit}", f"gray_bit_zero_{bit}:",
            "        LDI 0", "        ST PREV", f"gray_bit_done_{bit}:",
            "        LD PLACE", "        ADD PLACE", "        ST PLACE",
        ])
    lines.extend([
        "        LD PREV", "        JZ gray_high_clear", "        LD GRAY", "        ADD PLACE", "        ST GRAY",
        "gray_high_clear:", "        LD GRAY", "        OUT", "        LD VALUE", "        ADDI 1", "        ST VALUE",
        "        ADDI -16", "        JZ gray_done", "        JMP gray_loop", "gray_done:", "        HALT",
    ])
    return "\n".join(lines) + "\n"


def life_source(frame_count: int = 8) -> str:
    lines = [
        "; Conway 8x8 finite board, closed edges, and two complete buffers.",
        "; Bank1 offsets 0..63=A and 64..127=B; page0 E0 selects display, E1 is frame sequence.",
        ".equ SELECTOR, 0xE0", ".equ GENERATION, 0xE1", ".equ CELL, 0xE2",
        ".equ SOURCE, 0xE3", ".equ DEST, 0xE4", ".equ SUM, 0xE5",
        ".equ CURRENT, 0xE6", ".equ MASK, 0xE7",
        "        BANK 0", "        LDI 0", "        ST GENERATION", "generation:",
        "        LD SELECTOR", "        JZ source_a",
        "        LDI 64", "        ST SOURCE", "        LDI 0", "        ST DEST", "        JMP frame_setup",
        "source_a:", "        LDI 0", "        ST SOURCE", "        LDI 64", "        ST DEST",
        "frame_setup:", "        LDI 0", "        ST CELL", "cell_loop:",
        "        LD CELL", "        TAX", "        LDAX", "        ST MASK", "        LDI 0", "        ST SUM",
        "        LD CELL", "        ADD SOURCE", "        TAX", "        BANK 1", "        LDAX",
        "        BANK 0", "        ST CURRENT",
    ]
    directions = [(-9, "nw"), (-8, "n"), (-7, "ne"), (-1, "w"), (1, "e"),
                  (7, "sw"), (8, "s"), (9, "se")]
    for offset, name in directions:
        lines.extend([
            "        LD MASK", "        SHR", "        ST MASK", f"        JNC skip_{name}",
            "        LD CELL", "        ADD SOURCE", f"        ADDI {offset}", "        TAX",
            "        BANK 1", "        LDAX", "        BANK 0", "        ADD SUM", "        ST SUM",
            f"skip_{name}:",
        ])
    lines.extend([
        "        LD CURRENT", "        JZ dead_cell", "        LD SUM", "        ADDI -2", "        JZ live_cell",
        "        LD SUM", "        ADDI -3", "        JZ live_cell", "        LDI 0", "        JMP store_cell",
        "dead_cell:", "        LD SUM", "        ADDI -3", "        JZ live_cell", "        LDI 0", "        JMP store_cell",
        "live_cell:", "        LDI 1", "store_cell:", "        ST CURRENT", "        LD CELL", "        ADD DEST", "        TAX",
        "        LD CURRENT", "        BANK 1", "        STAX", "        BANK 0",
        "        LD CELL", "        ADDI 1", "        ST CELL", "        ADDI -64", "        JZ frame_done", "        JMP cell_loop",
        "frame_done:", "        LD SELECTOR", "        XORI 1", "        ST SELECTOR",
        "        LD GENERATION", "        ADDI 1", "        ST GENERATION",
        f"        ADDI -{frame_count}", "        JZ life_done", "        JMP generation", "life_done:", "        HALT",
    ])
    return "\n".join(lines) + "\n"


def simple_sources() -> dict[str, str]:
    return {
        "lfsr.asm": """; 8-bit maximal LFSR: x^8+x^6+x^5+x^4+1, right shift, seed A5.
.equ COUNT, 0x80
        BANK 1
        LDI 0xA5
        TAX
        LDI 0
        ST COUNT
lfsr_loop:
        TXA
        OUT
        TXA
        SHR
        TAX
        JNC lfsr_no_feedback
        TXA
        XORI 0xB8
        TAX
lfsr_no_feedback:
        LD COUNT
        ADDI 1
        ST COUNT
        ADDI -16
        JZ lfsr_done
        JMP lfsr_loop
lfsr_done:
        HALT
""",
        "fibonacci16.asm": """; Compute and emit F0..F15 as little-endian low/high bytes.
.equ A_LO, 0x80
.equ A_HI, 0x81
.equ B_LO, 0x82
.equ B_HI, 0x83
.equ T_LO, 0x84
.equ T_HI, 0x85
.equ COUNT, 0x86
        BANK 1
        LDI 0
        ST A_LO
        ST A_HI
        LDI 1
        ST B_LO
        LDI 0
        ST B_HI
        LDI 0
        OUT
        OUT
        LDI 1
        OUT
        LDI 0
        OUT
        LDI 14
        ST COUNT
fib_loop:
        LD A_LO
        ADD B_LO
        ST T_LO
        LD A_HI
        ADC B_HI
        ST T_HI
        LD T_LO
        OUT
        LD T_HI
        OUT
        LD B_LO
        ST A_LO
        LD B_HI
        ST A_HI
        LD T_LO
        ST B_LO
        LD T_HI
        ST B_HI
        LD COUNT
        ADDI -1
        ST COUNT
        JZ fib_done
        JMP fib_loop
fib_done:
        HALT
""",
        "array_sort.asm": """; Bubble-sort eight bytes in RAM bank1 offsets 0..7.
.equ PASS, 0x80
.equ INDEX, 0x81
.equ LIMIT, 0x82
.equ LEFT, 0x83
.equ RIGHT, 0x84
        BANK 1
        LDI 0
        ST PASS
sort_outer:
        LDI 7
        SUB PASS
        ST LIMIT
        LDI 0
        ST INDEX
sort_inner:
        LD INDEX
        TAX
        LDAX
        ST LEFT
        INX
        LDAX
        ST RIGHT
        LD RIGHT
        SUB LEFT
        JC sort_no_swap
        LD LEFT
        STAX
        DEX
        LD RIGHT
        STAX
sort_no_swap:
        LD INDEX
        ADDI 1
        ST INDEX
        SUB LIMIT
        JZ sort_next_pass
        JMP sort_inner
sort_next_pass:
        LD PASS
        ADDI 1
        ST PASS
        ADDI -7
        JZ sort_done
        JMP sort_outer
sort_done:
        HALT
""",
        "prime_sieve.asm": """; Sieve of Eratosthenes for 2..100, flags in RAM bank1 offsets 0..100.
.equ PRIME, 0x80
.equ MULTIPLE, 0x81
        BANK 1
        LDI 2
        TAX
sieve_init:
        LDI 1
        STAX
        INX
        TXA
        ADDI -101
        JZ sieve_initialized
        JMP sieve_init
sieve_initialized:
        LDI 2
        ST PRIME
sieve_outer:
        LD PRIME
        TAX
        ADD PRIME
        ST MULTIPLE
sieve_inner:
        LD MULTIPLE
        TAX
        LDI 0
        STAX
        LD MULTIPLE
        ADD PRIME
        ST MULTIPLE
        LDI 100
        SUB MULTIPLE
        JC sieve_inner
        LD PRIME
        ADDI 1
        ST PRIME
        ADDI -11
        JZ sieve_done
        JMP sieve_outer
sieve_done:
        HALT
""",
        "gcd.asm": """; Euclid's repeated-subtraction algorithm for gcd(252,105).
.equ A, 0x80
.equ B, 0x81
.equ RESULT, 0x82
        BANK 1
        LDI 252
        ST A
        LDI 105
        ST B
gcd_loop:
        LD A
        SUB B
        JZ gcd_done
        JC gcd_a_ge
        LD B
        SUB A
        ST B
        JMP gcd_loop
gcd_a_ge:
        LD A
        SUB B
        ST A
        JMP gcd_loop
gcd_done:
        LD A
        ST RESULT
        OUT
        HALT
""",
        "crc8.asm": """; CRC-8/ATM polynomial 07 over input byte A5 with initial CRC 00.
.equ CRC, 0x80
        BANK 1
        LDI 0xA5
        ST CRC
        LDI 8
        TAX
crc_loop:
        LD CRC
        SHL
        ST CRC
        JNC crc_no_xor
        LD CRC
        XORI 0x07
        ST CRC
crc_no_xor:
        DEX
        JZ crc_done
        JMP crc_loop
crc_done:
        LD CRC
        OUT
        HALT
""",
        "ram_cross_page.asm": """; Write/read distinct sentinels at offsets 00 and FF on all four pages.
        BANK 0
        LDI 0x10
        ST 0xFF
        LDI 0x11
        ST 0x00
        BANK 1
        LDI 0x20
        ST 0xFF
        LDI 0x21
        ST 0x00
        BANK 2
        LDI 0x30
        ST 0xFF
        LDI 0x31
        ST 0x00
        BANK 3
        LDI 0x40
        ST 0xFF
        LDI 0x41
        ST 0x00
        BANK 0
        LD 0xFF
        XORI 0x10
        JZ check_0_low
        JMP9 fail
check_0_low:
        LD 0x00
        XORI 0x11
        JZ check_1_high
        JMP9 fail
check_1_high:
        BANK 1
        LD 0xFF
        XORI 0x20
        JZ check_1_low
        JMP9 fail
check_1_low:
        LD 0x00
        XORI 0x21
        JZ check_2_high
        JMP9 fail
check_2_high:
        BANK 2
        LD 0xFF
        XORI 0x30
        JZ check_2_low
        JMP9 fail
check_2_low:
        LD 0x00
        XORI 0x31
        JZ check_3_high
        JMP9 fail
check_3_high:
        BANK 3
        LD 0xFF
        XORI 0x40
        JZ check_3_low
        JMP9 fail
check_3_low:
        LD 0x00
        XORI 0x41
        JZ passed
        JMP9 fail
passed:
        LDI 'P'
        OUT
        LDI 'A'
        OUT
        LDI 'S'
        OUT
        LDI 'S'
        OUT
        LDI '\\n'
        OUT
        HALT
fail:
        LDI 'F'
        OUT
        LDI '\\n'
        OUT
        HALT
""",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi-only", action="store_true", help="only regenerate the two π spigot sources")
    args = parser.parse_args()
    PROGRAMS.mkdir(parents=True, exist_ok=True)
    for digits in (64, 100):
        path = PROGRAMS / f"pi_spigot_{digits}.asm"
        path.write_text(pi_spigot_source(digits), encoding="utf-8")
        print(path.relative_to(ROOT))
    if not args.pi_only:
        generated = {**simple_sources(), "gray_code.asm": gray_source(),
                     "life_8x8.asm": life_source(8)}
        for filename, source in generated.items():
            path = PROGRAMS / filename
            path.write_text(source, encoding="utf-8")
            print(path.relative_to(ROOT))


if __name__ == "__main__":
    main()
