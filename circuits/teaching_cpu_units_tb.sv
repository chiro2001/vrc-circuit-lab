`timescale 1ns/1ps
module teaching_cpu_units_tb;
    reg [7:0] a = 0, b = 0;
    reg [2:0] operation = 0;
    wire [7:0] result;
    wire alu_carry;
    teaching_alu alu(.a(a), .b(b), .operation(operation), .result(result), .carry(alu_carry));
    reg carry_in = 0;
    wire [7:0] sum;
    wire carry_out;
    teaching_adder8 adder(.a(a), .b(b), .carry_in(carry_in), .sum(sum), .carry_out(carry_out));
    reg clk = 0, reset = 1;
    always #5 clk = ~clk;
    reg [11:0] rom_data = 0;
    reg control_zero = 0;
    wire [11:0] instruction;
    wire phase, halt, acc_write, out_write, pc_enable, pc_jump;
    wire [2:0] alu_op;
    wire [3:0] pc_target;
    teaching_control control(.clk(clk), .reset(reset), .rom_data(rom_data), .zero(control_zero),
        .instruction(instruction), .phase(phase), .halt(halt), .alu_op(alu_op),
        .acc_write(acc_write), .out_write(out_write), .pc_enable(pc_enable),
        .pc_jump(pc_jump), .pc_target(pc_target));
    reg enable = 0, jump = 0;
    reg [3:0] target = 0;
    wire [3:0] value;
    teaching_pc pc(.clk(clk), .reset(reset), .enable(enable), .jump(jump), .target(target), .value(value));
    reg [3:0] expected_pc = 0;
    reg [8:0] expected_sum;
    reg [7:0] expected_result;
    integer ai, bi, op, ci, code, z, mode, n;

    task automatic edge_tick;
        @(posedge clk); #1;
    endtask

    initial begin
        // Every operand pair for all five ALU operations.
        for (op = 0; op < 5; op = op + 1)
            for (ai = 0; ai < 256; ai = ai + 1)
                for (bi = 0; bi < 256; bi = bi + 1) begin
                    a = ai; b = bi; operation = op;
                    expected_sum = ai + bi;
                    case (op)
                        0: expected_result = bi;
                        1: expected_result = ai + bi;
                        2: expected_result = ai & bi;
                        3: expected_result = ai | bi;
                        4: expected_result = ai ^ bi;
                    endcase
                    #1;
                    if (result !== expected_result || alu_carry !== ((op == 1) && expected_sum[8]))
                        $fatal(1, "ALU op=%0d a=%0d b=%0d", op, ai, bi);
                end
        // Include carry-in=1, exercising every full-adder truth table and carry chain.
        for (ci = 0; ci < 2; ci = ci + 1)
            for (ai = 0; ai < 256; ai = ai + 1)
                for (bi = 0; bi < 256; bi = bi + 1) begin
                    a = ai; b = bi; carry_in = ci; expected_sum = ai + bi + ci;
                    #1;
                    if ({carry_out, sum} !== expected_sum) $fatal(1, "Adder a=%0d b=%0d cin=%0d", ai, bi, ci);
                end
        for (op = 5; op < 8; op = op + 1) begin
            operation = op; a = 8'hff; b = 8'h37; #1;
            if (result !== b || alu_carry !== 0) $fatal(1, "ALU default operation");
        end

        // All sixteen opcodes, both zero flag values, FETCH/EXEC and reset/HALT behavior.
        for (code = 0; code < 16; code = code + 1)
            for (z = 0; z < 2; z = z + 1) begin
                @(negedge clk); reset = 1; rom_data = {code[3:0], 8'hac}; control_zero = z;
                edge_tick();
                if (instruction !== 0 || phase !== 0 || halt !== 0 || acc_write || out_write || pc_enable || pc_jump)
                    $fatal(1, "Control reset");
                @(negedge clk); reset = 0;
                edge_tick();
                if (instruction !== {code[3:0], 8'hac} || phase !== 1 || halt ||
                    acc_write !== (code <= 4) || out_write !== (code == 5) ||
                    pc_enable !== (code != 15) || pc_jump !== (code == 6 || (code == 7 && z)) ||
                    pc_target !== 4'hc || alu_op !== ((code >= 1 && code <= 4) ? code : 0))
                    $fatal(1, "Control decode opcode=%0d zero=%0d", code, z);
                @(negedge clk); rom_data = 12'h123;
                edge_tick();
                if (instruction !== {code[3:0], 8'hac} || halt !== (code == 15) || phase !== (code == 15) ||
                    acc_write || out_write || pc_enable || pc_jump)
                    $fatal(1, "Control execute/hold opcode=%0d", code);
                if (code == 15) begin
                    edge_tick();
                    if (!halt || instruction !== 12'hfac || !phase) $fatal(1, "HALT hold");
                end
            end

        // PC reset priority, enable hold, all jump targets and modulo-16 increments.
        for (mode = 0; mode < 8; mode = mode + 1)
            for (n = 0; n < 32; n = n + 1) begin
                @(negedge clk); reset = mode[2]; enable = mode[1]; jump = mode[0]; target = n;
                if (reset) expected_pc = 0;
                else if (enable) expected_pc = jump ? target : expected_pc + 1;
                edge_tick();
                if (value !== expected_pc) $fatal(1, "PC reset/enable/jump mode=%0d target=%0d", mode, n);
            end
        $display("TEACHING_UNITS_PASS alu_vectors=327680 adder_vectors=131072 control_vectors=32 pc_vectors=256");
        $finish;
    end
endmodule
