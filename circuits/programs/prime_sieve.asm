; Sieve of Eratosthenes for 2..100, flags in RAM bank1 offsets 0..100.
.equ PRIME, 0x80
.equ MULTIPLE, 0x81
        BANK 1
        LDI 2
        TAX
sieve_init:
        LDI 1
        STAX
        INX
        TXA
        ADDI -101
        JZ sieve_initialized
        JMP sieve_init
sieve_initialized:
        LDI 2
        ST PRIME
sieve_outer:
        LD PRIME
        TAX
        ADD PRIME
        ST MULTIPLE
sieve_inner:
        LD MULTIPLE
        TAX
        LDI 0
        STAX
        LD MULTIPLE
        ADD PRIME
        ST MULTIPLE
        LDI 100
        SUB MULTIPLE
        JC sieve_inner
        LD PRIME
        ADDI 1
        ST PRIME
        ADDI -11
        JZ sieve_done
        JMP sieve_outer
sieve_done:
        HALT
