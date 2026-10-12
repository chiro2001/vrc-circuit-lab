; Bubble-sort eight bytes in RAM bank1 offsets 0..7.
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
