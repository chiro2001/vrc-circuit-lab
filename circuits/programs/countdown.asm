        LDI 5
        ST COUNT
loop:   LD COUNT
        ADDI '0'
        OUT
        LD COUNT
        ADDI -1
        ST COUNT
        JZ finish
        JMP loop
finish: LDI '\n'
        OUT
        HALT
.equ COUNT, 0
