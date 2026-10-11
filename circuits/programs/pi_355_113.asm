; Compute 355 / 113 using byte-wide repeated subtraction, then generate six
; decimal places by long division. 355 starts as high=1, low=99; neither the
; quotient nor the first remainder (16) is preloaded as a result.
        LDI 113
        ST D
        LDI 1
        ST HI
        LDI 99
        ST LO
        LDI 0
        ST Q

integer_loop:
        LD HI
        JZ integer_low
        LD LO
        SUB D
        ST LO
        JNC integer_borrow
        JMP integer_quotient
integer_borrow:
        LD HI
        ADDI -1
        ST HI
integer_quotient:
        LD Q
        ADDI 1
        ST Q
        JMP integer_loop

integer_low:
        LD LO
        SUB D
        JC integer_subtract
        ADD D
        ST LO
        JMP integer_ready
integer_subtract:
        ST LO
        LD Q
        ADDI 1
        ST Q
        JMP integer_loop

integer_ready:
        LD Q
        ADDI '0'
        OUT
        LDI '.'
        OUT
        LD LO
        ST R
        LDI 6
        ST COUNT

digit:
        LDI 0
        ST RUN
        ST DIGIT
        LDI 10
        ST ITER
step:
        LD RUN
        ADD R
        ST RUN
        SUB D
        JC subtract_divisor
        ADD D
        ST RUN
        JMP next_step
subtract_divisor:
        ST RUN
        LD DIGIT
        ADDI 1
        ST DIGIT
next_step:
        LD ITER
        ADDI -1
        ST ITER
        JZ step_done
        JMP step
step_done:
        LD RUN
        ST R
        LD DIGIT
        ADDI '0'
        OUT
        LD COUNT
        ADDI -1
        ST COUNT
        JZ finished
        JMP digit
finished:
        LDI '\n'
        OUT
        HALT

.equ D, 0
.equ R, 1
.equ RUN, 2
.equ DIGIT, 3
.equ COUNT, 4
.equ ITER, 5
.equ HI, 6
.equ LO, 7
.equ Q, 8
