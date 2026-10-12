; 8-bit maximal LFSR: x^8+x^6+x^5+x^4+1, right shift, seed A5.
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
