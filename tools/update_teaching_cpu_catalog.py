#!/usr/bin/env python3
"""Refresh the editable JSON program catalog and three-language UI metadata."""

from __future__ import annotations

import ast
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
PROGRAM_DIR = ROOT / "circuits" / "programs"
sys.path.insert(0, str(ROOT / "tools"))
from verify_teaching_cpu_programs import crc8_atm, pi_text  # noqa: E402


def tr(en: str, zh: str, ja: str, description_en: str, description_zh: str,
       description_ja: str, expected: str = "") -> dict:
    return {
        "en": {"name": en, "description": description_en, "expectedText": expected},
        "zh": {"name": zh, "description": description_zh, "expectedText": expected},
        "ja": {"name": ja, "description": description_ja, "expectedText": expected},
    }


OLD_TRANSLATIONS = {
    "pi-355-113": tr("Pi: 355/113 long division", "π：355/113 十进制长除法", "π：355/113 の十進長除法",
        "The CPU divides 355 by 113 and then generates six fractional digits by long division. This is the 355/113 approximation; digits agree with π through the sixth fractional place.",
        "CPU 先用长除法求 355÷113，再逐位生成 6 位小数。这是 355/113 近似值，前六位小数与 π 一致。",
        "CPUが355÷113を長除法で求め、小数6桁を逐次生成します。355/113の近似で、小数第6位までπと一致します。", "3.141592\n"),
    "counter": tr("Loop counter", "循环计数器", "ループカウンター", "Accumulates, outputs, and loops.", "累加、输出并循环跳转。", "加算して出力し、ループします。"),
    "logic": tr("Logic operations", "逻辑运算", "論理演算", "Demonstrates immediate AND, OR, and XOR.", "演示立即数 AND、OR、XOR。", "即値 AND、OR、XOR を示します。", "\u0000"),
    "hello": tr("Hello", "Hello", "Hello", "Outputs HELLO and a newline.", "输出 HELLO 和换行。", "HELLO と改行を出力します。", "HELLO\n"),
    "halt": tr("HALT", "停机", "HALT", "The smallest stop program.", "最小停机程序。", "最小の停止プログラム。"),
    "blinker": tr("Running light", "流水灯", "流れるライト", "Rotates one lit bit through the accumulator.", "在累加器中循环移动一个亮位。", "アキュムレーター内の点灯ビットを回転します。"),
    "alternating-55-aa": tr("55 / AA alternation", "55 / AA 交替", "55 / AA 交互", "Alternates between 0x55 and 0xAA.", "交替输出 0x55 和 0xAA。", "0x55 と 0xAA を交互に出力します。"),
    "ff-plus-one": tr("FF plus one", "FF 加一", "FF に 1 を加算", "Shows eight-bit wraparound and carry.", "演示 8 位回绕与进位。", "8ビットの桁あふれとキャリーを示します。", "\u0000"),
    "countdown": tr("Countdown", "倒计时", "カウントダウン", "Counts down to zero and outputs digits.", "递减到零并输出数字。", "ゼロまで減算し、数字を出力します。", "54321\n"),
    "ascii-digits": tr("ASCII 0 to 9", "ASCII 数字 0 到 9", "ASCII 数字 0～9", "Outputs ASCII digits from zero through nine.", "输出 ASCII 字符 0 到 9。", "ASCII の0から9を出力します。", "0123456789\n"),
    "step-count": tr("Step counter", "步长计数", "ステップカウンター", "Adds three on every loop.", "每轮累加 3。", "ループごとに3を加算します。"),
    "long-call": tr("Nine-bit CALL / RET", "9 位地址 CALL / RET", "9ビットアドレス CALL / RET", "Calls and returns from ROM addresses above 255.", "从高于 255 的 ROM 地址调用并返回。", "255を超えるROMアドレスから呼び出して復帰します。", "Hx\n"),
    "cpu-fixture": tr("CPU / RAM validation", "CPU / RAM 验证程序", "CPU / RAM 検証", "Covers RAM, X addressing, carry/borrow, CALL/RET, and a high ROM address.", "覆盖 RAM、X 寻址、进借位、CALL/RET 和高 ROM 地址。", "RAM、Xアドレス、キャリー/ボロー、CALL/RET、高位ROMアドレスを検証します。"),
}

