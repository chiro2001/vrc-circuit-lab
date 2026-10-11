`timescale 1ns/1ps
module teaching_cpu_units_tb;
    reg [7:0] a = 0, b = 0;
    reg carry_in = 0;
    wire [7:0] sum;
    wire carry_out;
    teaching_adder8 adder(.a(a), .b(b), .carry_in(carry_in), .sum(sum), .carry_out(carry_out));
    reg [8:0] expected;
    integer ai, bi, ci;
    initial begin
        for (ci = 0; ci < 2; ci = ci + 1)
            for (ai = 0; ai < 256; ai = ai + 1)
                for (bi = 0; bi < 256; bi = bi + 1) begin
                    a = ai; b = bi; carry_in = ci; expected = ai + bi + ci; #1;
                    if ({carry_out, sum} !== expected)
                        $fatal(1, "adder a=%02x b=%02x cin=%0d got=%03x expected=%03x",
                               ai, bi, ci, {carry_out, sum}, expected);
                end
        $display("TEACHING_UNITS_PASS adder_vectors=131072");
        $finish;
    end
endmodule
