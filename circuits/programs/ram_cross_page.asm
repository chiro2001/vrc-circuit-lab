; Write/read distinct sentinels at offsets 00 and FF on all four pages.
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
        LDI '\n'
        OUT
        HALT
fail:
        LDI 'F'
        OUT
        LDI '\n'
        OUT
        HALT