OLD_SOURCE_TRANSLATIONS = {
    "pi-355-113": ("CPU integer long division for the 355/113 approximation.", "CPU 运行整数长除法，计算 355/113 近似值。", "CPUで整数長除法を行い、355/113の近似値を計算します。"),
    "counter": ("Looping accumulator demonstration.", "循环累加器演示。", "ループするアキュムレーターのデモ。"),
    "logic": ("Immediate bitwise logic demonstration.", "立即数按位逻辑运算演示。", "即値ビット演算のデモ。"),
    "hello": ("Terminal output demonstration.", "终端输出演示。", "端末出力のデモ。"),
    "halt": ("Minimal CPU stop program.", "最小 CPU 停机程序。", "最小のCPU停止プログラム。"),
    "blinker": ("Rotating single-bit light pattern.", "循环旋转的单亮位图案。", "1ビット点灯パターンを回転します。"),
    "alternating-55-aa": ("Alternates two bit patterns.", "交替切换两个位图案。", "2つのビットパターンを交互に出力します。"),
    "ff-plus-one": ("Eight-bit addition and carry demonstration.", "8 位加法与进位演示。", "8ビット加算とキャリーのデモ。"),
    "countdown": ("Zero-flag-controlled countdown.", "使用零标志控制倒计时。", "ゼロフラグで制御するカウントダウン。"),
    "ascii-digits": ("ASCII digit output loop.", "ASCII 数字输出循环。", "ASCII数字を出力するループ。"),
    "step-count": ("Fixed-step accumulator loop.", "定步长累加循环。", "一定幅で加算するループ。"),
    "long-call": ("Nine-bit call and return test.", "9 位地址调用与返回测试。", "9ビットアドレスの呼び出しと復帰テスト。"),
    "cpu-fixture": ("CPU and RAM architectural validation program.", "CPU 与 RAM 架构验证程序。", "CPUとRAMのアーキテクチャ検証プログラム。"),
}


def initial_life_ram() -> tuple[dict[str, int], list[int]]:
    directions = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]
    initial: dict[str, int] = {}
    masks: list[int] = []
    for row in range(8):
        for col in range(8):
            mask = 0
            for bit, (dy, dx) in enumerate(directions):
                y, x = row + dy, col + dx
                if 0 <= y < 8 and 0 <= x < 8:
                    mask |= 1 << bit
            masks.append(mask)
            if mask:
                initial[str(row * 8 + col)] = mask
    for row, col in ((0, 1), (1, 2), (2, 0), (2, 1), (2, 2), (5, 3), (5, 4), (5, 5)):
        initial[str(256 + row * 8 + col)] = 1
    return initial, masks


def add_program(manifest: list[dict], item: dict) -> None:
    manifest.append(item)


