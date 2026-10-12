; CPU-generated π using the Rabinowitz–Wagon integer spigot.
; Emits 3 plus 64 fractional digits and a newline.
; For each output pass, i descends: x=10*a[i-1]+q*i; a[i-1]=x%(2*i-1); q=x//(2*i-1).
; The CPU implements the product and quotient with bounded 8/16-bit add/subtract loops.
; a[i] starts at 2; no π digit is stored in ROM or initial RAM.
; N=216; two bytes per array element across RAM banks 1..3.
.equ Q, 0x20
.equ J, 0x21
.equ I, 0x22
.equ IH, 0x23
.equ NINES, 0x24
.equ PRED, 0x25
.equ AL, 0x26
.equ AH, 0x27
.equ XL, 0x28
.equ XH, 0x29
.equ ML, 0x2A
.equ MH, 0x2B
.equ DL, 0x2C
.equ DH, 0x2D
.equ CL, 0x2E
.equ QT, 0x2F
.equ DIGIT, 0x30
.equ PTR, 0x31
.equ ZERO, 0x32
        BANK 0
        LDI 0
        ST Q
        ST NINES
        ST PRED
        ST ZERO
        ST J
        ADDI 1
        ST J
outer:
        LDI 0
        ST Q
        LDI 216
        ST I
        LDI 0
        ST IH
        BANK 2
        LDI 174
        TAX
        JMP inner2
inner2:
        BANK 2
        LDAX
        BANK 0
        ST AL
        BANK 2
        INX
        LDAX
        BANK 0
        ST AH
        TXA
        ADDI -1
        ST PTR
        CALL process_cell
        BANK 0
        LD PTR
        TAX
        LD XL
        BANK 2
        STAX
        INX
        BANK 0
        LD XH
        BANK 2
        STAX
        DEX
        DEX
        DEX
        BANK 0
        LD I
        ADDI -1
        ST I
        JC index_no_borrow2
        LD IH
        ADDI -1
        ST IH
index_no_borrow2:
        LD I
        ADDI -128
        JZ switch_to_bank1
        JMP inner2
switch_to_bank1:
        BANK 1
        LDI 254
        TAX
        JMP inner1
inner1:
        BANK 1
        LDAX
        BANK 0
        ST AL
        BANK 1
        INX
        LDAX
        BANK 0
        ST AH
        TXA
        ADDI -1
        ST PTR
        CALL process_cell
        BANK 0
        LD PTR
        TAX
        LD XL
        BANK 1
        STAX
        INX
        BANK 0
        LD XH
        BANK 1
        STAX
        DEX
        DEX
        DEX
        BANK 0
        LD I
        ADDI -1
        ST I
        JC index_no_borrow1
        LD IH
        ADDI -1
        ST IH
index_no_borrow1:
        LD I
        JZ inner_done
        JMP inner1
inner_done:
        BANK 0
        LD Q
        ST QT
        LDI 0
        ST DIGIT
divide_q_by_ten:
        LD QT
        ADDI -10
        JNC quotient_ready
        ST QT
        LD DIGIT
        ADDI 1
        ST DIGIT
        JMP9 divide_q_by_ten
quotient_ready:
        BANK 1
        LDI 0
        TAX
        BANK 0
        LD QT
        BANK 1
        STAX
        INX
        BANK 0
        LDI 0
        BANK 1
        STAX
        BANK 0
        LD DIGIT
        ST Q
        LD Q
        ADDI -9
        JZ quotient_nine
        LD Q
        ADDI -10
        JZ quotient_ten
quotient_regular:
        LD J
        ADDI -1
        JZ first_digit
        LD PRED
        ADDI '0'
        OUT
        LD J
        ADDI -2
        JZ decimal_point
        JMP after_decimal_point
decimal_point:
        LDI '.'
        OUT
after_decimal_point:
first_digit:
        LD Q
        ST PRED
flush_nines:
        LD NINES
        JZ digit_complete
        LDI '9'
        OUT
        LD NINES
        ADDI -1
        ST NINES
        JMP9 flush_nines
quotient_nine:
        LD NINES
        ADDI 1
        ST NINES
        JMP digit_complete
quotient_ten:
        LD PRED
        ADDI '1'
        OUT
flush_zeros:
        LD NINES
        JZ ten_complete
        LDI '0'
        OUT
        LD NINES
        ADDI -1
        ST NINES
        JMP9 flush_zeros
ten_complete:
        LDI 0
        ST PRED
        ST NINES
digit_complete:
        LD J
        ADDI -65
        JZ all_digits_done
        LD J
        ADDI 1
        ST J
        JMP outer
all_digits_done:
        LD PRED
        ADDI '0'
        OUT
flush_final_nines:
        LD NINES
        JZ final_newline
        LDI '9'
        OUT
        LD NINES
        ADDI -1
        ST NINES
        JMP9 flush_final_nines
final_newline:
        LDI '\n'
        OUT
        HALT
process_cell:
        LD I
        SHL
        ST DL
        LD IH
        ADC IH
        ST DH
        LD DL
        ADDI -1
        ST DL
        LD DH
        SBC ZERO
        ST DH
        LD AL
        ST ML
        LD AH
        ST MH
        LD ML
        SHL
        ST ML
        LD MH
        ADC MH
        ST MH
        LD ML
        SHL
        ST ML
        LD MH
        ADC MH
        ST MH
        LD ML
        SHL
        ST ML
        LD MH
        ADC MH
        ST MH
        LD ML
        ST XL
        LD MH
        ST XH
        LD AL
        ST ML
        LD AH
        ST MH
        LD ML
        SHL
        ST ML
        LD MH
        ADC MH
        ST MH
        LD XL
        ADD ML
        ST XL
        LD XH
        ADC MH
        ST XH
        LDI 0
        ST ML
        ST MH
        LD Q
        TAX
multiply_loop:
        TXA
        JZ9 multiply_done
        LD ML
        ADD I
        ST ML
        LD MH
        ADC IH
        ST MH
        DEX
        JZ9 multiply_done
        JMP9 multiply_loop
multiply_done:
        LD XL
        ADD ML
        ST XL
        LD XH
        ADC MH
        ST XH
        LDI 0
        ST Q
divide_loop:
        LD XL
        SUB DL
        ST CL
        LD XH
        SBC DH
        JNC divide_done
        ST XH
        LD CL
        ST XL
        LD Q
        ADDI 1
        ST Q
        JMP9 divide_loop
divide_done:
        RET
