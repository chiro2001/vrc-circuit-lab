; CRC-8/ATM polynomial 07 over input byte A5 with initial CRC 00.
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
