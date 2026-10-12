; Compute the four-bit reflected Gray code sequence in CPU registers.
; SHR extracts adjacent bits; control flow computes their XOR.
.equ VALUE, 0x80
.equ WORK, 0x81
.equ PREV, 0x82
.equ PLACE, 0x83
.equ GRAY, 0x84
        BANK 1
        LDI 0
        ST VALUE
gray_loop:
        LD VALUE
        ST WORK
        LDI 0
        ST GRAY
        LDI 1
        ST PLACE
        LD WORK
        SHR
        ST WORK
        JNC bit0_clear
        LDI 1
        ST PREV
        JMP bit0_ready
bit0_clear:
        LDI 0
        ST PREV
bit0_ready:
gray_bit_1:
        LD WORK
        SHR
        ST WORK
        JNC gray_current_zero_1
        LD PREV
        JZ gray_set_bit_1
        LDI 1
        ST PREV
        JMP gray_bit_done_1
gray_set_bit_1:
        LD GRAY
        ADD PLACE
        ST GRAY
        LDI 1
        ST PREV
        JMP gray_bit_done_1
gray_current_zero_1:
        LD PREV
        JZ gray_bit_zero_1
        LD GRAY
        ADD PLACE
        ST GRAY
        LDI 0
        ST PREV
        JMP gray_bit_done_1
gray_bit_zero_1:
        LDI 0
        ST PREV
gray_bit_done_1:
        LD PLACE
        ADD PLACE
        ST PLACE
gray_bit_2:
        LD WORK
        SHR
        ST WORK
        JNC gray_current_zero_2
        LD PREV
        JZ gray_set_bit_2
        LDI 1
        ST PREV
        JMP gray_bit_done_2
gray_set_bit_2:
        LD GRAY
        ADD PLACE
        ST GRAY
        LDI 1
        ST PREV
        JMP gray_bit_done_2
gray_current_zero_2:
        LD PREV
        JZ gray_bit_zero_2
        LD GRAY
        ADD PLACE
        ST GRAY
        LDI 0
        ST PREV
        JMP gray_bit_done_2
gray_bit_zero_2:
        LDI 0
        ST PREV
gray_bit_done_2:
        LD PLACE
        ADD PLACE
        ST PLACE
gray_bit_3:
        LD WORK
        SHR
        ST WORK
        JNC gray_current_zero_3
        LD PREV
        JZ gray_set_bit_3
        LDI 1
        ST PREV
        JMP gray_bit_done_3
gray_set_bit_3:
        LD GRAY
        ADD PLACE
        ST GRAY
        LDI 1
        ST PREV
        JMP gray_bit_done_3
gray_current_zero_3:
        LD PREV
        JZ gray_bit_zero_3
        LD GRAY
        ADD PLACE
        ST GRAY
        LDI 0
        ST PREV
        JMP gray_bit_done_3
gray_bit_zero_3:
        LDI 0
        ST PREV
gray_bit_done_3:
        LD PLACE
        ADD PLACE
        ST PLACE
        LD PREV
        JZ gray_high_clear
        LD GRAY
        ADD PLACE
        ST GRAY
gray_high_clear:
        LD GRAY
        OUT
        LD VALUE
        ADDI 1
        ST VALUE
        ADDI -16
        JZ gray_done
        JMP gray_loop
gray_done:
        HALT
