        LDI 1
loop:   OUT
        SHL
        JZ restart
        JMP loop
restart: LDI 1
        JMP loop
