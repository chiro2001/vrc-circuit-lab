        JMP9 entry
        .org 300
entry:  CALL routine
        LDI 'x'
        OUT
        JMP9 finish
        .org 400
routine: LDI 'H'
        OUT
        RET
        .org 430
finish: LDI '\n'
        OUT
        HALT