def build_catalog() -> list[dict]:
    json_path = PROGRAM_DIR / "catalog.json"
    existing = []
    if json_path.exists():
        existing = json.loads(json_path.read_text(encoding="utf-8"))
        old = [item for item in existing if item["id"] in OLD_TRANSLATIONS]
    else:
        old = ast.literal_eval((PROGRAM_DIR / "catalog.pydata").read_text(encoding="utf-8"))
    manifest: list[dict] = []
    for source in old:
        item = dict(source)
        item["translations"] = OLD_TRANSLATIONS[item["id"]]
        source_text = (PROGRAM_DIR / item["source"]).read_text(encoding="utf-8")
        source_comment = next((line[1:].strip() for line in source_text.splitlines() if line.lstrip().startswith(";")), "")
        item["sourceComment"] = source_comment
        localized_comment = OLD_SOURCE_TRANSLATIONS[item["id"]]
        item["sourceCommentTranslations"] = {"en": localized_comment[0], "zh": localized_comment[1], "ja": localized_comment[2]}
        add_program(manifest, item)

    pi_translations = {
        64: tr("Pi spigot: 64 fractional digits", "π 整数喷泉算法：小数 64 位", "πスピゴット：小数64桁",
            "Rabinowitz–Wagon integer spigot. Runs 10,421,948 CPU cycles (about 11.3 hours at 256 Hz) and emits 64 fractional digits computed at runtime.",
            "Rabinowitz–Wagon 整数喷泉算法。CPU 运行 10,421,948 周期（256 Hz 下约 11.3 小时），运行时计算并输出 64 位小数。",
            "Rabinowitz–Wagon 整数スピゴット。CPUを10,421,948サイクル実行（256 Hzで約11.3時間）し、小数64桁を実行時に計算して出力します。"),
        100: tr("Pi spigot: 100 fractional digits", "π 整数喷泉算法：小数 100 位", "πスピゴット：小数100桁",
            "Rabinowitz–Wagon integer spigot. Runs 25,244,764 CPU cycles (about 27.4 hours at 256 Hz) and emits 100 fractional digits computed at runtime.",
            "Rabinowitz–Wagon 整数喷泉算法。CPU 运行 25,244,764 周期（256 Hz 下约 27.4 小时），运行时计算并输出 100 位小数。",
            "Rabinowitz–Wagon 整数スピゴット。CPUを25,244,764サイクル実行（256 Hzで約27.4時間）し、小数100桁を実行時に計算して出力します。"),
    }
    for digits in (64, 100):
        comment = f"Rabinowitz–Wagon CPU integer spigot; {digits} fractional digits."
        expected_pi = pi_text(digits)
        for language in ("en", "zh", "ja"):
            pi_translations[digits][language]["expectedText"] = expected_pi
        add_program(manifest, {
            "id": f"pi-spigot-{digits}",
            "name": f"π 整数喷泉算法：小数 {digits} 位",
            "source": f"pi_spigot_{digits}.asm",
            "description": pi_translations[digits]["zh"]["description"],
            "expectedText": expected_pi,
            "digits": digits,
            "oracle": "pi-spigot",
            "initialRamPatterns": [{"start": 256, "count": 10 * (digits + 1) // 3, "values": [2, 0]}],
            "translations": pi_translations[digits],
            "sourceComment": comment,
            "sourceCommentTranslations": {"en": comment, "zh": f"CPU运行整数喷泉算法，计算小数{digits}位。", "ja": f"CPUで整数スピゴットを実行し、小数{digits}桁を計算します。"},
        })

    add_program(manifest, {
        "id": "gray-code", "name": "格雷码序列", "source": "gray_code.asm",
        "description": "CPU 逐位提取二进制计数器的相邻位并异或，运行时生成 0..15 的四位反射格雷码。",
        "expectedBytes": [value ^ (value >> 1) for value in range(16)], "oracle": "gray4",
        "translations": tr("Gray-code sequence", "格雷码序列", "グレイコード列",
            "Extracts adjacent bits from a binary counter and computes the four-bit reflected Gray sequence at runtime.",
            "CPU 逐位提取二进制计数器的相邻位并异或，运行时生成 0..15 的四位反射格雷码。",
            "二進カウンターの隣接ビットを取り出してXORし、4ビット反射グレイコードを実行時に生成します。"),
        "sourceComment": "Computes the four-bit Gray sequence by extracting adjacent binary bits.",
        "sourceCommentTranslations": {"en": "Computes the four-bit Gray sequence by extracting adjacent binary bits.", "zh": "提取相邻二进制位并计算四位格雷码。", "ja": "隣接する二進ビットから4ビットのグレイコードを計算します。"},
    })
    lfsr = []
    state = 0xA5
    for _ in range(16):
        lfsr.append(state)
        carry = state & 1
        state >>= 1
        if carry:
            state ^= 0xB8
    add_program(manifest, {
        "id": "lfsr", "name": "8 位 LFSR", "source": "lfsr.asm",
        "description": "使用多项式 x⁸+x⁶+x⁵+x⁴+1，从 A5 种子运行并输出 16 个状态。",
        "expectedBytes": lfsr, "oracle": "lfsr8",
        "translations": tr("8-bit LFSR", "8 位 LFSR", "8ビットLFSR",
            "Runs an eight-bit right-shifting LFSR with polynomial x^8+x^6+x^5+x^4+1 from seed A5.",
            "使用多项式 x⁸+x⁶+x⁵+x⁴+1，从 A5 种子运行并输出 16 个状态。",
            "多項式 x⁸+x⁶+x⁵+x⁴+1、シードA5の8ビット右シフトLFSRを実行します。"),
        "sourceComment": "8-bit maximal LFSR using a right shift and feedback polynomial.",
        "sourceCommentTranslations": {"en": "8-bit maximal LFSR using a right shift and feedback polynomial.", "zh": "使用右移和反馈多项式的 8 位最大长度 LFSR。", "ja": "右シフトとフィードバック多項式を使う8ビット最大長LFSR。"},
    })
    fib = []
    a, b = 0, 1
    for _ in range(16):
        fib.extend((a & 0xFF, a >> 8))
        a, b = b, (a + b) & 0xFFFF
    add_program(manifest, {
        "id": "fibonacci-16", "name": "16 位 Fibonacci", "source": "fibonacci16.asm",
        "description": "用低/高字节的 ADD 与 ADC 在 CPU 上计算 F0..F15，终端按每项低字节、高字节输出。",
        "expectedBytes": fib, "oracle": "fibonacci16",
        "translations": tr("16-bit Fibonacci", "16 位 Fibonacci", "16ビットFibonacci",
            "Computes F0 through F15 with low/high-byte ADD and ADC; each value is emitted low byte then high byte.",
            "用低/高字节的 ADD 与 ADC 在 CPU 上计算 F0..F15，终端按每项低字节、高字节输出。",
            "下位/上位バイトのADDとADCでF0～F15を計算し、各値を下位バイト、上位バイトの順に出力します。"),
        "sourceComment": "Computes 16-bit Fibonacci values using carry propagation.",
        "sourceCommentTranslations": {"en": "Computes 16-bit Fibonacci values using carry propagation.", "zh": "通过进位传播计算 16 位 Fibonacci 数。", "ja": "キャリー伝播を使って16ビットのFibonacci数を計算します。"},
    })
    sort_input = [7, 1, 5, 3, 8, 2, 6, 4]
    add_program(manifest, {
        "id": "array-sort", "name": "8 项数组排序", "source": "array_sort.asm",
        "description": "CPU 对 RAM bank1 的八个输入字节执行冒泡排序，结果仍保存在原数组。",
        "initialRam": {str(256 + index): value for index, value in enumerate(sort_input)},
        "inputValues": sort_input, "oracle": "array-sort8",
        "translations": tr("Eight-element array sort", "8 项数组排序", "8要素配列ソート",
            "Bubble-sorts eight input bytes in RAM bank1 in place.",
            "CPU 对 RAM bank1 的八个输入字节执行冒泡排序，结果仍保存在原数组。",
            "RAM bank1の8バイトをバブルソートし、元の配列へ書き戻します。"),
        "sourceComment": "Bubble-sorts eight bytes in RAM bank1.",
        "sourceCommentTranslations": {"en": "Bubble-sorts eight bytes in RAM bank1.", "zh": "对 RAM bank1 中的八个字节执行冒泡排序。", "ja": "RAM bank1の8バイトをバブルソートします。"},
    })
    add_program(manifest, {
        "id": "prime-sieve", "name": "2 到 100 素数筛", "source": "prime_sieve.asm",
        "description": "CPU 初始化候选标记并运行埃拉托斯特尼筛，RAM bank1 的 2..100 标记为 1 的位置是素数。",
        "oracle": "prime-sieve100",
        "translations": tr("Sieve of primes through 100", "2 到 100 素数筛", "100までのエラトステネスのふるい",
            "Initializes candidate flags and runs the sieve of Eratosthenes. Prime entries remain set in RAM bank1.",
            "CPU 初始化候选标记并运行埃拉托斯特尼筛，RAM bank1 的 2..100 标记为 1 的位置是素数。",
            "候補フラグを初期化してエラトステネスのふるいを実行し、RAM bank1の素数位置を1のまま残します。"),
        "sourceComment": "Sieve of Eratosthenes for the integers 2 through 100.",
        "sourceCommentTranslations": {"en": "Sieve of Eratosthenes for the integers 2 through 100.", "zh": "对 2 到 100 执行埃拉托斯特尼筛。", "ja": "2から100までにエラトステネスのふるいを実行します。"},
    })
    add_program(manifest, {
        "id": "gcd", "name": "欧几里得 GCD", "source": "gcd.asm",
        "description": "CPU 用重复减法执行欧几里得算法，计算 gcd(252,105)=21，并输出原始结果字节。",
        "expectedBytes": [21], "oracle": "gcd",
        "translations": tr("Euclidean GCD", "欧几里得 GCD", "ユークリッドのGCD",
            "Uses repeated subtraction to compute gcd(252,105)=21 and emits the raw result byte.",
            "CPU 用重复减法执行欧几里得算法，计算 gcd(252,105)=21，并输出原始结果字节。",
            "反復減算でgcd(252,105)=21を計算し、結果の生バイトを出力します。"),
        "sourceComment": "Euclid's repeated-subtraction algorithm for gcd(252,105).",
        "sourceCommentTranslations": {"en": "Euclid's repeated-subtraction algorithm for gcd(252,105).", "zh": "用重复减法计算 gcd(252,105)。", "ja": "反復減算でgcd(252,105)を計算します。"},
    })
    crc = crc8_atm(b"\xA5")
    add_program(manifest, {
        "id": "crc8", "name": "CRC-8/ATM", "source": "crc8.asm",
        "description": f"CPU 使用多项式 0x07 对输入字节 A5 逐位计算 CRC-8/ATM，输出结果字节 0x{crc:02X}。",
        "expectedBytes": [crc], "oracle": "crc8-atm-a5",
        "translations": tr("CRC-8/ATM", "CRC-8/ATM", "CRC-8/ATM",
            f"Computes CRC-8/ATM with polynomial 0x07 over input byte A5; result is 0x{crc:02X}.",
            f"CPU 使用多项式 0x07 对输入字节 A5 逐位计算 CRC-8/ATM，输出结果字节 0x{crc:02X}。",
            f"多項式0x07で入力バイトA5のCRC-8/ATMを計算し、結果0x{crc:02X}を出力します。"),
        "sourceComment": "CRC-8/ATM polynomial 07 over input byte A5.",
        "sourceCommentTranslations": {"en": "CRC-8/ATM polynomial 07 over input byte A5.", "zh": "对输入 A5 使用 CRC-8/ATM 多项式 07。", "ja": "入力A5にCRC-8/ATM多項式07を適用します。"},
    })
    add_program(manifest, {
        "id": "ram-cross-page", "name": "跨页 RAM 自检", "source": "ram_cross_page.asm",
        "description": "在四个 RAM 页的 0x00 与 0xFF 写入不同哨兵值，再逐页读回；全部匹配时输出 PASS。",
        "expectedText": "PASS\n", "oracle": "ram-cross-page",
        "translations": tr("Cross-page RAM self-test", "跨页 RAM 自检", "ページ跨ぎRAMセルフテスト",
            "Writes distinct sentinels at 0x00 and 0xFF in all four pages, reads them back, and prints PASS on success.",
            "在四个 RAM 页的 0x00 与 0xFF 写入不同哨兵值，再逐页读回；全部匹配时输出 PASS。",
            "4ページすべての0x00と0xFFに異なる値を書いて読み戻し、成功時にPASSを出力します。", "PASS\n"),
        "sourceComment": "Checks distinct RAM sentinels across all four pages.",
        "sourceCommentTranslations": {"en": "Checks distinct RAM sentinels across all four pages.", "zh": "检查四个 RAM 页中的不同哨兵值。", "ja": "4つのRAMページにある異なる値を確認します。"},
    })
    life_initial, masks = initial_life_ram()
    display = {"displayRamBase": 256, "displayAlternateBase": 320,
               "selectorAddress": 224, "generationAddress": 225,
               "commitOrder": ["selector", "generation"]}
    add_program(manifest, {
        "id": "life-8x8", "name": "Conway 生命游戏 8×8", "source": "life_8x8.asm",
        "description": "封闭边界 8×8 Conway 生命游戏，初始包含 glider 与 blinker；双缓冲完整写入后切换显示页，运行 8 帧。",
        "initialRam": life_initial, "frameCount": 8, "neighborMasks": masks,
        "oracle": "life8x8", "display": display,
        "translations": tr("Conway's Game of Life 8×8", "Conway 生命游戏 8×8", "Conwayのライフゲーム 8×8",
            "Closed-edge 8×8 board seeded with a glider and blinker. Computes eight generations in two RAM buffers and commits each completed frame.",
            "封闭边界 8×8 Conway 生命游戏，初始包含 glider 与 blinker；双缓冲完整写入后切换显示页，运行 8 帧。",
            "閉じた境界の8×8盤面にグライダーとブリンカーを配置。2つのRAMバッファで8世代を計算し、完成したフレームを切り替えます。"),
        "sourceComment": "Closed-edge Conway Life with two frame buffers and a commit selector.",
        "sourceCommentTranslations": {"en": "Closed-edge Conway Life with two frame buffers and a commit selector.", "zh": "封闭边界 Conway 生命游戏，使用双缓冲与帧提交选择器。", "ja": "閉じた境界のConwayライフゲーム。二重バッファとフレーム切替を使用します。"},
    })
    # Keep player/community examples that are not regenerated by this builder.
    generated = {item["id"] for item in manifest}
    manifest.extend(item for item in existing if item["id"] not in generated)
    return manifest


def main() -> None:
    manifest = build_catalog()
    path = PROGRAM_DIR / "catalog.json"
    path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {len(manifest)} programs to {path.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
