; Compute and emit F0..F15 as little-endian low/high bytes.
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
