; Exercises RAM and X operations, 8-bit carry/no-borrow, high PC, CALL and RET.
        JMP9 entry
        .org 280
entry:  LDI 0x80
        ST 0x20
        LDI 0x7F
        ST 0x21
        LD 0x20
        ADD 0x21
        OUT
        LDI 0x40
        TAX
        LDI 0xAA
        STAX
        LDAX
        OUT
        ADDX
        OUT
        SUBX
        OUT
        LDI 0x80
        ST 0x22
        LDI 0x80
        ADDI 0x80
        ST 0x23
        LD 0x22
        ADC 0x22
        OUT
        LDI 0xFF
        ADDI 1
        LD 0x21
        SBC 0x20
        OUT
        LDI 0x80
        ADDI 0x80
        LD 0x22
        SBC 0x21
        OUT
        INX
        DEX
        TXA
        OUT
        LDI 0x10
        SUB 0x20
        JNC borrow_seen
        JMP9 after_borrow
borrow_seen:
        LDI 'B'
        OUT
after_borrow:
        CALL routine
        LDI 0
        JZ9 finish
        LDI 'X'
        OUT
        JMP9 finish
        .org 350
routine: LDI 'C'
        OUT
        RET
        .org 380
finish: LDI '\n'
        OUT
        HALT
