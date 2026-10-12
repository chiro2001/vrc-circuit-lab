#!/usr/bin/env python3
"""Build readable three-language ISA pages; no program execution or asset edits."""
import json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from teaching_cpu_assembler import ISA
ROOT = Path(__file__).resolve().parents[1]
pages=[]
def page(en_title,zh_title,ja_title,en,zh,ja):
    pages.append({'titles':[en_title,zh_title,ja_title],'bodies':[en,zh,ja]})
page('CPU OVERVIEW','CPU 概览','CPU 概要',
     '8-bit ACC / X / SP / OUT\n9-bit PC · 512 × 12-bit ROM\n1 KiB RAM = 4 pages × 256 bytes\nBANK = 0..3; ordinary RAM accesses use BANK.\nCALL / RET stack always uses page 0.\nRESET: CPU + BANK=0; all RAM retained.\nLOAD / RESTART: restore program initial RAM.\nEvery clock edge runs the synthesized GPU netlist.',
     'ACC / X / SP / OUT：8 位\nPC：9 位 · ROM：512 × 12 位\nRAM：1 KiB = 4 页 × 256 字节\nBANK：0..3；普通访存使用当前页。\nCALL / RET 栈始终位于第 0 页。\nRESET：复位 CPU 与 BANK，保留全部 RAM。\nLOAD / RESTART：恢复程序初始 RAM。\n每个时钟沿执行综合网表，由 GPU 计算。',
     'ACC / X / SP / OUT：8 ビット\nPC：9 ビット · ROM：512 × 12 ビット\nRAM：1 KiB = 4 ページ × 256 バイト\nBANK：0..3；通常アクセスは現在のページ。\nCALL / RET のスタックは常にページ 0。\nRESET：CPU と BANK をリセット、RAM を保持。\nLOAD / RESTART：プログラムの初期 RAM を復元。\n各クロックで合成ネットリストを GPU で計算。')
page('WORD ENCODING','指令编码','命令エンコード',
     '[11:8] opcode | [7:0] imm / RAM offset\nExample: LDI 0x41 = 0x041 = 0000 0100 0001\nEXT: [11:8]=E | [7:4] subop | [3:0] modifier\nBANK 0/1/2/3: E21 / E22 / E23 / E24\nE20 remains TAX.\nJC/JNC/JMP9/JZ9/CALL: two ROM words.\nWord 2: [8:0] absolute PC target.\nShort JMP/JZ can only target 0x000..0x0FF.',
     '[11:8] 操作码 | [7:0] 立即数 / RAM 页内偏移\n示例：LDI 0x41 = 0x041 = 0000 0100 0001\nEXT：[11:8]=E | [7:4] 子操作码 | [3:0] 修饰\nBANK 0/1/2/3：E21 / E22 / E23 / E24\nE20 仍然是 TAX。\nJC/JNC/JMP9/JZ9/CALL 占两个 ROM 字。\n第二字：[8:0] 是绝对 PC 目标地址。\n短 JMP/JZ 只可跳至 0x000..0x0FF。',
     '[11:8] オペコード | [7:0] 即値 / RAM オフセット\n例：LDI 0x41 = 0x041 = 0000 0100 0001\nEXT：[11:8]=E | [7:4] サブ命令 | [3:0] 修飾\nBANK 0/1/2/3：E21 / E22 / E23 / E24\nE20 は TAX のまま。\nJC/JNC/JMP9/JZ9/CALL は ROM 2 ワード。\n第 2 ワード：[8:0] は絶対 PC アドレス。\n短い JMP/JZ の範囲は 0x000..0x0FF。')
page('INSTRUCTION DIRECTORY','基本指令目录','基本命令一覧',
     '0 LDI   1 ADDI   2 ANDI   3 ORI\n4 XORI  5 OUT    6 JMP    7 JZ\n8 LD    9 ST     A ADD    B SUB\nC ADC   D SBC    E EXT    F HALT\n\nNext pages: extensions, flags and examples.\nThen one page per instruction, in opcode order.\nUse PREVIOUS / NEXT to inspect details.',
     '0 LDI   1 ADDI   2 ANDI   3 ORI\n4 XORI  5 OUT    6 JMP    7 JZ\n8 LD    9 ST     A ADD    B SUB\nC ADC   D SBC    E EXT    F HALT\n\n接下来是扩展指令、标志规则与例子。\n其后按操作码顺序，每条指令独立一页。\n使用上一页 / 下一页查看详情。',
     '0 LDI   1 ADDI   2 ANDI   3 ORI\n4 XORI  5 OUT    6 JMP    7 JZ\n8 LD    9 ST     A ADD    B SUB\nC ADC   D SBC    E EXT    F HALT\n\n次は拡張命令、フラグと例。\n以降はオペコード順、各命令に独立したページ。\n前 / 次で詳細を表示。')
