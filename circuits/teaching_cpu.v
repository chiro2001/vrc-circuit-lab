// A deliberately small, hierarchical teaching CPU. All state uses clk's
// rising edge; reset is synchronous and wins over every enable.
// Instructions are opcode[11:8], immediate[7:0].
module teaching_cpu (
    input wire clk, reset,
    input wire [1:0] program_select,
    output wire [3:0] pc,
    output wire [11:0] instruction,
    output wire phase,
    output wire [7:0] acc, out,
    output wire zero, carry, halt,
    output wire [7:0] terminal_data,
    output wire terminal_write,
    output wire [7:0] terminal_sequence
);
    wire [11:0] rom_data;
    wire [7:0] result;
    wire result_carry;
    wire [2:0] alu_op;
    wire acc_write, out_write, pc_enable, pc_jump;
    wire [3:0] pc_target;

    teaching_rom rom(.address(pc), .program_select(program_select), .data(rom_data));
    teaching_control control(.clk(clk), .reset(reset), .rom_data(rom_data), .zero(zero),
        .instruction(instruction), .phase(phase), .halt(halt), .alu_op(alu_op),
        .acc_write(acc_write), .out_write(out_write), .pc_enable(pc_enable),
        .pc_jump(pc_jump), .pc_target(pc_target));
    teaching_pc pc_unit(.clk(clk), .reset(reset), .enable(pc_enable),
        .jump(pc_jump), .target(pc_target), .value(pc));
    teaching_registers registers(.clk(clk), .reset(reset), .write_enable(acc_write),
        .data(result), .data_carry(result_carry), .acc(acc), .zero(zero), .carry(carry));
    teaching_alu alu(.a(acc), .b(instruction[7:0]), .operation(alu_op),
        .result(result), .carry(result_carry));
    teaching_output output_unit(.clk(clk), .reset(reset), .write_enable(out_write),
        .data(acc), .value(out), .write_strobe(terminal_write), .sequence_id(terminal_sequence));
    assign terminal_data = out;
endmodule

// The two ROMs are combinational case logic, not a memory primitive.
module teaching_rom(input wire [3:0] address, input wire [1:0] program_select,
                    output reg [11:0] data);
    always @* begin
        data = 12'hf00;
        if (program_select == 0) begin
            case (address)
                4'h0: data = 12'h000; // LDI 0
                4'h1: data = 12'h500; // OUT
                4'h2: data = 12'h101; // ADDI 1
                4'h3: data = 12'h500; // OUT
                4'h4: data = 12'h602; // JMP 2
                default: data = 12'hf00;
            endcase
        end else if (program_select == 1) begin
            case (address)
                4'h0: data = 12'h0a5; // LDI A5
                4'h1: data = 12'h20f; // ANDI 0F => 05
                4'h2: data = 12'h380; // ORI 80 => 85
                4'h3: data = 12'h485; // XORI 85 => 00, Z=1
                4'h4: data = 12'h706; // JZ 6 (taken)
                4'h5: data = 12'h0ff; // skipped
                4'h6: data = 12'h33c; // ORI 3C => 3C
                4'h7: data = 12'h500; // OUT => 3C
                4'h8: data = 12'h1c4; // ADDI C4 => 00, C=1
                4'h9: data = 12'h70b; // JZ 11 (taken)
                4'ha: data = 12'h0ee; // skipped
                4'hb: data = 12'h500; // OUT => 00
                4'hc: data = 12'h055; // LDI 55
                4'hd: data = 12'h4ff; // XORI FF => AA
                4'he: data = 12'h500; // OUT => AA
                4'hf: data = 12'hf00; // HALT
            endcase
        end else if (program_select == 2) begin
            case (address)
                4'h0: data = 12'h048; // LDI 'H'
                4'h1: data = 12'h500; // OUT
                4'h2: data = 12'h045; // LDI 'E'
                4'h3: data = 12'h500;
                4'h4: data = 12'h04c; // LDI 'L'
                4'h5: data = 12'h500;
                4'h6: data = 12'h500; // second 'L': a separate real write
                4'h7: data = 12'h04f; // LDI 'O'
                4'h8: data = 12'h500;
                4'h9: data = 12'h00a; // LDI newline
                4'ha: data = 12'h500;
                default: data = 12'hf00;
            endcase
        end
    end
endmodule

