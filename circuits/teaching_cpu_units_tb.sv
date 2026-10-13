`timescale 1ns/1ps
module teaching_cpu_units_tb;
    reg [7:0] a = 0, b = 0;
    reg carry_in = 0;
    wire [7:0] sum;
    wire carry_out;
    teaching_adder8 adder(.a(a), .b(b), .carry_in(carry_in), .sum(sum), .carry_out(carry_out));
    reg [8:0] expected;
    reg clk = 0, reset = 1;
    reg acc_write = 0, carry_write = 0, carry_data = 0;
    reg x_load = 0, x_count = 0, x_count_down = 0;
    reg [7:0] acc_data = 0, x_data = 0;
    wire [7:0] acc, x, sp;
    wire zero, carry;
    reg [7:0] expected_x;
    integer ai, bi, ci, direction, previous_zero;
    teaching_registers registers(.clk(clk), .reset(reset),
        .acc_write(acc_write), .acc_data(acc_data),
        .carry_write(carry_write), .carry_data(carry_data),
        .x_load(x_load), .x_data(x_data), .x_count(x_count),
        .x_count_down(x_count_down), .sp_push(1'b0), .sp_pop(1'b0),
        .acc(acc), .x(x), .sp(sp), .zero(zero), .carry(carry));

    task tick;
        begin clk = 0; #1; clk = 1; #1; clk = 0; #1; end
    endtask

    initial begin
        for (ci = 0; ci < 2; ci = ci + 1)
            for (ai = 0; ai < 256; ai = ai + 1)
                for (bi = 0; bi < 256; bi = bi + 1) begin
                    a = ai; b = bi; carry_in = ci; expected = ai + bi + ci; #1;
                    if ({carry_out, sum} !== expected)
                        $fatal(1, "adder a=%02x b=%02x cin=%0d got=%03x expected=%03x",
                               ai, bi, ci, {carry_out, sum}, expected);
                end
        tick(); reset = 0;
        // Seed both flag values, then check every eight-bit count result.
        // In particular, INX FF->00 must test the truncated result for Z.
        for (direction = 0; direction < 2; direction = direction + 1)
            for (ci = 0; ci < 2; ci = ci + 1)
                for (previous_zero = 0; previous_zero < 2; previous_zero = previous_zero + 1)
                    for (ai = 0; ai < 256; ai = ai + 1) begin
                        x_load = 1; x_data = ai;
                        acc_write = 1; acc_data = previous_zero ? 8'h00 : 8'h5a;
                        carry_write = 1; carry_data = ci; x_count = 0;
                        tick();
                        x_load = 0; acc_write = 0; carry_write = 0;
                        x_count = 1; x_count_down = direction;
                        expected_x = direction ? ai - 1 : ai + 1;
                        tick();
                        if (x !== expected_x || zero !== (expected_x == 8'd0) ||
                            carry !== carry_data || acc !== acc_data || sp !== 8'd0)
                            $fatal(1, "X count down=%0d x=%02x previous_z=%0d cin=%0d got_x=%02x z=%0d c=%0d expected_x=%02x z=%0d",
                                   direction, ai, previous_zero, ci, x, zero, carry,
                                   expected_x, (expected_x == 8'd0));
                        x_count = 0;
                    end
        $display("TEACHING_UNITS_PASS adder_vectors=131072 x_count_vectors=2048");
        $finish;
    end
endmodule