page('EXTENSION DIRECTORY','扩展指令目录','拡張命令一覧',
     'E00 SHL  E10 SHR  E20 TAX  E30 TXA\nE40 LDAX E50 STAX E60 INX  E70 DEX\nE80 ADDX E90 SUBX EA0 JC   EB0 JNC\nEC0 JMP9 ED0 JZ9  EE0 CALL EF0 RET\nE21..E24 BANK 0..3\n\nRAM physical address = BANK × 256 + offset.\n8-bit operations wrap modulo 256.',
     'E00 SHL  E10 SHR  E20 TAX  E30 TXA\nE40 LDAX E50 STAX E60 INX  E70 DEX\nE80 ADDX E90 SUBX EA0 JC   EB0 JNC\nEC0 JMP9 ED0 JZ9  EE0 CALL EF0 RET\nE21..E24 BANK 0..3\n\nRAM 物理地址 = BANK × 256 + 页内偏移。\n8 位运算结果按模 256 回绕。',
     'E00 SHL  E10 SHR  E20 TAX  E30 TXA\nE40 LDAX E50 STAX E60 INX  E70 DEX\nE80 ADDX E90 SUBX EA0 JC   EB0 JNC\nEC0 JMP9 ED0 JZ9  EE0 CALL EF0 RET\nE21..E24 BANK 0..3\n\nRAM 物理アドレス = BANK × 256 + オフセット。\n8 ビット演算は 256 を法として循環。')
page('FLAGS AND MULTI-BYTE ARITHMETIC','标志与多字节运算','フラグと多バイト演算',
     'Z = 1 when the updated result is zero.\nADD / ADC: C = unsigned carry out.\nSUB / SBC: C = 1 means NO BORROW.\nSBC computes ACC − RAM − (1 − C).\nLD / LDAX / TXA preserve C; LDI clears C.\nFor 16-bit add: ADD low, LD high, ADC high.\nFor 16-bit subtract: SUB low, LD high, SBC high.\nDo not insert LDI between the low and high words.',
     'Z = 1：本次更新结果为零。\nADD / ADC：C 表示无符号进位。\nSUB / SBC：C = 1 表示没有借位。\nSBC 计算 ACC − RAM − (1 − C)。\nLD / LDAX / TXA 保留 C；LDI 清除 C。\n16 位加法：低字节 ADD，高字节 LD 后 ADC。\n16 位减法：低字节 SUB，高字节 LD 后 SBC。\n不要在低、高字节之间插入 LDI。',
     'Z = 1：更新した結果がゼロ。\nADD / ADC：C は符号なしの桁上がり。\nSUB / SBC：C = 1 は借りなし。\nSBC は ACC − RAM − (1 − C)。\nLD / LDAX / TXA は C を保持；LDI は C をクリア。\n16 ビット加算：下位 ADD、上位 LD の後 ADC。\n16 ビット減算：下位 SUB、上位 LD の後 SBC。\n下位と上位の間に LDI を挿入しない。')
page('WORKED EXAMPLE','编码实例','実行例',
     'LDI 0x41  → 041   ACC=65, Z=0, C=0\nOUT       → 500   terminal prints A; LEDs=01000001\nBANK 2    → E23   BANK=2, flags unchanged\nST 0x10   → 910   RAM[0x210]=65\nLDI 0     → 000   ACC=0, Z=1, C=0\nLD 0x10   → 810   ACC=65, Z=0, C remains 0\nHALT      → F00   pause until RESET / RESTART\n\nShared page; each player chooses their own language.',
     'LDI 0x41  → 041   ACC=65，Z=0，C=0\nOUT       → 500   终端输出 A，LED=01000001\nBANK 2    → E23   BANK=2，不改变标志\nST 0x10   → 910   RAM[0x210]=65\nLDI 0     → 000   ACC=0，Z=1，C=0\nLD 0x10   → 810   ACC=65，Z=0，C 仍为 0\nHALT      → F00   停机，等待 RESET / RESTART\n\n页码为共享状态；各玩家独立选择语言。',
     'LDI 0x41  → 041   ACC=65、Z=0、C=0\nOUT       → 500   端末に A、LED=01000001\nBANK 2    → E23   BANK=2、フラグは保持\nST 0x10   → 910   RAM[0x210]=65\nLDI 0     → 000   ACC=0、Z=1、C=0\nLD 0x10   → 810   ACC=65、Z=0、C は 0\nHALT      → F00   RESET / RESTART まで停止\n\nページは共有、言語はプレイヤーごとに選択。')