module teaching_control (
    input wire clk, reset, input wire [11:0] rom_data, input wire zero,
    output reg [11:0] instruction, output reg phase, halt,
    output reg [2:0] alu_op,
    output wire acc_write, out_write, pc_enable, pc_jump,
    output wire [3:0] pc_target
);
    wire [3:0] opcode = instruction[11:8];
    wire execute = phase && !halt;
    assign acc_write = execute && (opcode <= 4'h4);
    assign out_write = execute && (opcode == 4'h5);
    assign pc_enable = execute && (opcode != 4'hf);
    assign pc_jump = execute && ((opcode == 4'h6) || ((opcode == 4'h7) && zero));
    assign pc_target = instruction[3:0];
    always @* begin
        case (opcode)
            4'h1: alu_op = 3'd1;
            4'h2: alu_op = 3'd2;
            4'h3: alu_op = 3'd3;
            4'h4: alu_op = 3'd4;
            default: alu_op = 3'd0;
        endcase
    end
    always @(posedge clk) begin
        if (reset) begin
            instruction <= 0;
            phase <= 0;
            halt <= 0;
        end else if (!halt) begin
            if (!phase) begin
                instruction <= rom_data;
                phase <= 1;
            end else if (opcode == 4'hf) begin
                halt <= 1;
            end else begin
                phase <= 0;
            end
        end
    end
endmodule

module teaching_pc(input wire clk, reset, enable, jump,
                   input wire [3:0] target, output reg [3:0] value);
    always @(posedge clk) begin
        if (reset) value <= 0;
        else if (enable) value <= jump ? target : value + 4'd1;
    end
endmodule

module teaching_registers(input wire clk, reset, write_enable,
    input wire [7:0] data, input wire data_carry,
    output reg [7:0] acc, output reg zero, carry);
    always @(posedge clk) begin
        if (reset) begin
            acc <= 0;
            zero <= 1;
            carry <= 0;
        end else if (write_enable) begin
            acc <= data;
            zero <= (data == 0);
            carry <= data_carry;
        end
    end
endmodule

module teaching_output(input wire clk, reset, write_enable,
    input wire [7:0] data, output reg [7:0] value,
    output reg write_strobe, output reg [7:0] sequence_id);
    always @(posedge clk) begin
        if (reset) begin
            value <= 0;
            write_strobe <= 0;
            sequence_id <= 0;
        end else begin
            write_strobe <= write_enable;
            if (write_enable) begin
                value <= data;
                sequence_id <= sequence_id + 8'd1;
            end
        end
    end
endmodule

module teaching_alu(input wire [7:0] a, b, input wire [2:0] operation,
                    output reg [7:0] result, output wire carry);
    wire [7:0] sum, and_result, or_result, xor_result;
    wire sum_carry;
    teaching_adder8 adder(.a(a), .b(b), .carry_in(1'b0), .sum(sum), .carry_out(sum_carry));
    teaching_and8 bit_and(.a(a), .b(b), .result(and_result));
    teaching_or8 bit_or(.a(a), .b(b), .result(or_result));
    teaching_xor8 bit_xor(.a(a), .b(b), .result(xor_result));
    assign carry = (operation == 3'd1) && sum_carry;
    always @* begin
        case (operation)
            3'd1: result = sum;
            3'd2: result = and_result;
            3'd3: result = or_result;
            3'd4: result = xor_result;
            default: result = b;
        endcase
    end
endmodule

module teaching_and8(input wire [7:0] a, b, output wire [7:0] result);
    assign result = a & b;
endmodule
module teaching_or8(input wire [7:0] a, b, output wire [7:0] result);
    assign result = a | b;
endmodule
module teaching_xor8(input wire [7:0] a, b, output wire [7:0] result);
    assign result = a ^ b;
endmodule

module teaching_adder8(input wire [7:0] a, b, input wire carry_in,
    output wire [7:0] sum, output wire carry_out);
    wire [8:0] carry;
    assign carry[0] = carry_in;
    assign carry_out = carry[8];
    teaching_full_adder fa0(a[0], b[0], carry[0], sum[0], carry[1]);
    teaching_full_adder fa1(a[1], b[1], carry[1], sum[1], carry[2]);
    teaching_full_adder fa2(a[2], b[2], carry[2], sum[2], carry[3]);
    teaching_full_adder fa3(a[3], b[3], carry[3], sum[3], carry[4]);
    teaching_full_adder fa4(a[4], b[4], carry[4], sum[4], carry[5]);
    teaching_full_adder fa5(a[5], b[5], carry[5], sum[5], carry[6]);
    teaching_full_adder fa6(a[6], b[6], carry[6], sum[6], carry[7]);
    teaching_full_adder fa7(a[7], b[7], carry[7], sum[7], carry[8]);
endmodule
module teaching_full_adder(input wire a, b, carry_in, output wire sum, carry_out);
    wire propagate = a ^ b;
    assign sum = propagate ^ carry_in;
    assign carry_out = (a & b) | (propagate & carry_in);
endmodule
