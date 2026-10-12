; Euclid's repeated-subtraction algorithm for gcd(252,105).
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