# Algebraic effects are language-neutral; teaching notes are translated.
effects=['ACC ← imm8','ACC ← ACC + imm8','ACC ← ACC AND imm8','ACC ← ACC OR imm8','ACC ← ACC XOR imm8','OUT ← ACC; terminal write','PC ← addr8','if Z=1: PC ← addr8','ACC ← RAM[BANK:addr8]','RAM[BANK:addr8] ← ACC','ACC ← ACC + RAM[BANK:addr8]','ACC ← ACC − RAM[BANK:addr8]','ACC ← ACC + RAM[BANK:addr8] + C','ACC ← ACC − RAM[BANK:addr8] − (1−C)','EXT → subopcode','HALT ← 1']
flags=['Z=result==0, C=0','Z=result==0, C=carry','Z=result==0, C=0','Z=result==0, C=0','Z=result==0, C=0','—','—','—','Z=result==0; C preserved','—','Z=result==0, C=carry','Z=result==0, C=no-borrow','Z=result==0, C=carry','Z=result==0, C=no-borrow','see extension page','—']
notes=[('No signed overflow flag. Values are 8-bit unsigned bytes.','没有有符号溢出标志；数值为 8 位无符号字节。','符号付きオーバーフローのフラグはない。値は 8 ビット符号なし。'),('This is a rising-edge functional model; no gate delays or glitches.','这里使用上升沿功能仿真，不包含门延迟和毛刺。','立ち上がりエッジの機能シミュレーション。遅延やグリッチは含まない。')]
for op,effect,flag in zip(ISA['opcodes'],effects,flags):
    code=op['code']; name=op['mnemonic']; encoding=f'{code:X}xx' if code not in (5,14,15) else f'{code:X}00'
    operands='0x10' if code not in (5,14,15) else ''
    titles=[f'{code:X}  {name}']*3
    text=[]
    for lang in range(3):
        labels=[('ENCODING','EFFECT','FLAGS','EXAMPLE'),('编码','作用','标志','例子'),('コード','動作','フラグ','例')][lang]
        text.append(f'{labels[0]}: {encoding} · 1 ROM word\n\n{labels[1]}\n{effect}\n\n{labels[2]}: {flag}\n\n{labels[3]}: {name} {operands}\n{notes[code%2][lang]}')
    page(*titles,*text)
ext_effects=['ACC ← ACC << 1; C ← old bit7','ACC ← ACC >> 1; C ← old bit0','X ← ACC','ACC ← X','ACC ← RAM[BANK:X]','RAM[BANK:X] ← ACC','X ← X + 1','X ← X − 1','ACC ← ACC + X','ACC ← ACC − X','if C=1: PC ← word2[8:0]','if C=0: PC ← word2[8:0]','PC ← word2[8:0]','if Z=1: PC ← word2[8:0]','RAM[0:SP] ← return low; RAM[0:SP+1] ← return high; SP ← SP+2; PC ← word2[8:0]','PC ← RAM[0:SP−2] + 256×RAM[0:SP−1]; SP ← SP−2']
ext_flags=['Z=result==0; C=old bit7','Z=result==0; C=old bit0','—','Z=result==0; C preserved','Z=result==0; C preserved','—','Z=X==0; C preserved','Z=X==0; C preserved','Z=result==0; C=carry','Z=result==0; C=no-borrow']+['—']*6
for op,effect,flag in zip(ISA['extensions'][:16],ext_effects,ext_flags):
    sub=op['subopcode'];name=op['mnemonic'];words=2 if 10<=sub<=14 else 1
    texts=[]
    for lang in range(3):
        labels=[('ENCODING','EFFECT','FLAGS'),('编码','作用','标志'),('コード','動作','フラグ')][lang]
        texts.append(f'{labels[0]}: E{sub:X}0'+(' + 0..1FF' if words==2 else '')+f' · {words} ROM word\n\n{labels[1]}\n{effect}\n\n{labels[2]}: {flag}\n\n'+(['CALL / RET always access page 0, independent of BANK.','CALL / RET 始终访问第 0 页，不受 BANK 影响。','CALL / RET は BANK に関係なくページ 0 にアクセス。'][lang] if sub>=14 else notes[sub%2][lang]))
    page(*([f'E{sub:X}0  {name}']*3),*texts)
page('BANK n','BANK n：选择 RAM 页','BANK n：RAM ページ選択',
     'BANK 0 = E21   BANK 1 = E22\nBANK 2 = E23   BANK 3 = E24\n\nBANK ← n; all other registers and flags preserved.\nRAM address = n × 256 + 8-bit offset.\nExample: BANK 3; ST 0xFF writes RAM[0x3FF].\nRESET sets BANK=0 and retains every page.\nCALL / RET do not change BANK.',
     'BANK 0 = E21   BANK 1 = E22\nBANK 2 = E23   BANK 3 = E24\n\nBANK ← n；保留其它寄存器和标志。\nRAM 地址 = n × 256 + 8 位页内偏移。\n示例：BANK 3；ST 0xFF 写入 RAM[0x3FF]。\nRESET 设置 BANK=0，并保留全部页。\nCALL / RET 不改变 BANK。',
     'BANK 0 = E21   BANK 1 = E22\nBANK 2 = E23   BANK 3 = E24\n\nBANK ← n；他のレジスタとフラグは保持。\nRAM アドレス = n × 256 + 8 ビットオフセット。\n例：BANK 3；ST 0xFF は RAM[0x3FF] に書く。\nRESET は BANK=0、全ページを保持。\nCALL / RET は BANK を変更しない。')
path=ROOT/'Assets/CircuitLab/Data/isa-manual.json'
path.write_text(json.dumps({'pages':pages},ensure_ascii=False,indent=2)+'\n')
print(f'{len(pages)} pages → {path}')
