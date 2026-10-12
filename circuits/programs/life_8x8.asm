; Conway 8x8 finite board, closed edges, and two complete buffers.
; Bank1 offsets 0..63=A and 64..127=B; page0 E0 selects display, E1 is frame sequence.
.equ SELECTOR, 0xE0
.equ GENERATION, 0xE1
.equ CELL, 0xE2
.equ SOURCE, 0xE3
.equ DEST, 0xE4
.equ SUM, 0xE5
.equ CURRENT, 0xE6
.equ MASK, 0xE7
        BANK 0
        LDI 0
        ST GENERATION
generation:
        LD SELECTOR
        JZ source_a
        LDI 64
        ST SOURCE
        LDI 0
        ST DEST
        JMP frame_setup
source_a:
        LDI 0
        ST SOURCE
        LDI 64
        ST DEST
frame_setup:
        LDI 0
        ST CELL
cell_loop:
        LD CELL
        TAX
        LDAX
        ST MASK
        LDI 0
        ST SUM
        LD CELL
        ADD SOURCE
        TAX
        BANK 1
        LDAX
        BANK 0
        ST CURRENT
        LD MASK
        SHR
        ST MASK
        JNC skip_nw
        LD CELL
        ADD SOURCE
        ADDI -9
        TAX
        BANK 1
        LDAX
        BANK 0
        ADD SUM
        ST SUM
skip_nw:
        LD MASK
        SHR
        ST MASK
        JNC skip_n
        LD CELL
        ADD SOURCE
        ADDI -8
        TAX
        BANK 1
        LDAX
        BANK 0
        ADD SUM
        ST SUM
skip_n:
        LD MASK
        SHR
        ST MASK
        JNC skip_ne
        LD CELL
        ADD SOURCE
        ADDI -7
        TAX
        BANK 1
        LDAX
        BANK 0
        ADD SUM
        ST SUM
skip_ne:
        LD MASK
        SHR
        ST MASK
        JNC skip_w
        LD CELL
        ADD SOURCE
        ADDI -1
        TAX
        BANK 1
        LDAX
        BANK 0
        ADD SUM
        ST SUM
skip_w:
        LD MASK
        SHR
        ST MASK
        JNC skip_e
        LD CELL
        ADD SOURCE
        ADDI 1
        TAX
        BANK 1
        LDAX
        BANK 0
        ADD SUM
        ST SUM
skip_e:
        LD MASK
        SHR
        ST MASK
        JNC skip_sw
        LD CELL
        ADD SOURCE
        ADDI 7
        TAX
        BANK 1
        LDAX
        BANK 0
        ADD SUM
        ST SUM
skip_sw:
        LD MASK
        SHR
        ST MASK
        JNC skip_s
        LD CELL
        ADD SOURCE
        ADDI 8
        TAX
        BANK 1
        LDAX
        BANK 0
        ADD SUM
        ST SUM
skip_s:
        LD MASK
        SHR
        ST MASK
        JNC skip_se
        LD CELL
        ADD SOURCE
        ADDI 9
        TAX
        BANK 1
        LDAX
        BANK 0
        ADD SUM
        ST SUM
skip_se:
        LD CURRENT
        JZ dead_cell
        LD SUM
        ADDI -2
        JZ live_cell
        LD SUM
        ADDI -3
        JZ live_cell
        LDI 0
        JMP store_cell
dead_cell:
        LD SUM
        ADDI -3
        JZ live_cell
        LDI 0
        JMP store_cell
live_cell:
        LDI 1
store_cell:
        ST CURRENT
        LD CELL
        ADD DEST
        TAX
        LD CURRENT
        BANK 1
        STAX
        BANK 0
        LD CELL
        ADDI 1
        ST CELL
        ADDI -64
        JZ frame_done
        JMP cell_loop
frame_done:
        LD SELECTOR
        XORI 1
        ST SELECTOR
        LD GENERATION
        ADDI 1
        ST GENERATION
        ADDI -8
        JZ life_done
        JMP generation
life_done:
        HALT
