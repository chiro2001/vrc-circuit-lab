// Eight-bit accumulator CPU. Memory is kept as two explicit primitive
// boundaries; the rest of the datapath maps to simple gates and DFFs.
module teaching_cpu (
    input wire clk, reset,
    output wire [8:0] pc,
    output wire [11:0] instruction,
    output wire [2:0] phase,
    output wire [7:0] acc, x, sp, out,
    output wire [1:0] bank,
    output wire zero, carry, halt,
    output wire [7:0] terminal_data,
    output wire terminal_write,
    output wire [7:0] terminal_sequence
);
    wire [11:0] rom_data;
    wire [7:0] ram_data;
    wire [9:0] ram_read_address, ram_write_address;
    wire [7:0] ram_write_data;
    wire ram_write_enable, ram_write_enable_raw;
    wire pc_enable, pc_jump;
    wire [8:0] pc_target;
    wire call_push_low, call_push_high, return_read_high, return_read_low;
    wire sp_push, sp_pop;
    wire execute = (phase == 3'd1) && !halt;
    wire [3:0] opcode = instruction[11:8];
    wire [3:0] ext = instruction[7:4];
    wire [7:0] alu_b;
    wire [3:0] alu_operation;
    wire alu_carry_in;
    wire [7:0] alu_result;
    wire alu_carry_out;
    wire acc_write, carry_write, x_load, x_count, x_count_down, bank_write, out_write;
    wire [7:0] acc_write_data;

    teaching_rom rom(.address(pc), .data(rom_data));
    teaching_ram ram(.clk(clk), .read_address(ram_read_address), .read_data(ram_data),
        .write_enable(ram_write_enable), .write_address(ram_write_address),
        .write_data(ram_write_data));

    teaching_control control(.clk(clk), .reset(reset), .rom_data(rom_data),
        .zero(zero), .carry(carry), .ram_data(ram_data), .instruction(instruction),
        .phase(phase), .halt(halt), .pc_enable(pc_enable), .pc_jump(pc_jump),
        .pc_target(pc_target), .call_push_low(call_push_low),
        .call_push_high(call_push_high), .return_read_high(return_read_high),
        .return_read_low(return_read_low));
    teaching_pc pc_unit(.clk(clk), .reset(reset), .enable(pc_enable),
        .jump(pc_jump), .target(pc_target), .value(pc));

    assign sp_push = call_push_high;
    assign sp_pop = return_read_low;

    assign alu_b = (opcode == 4'h8 || (opcode >= 4'ha && opcode <= 4'hd) ||
                    (opcode == 4'he && ext == 4'h4)) ? ram_data :
                   (opcode == 4'he && ext == 4'h3) ? x :
                   (opcode == 4'he && (ext == 4'h8 || ext == 4'h9)) ? x :
                   instruction[7:0];
    assign alu_carry_in = (opcode == 4'hc || opcode == 4'hd) ? carry :
                          ((opcode == 4'hb || opcode == 4'h9 ||
                            (opcode == 4'he && ext == 4'h9)) ? 1'b1 : 1'b0);
    assign alu_operation =
        (opcode == 4'h1 || opcode == 4'ha || opcode == 4'hc ||
         (opcode == 4'he && ext == 4'h8)) ? 4'd1 :
        opcode == 4'h2 ? 4'd2 : opcode == 4'h3 ? 4'd3 : opcode == 4'h4 ? 4'd4 :
        (opcode == 4'hb || opcode == 4'hd ||
         (opcode == 4'he && ext == 4'h9)) ? 4'd5 :
        (opcode == 4'he && ext == 4'h0) ? 4'd6 :
        (opcode == 4'he && ext == 4'h1) ? 4'd7 : 4'd0;

    teaching_alu alu(.a(acc), .b(alu_b), .operation(alu_operation),
        .carry_in(alu_carry_in), .result(alu_result), .carry(alu_carry_out));

    assign acc_write = execute && ((opcode <= 4'h4) || opcode == 4'h8 ||
        (opcode >= 4'ha && opcode <= 4'hd) ||
        (opcode == 4'he && (ext == 4'h0 || ext == 4'h1 || ext == 4'h3 ||
                            ext == 4'h4 || ext == 4'h8 || ext == 4'h9)));
    assign carry_write = acc_write && opcode != 4'h8 &&
                         !(opcode == 4'he && (ext == 4'h3 || ext == 4'h4));
    assign acc_write_data = alu_result;
    // E20 remains TAX. E21..E24 select BANK0..BANK3; other E2x values are
    // reserved and behave as no-ops.
    assign x_load = execute && instruction == 12'he20;
    assign bank_write = execute && opcode == 4'he && ext == 4'h2 &&
                        instruction[3:0] >= 1 && instruction[3:0] <= 4;
    assign x_count = execute && opcode == 4'he && (ext == 4'h6 || ext == 4'h7);
    assign x_count_down = ext == 4'h7;
    assign out_write = execute && opcode == 4'h5;

    teaching_registers registers(.clk(clk), .reset(reset), .acc_write(acc_write),
        .acc_data(acc_write_data), .carry_write(carry_write), .carry_data(alu_carry_out),
        .x_load(x_load), .x_data(acc), .x_count(x_count), .x_count_down(x_count_down),
        .sp_push(sp_push), .sp_pop(sp_pop), .acc(acc), .x(x), .sp(sp),
        .zero(zero), .carry(carry));
    teaching_output output_unit(.clk(clk), .reset(reset), .write_enable(out_write),
        .data(acc), .value(out), .write_strobe(terminal_write),
        .sequence_id(terminal_sequence));
    assign terminal_data = out;

    reg [1:0] bank_r;
    assign bank = bank_r;
    always @(posedge clk) begin
        if (reset) bank_r <= 2'b00;
        else if (bank_write) bank_r <= instruction[1:0] - 2'b01;
    end

    reg [9:0] ram_read_address_r, ram_write_address_r;
    reg [7:0] ram_write_data_r;
    reg ram_write_enable_raw_r;
    assign ram_read_address = ram_read_address_r;
    assign ram_write_address = ram_write_address_r;
    assign ram_write_data = ram_write_data_r;
    assign ram_write_enable_raw = ram_write_enable_raw_r;
    assign ram_write_enable = ram_write_enable_raw_r && !reset;

    always @* begin
        ram_read_address_r = {bank_r, instruction[7:0]};
        if (opcode == 4'he && (ext == 4'h4 || ext == 4'h5))
            ram_read_address_r = {bank_r, x};
        if (return_read_high)
            ram_read_address_r = {2'b00, sp - 8'd1};
        else if (return_read_low)
            ram_read_address_r = {2'b00, sp - 8'd2};

        ram_write_enable_raw_r = 1'b0;
        ram_write_address_r = {bank_r, instruction[7:0]};
        ram_write_data_r = acc;
        if (execute && opcode == 4'h9) begin
            ram_write_enable_raw_r = 1'b1;
            ram_write_address_r = {bank_r, instruction[7:0]};
        end else if (execute && opcode == 4'he && ext == 4'h5) begin
            ram_write_enable_raw_r = 1'b1;
            ram_write_address_r = {bank_r, x};
        end else if (call_push_low) begin
            ram_write_enable_raw_r = 1'b1;
            ram_write_address_r = {2'b00, sp};
            ram_write_data_r = pc + 9'd1;
        end else if (call_push_high) begin
            ram_write_enable_raw_r = 1'b1;
            ram_write_address_r = {2'b00, sp + 8'd1};
            ram_write_data_r = {7'b0, (pc + 9'd1) >> 8};
        end
    end
endmodule

module teaching_control(input wire clk, reset, input wire [11:0] rom_data,
    input wire zero, carry, input wire [7:0] ram_data,
    output reg [11:0] instruction, output reg [2:0] phase, output reg halt,
    output wire pc_enable, pc_jump, output reg [8:0] pc_target,
    output wire call_push_low, call_push_high, return_read_high, return_read_low);
    localparam [2:0] FETCH = 3'd0, EXEC = 3'd1, WORD_FETCH = 3'd2,
                     WORD_EXEC = 3'd3, CALL_HIGH = 3'd4,
                     RETURN_HIGH = 3'd5, RETURN_LOW = 3'd6;
    wire [3:0] opcode = instruction[11:8];
    wire [3:0] ext = instruction[7:4];
    wire long_instruction = opcode == 4'he &&
        (ext == 4'ha || ext == 4'hb || ext == 4'hc || ext == 4'hd || ext == 4'he);
    reg [8:0] long_target;
    reg return_high;
    wire direct_taken = opcode == 4'h6 || (opcode == 4'h7 && zero);
    wire long_taken = (ext == 4'ha && carry) || (ext == 4'hb && !carry) ||
                      ext == 4'hc || (ext == 4'hd && zero);

    assign pc_enable = !reset && !halt &&
        ((phase == EXEC && opcode != 4'hf && !(opcode == 4'he && ext == 4'hf)) ||
         (phase == WORD_EXEC && !(opcode == 4'he && ext == 4'he)) ||
         phase == CALL_HIGH || phase == RETURN_LOW);
    assign pc_jump = (phase == EXEC && direct_taken) ||
                     (phase == WORD_EXEC && long_taken) ||
                     phase == CALL_HIGH || phase == RETURN_LOW;
    always @* begin
        pc_target = {1'b0, instruction[7:0]};
        if (phase == WORD_EXEC || phase == CALL_HIGH)
            pc_target = long_target;
        else if (phase == RETURN_LOW)
            pc_target = {return_high, ram_data};
    end
    assign call_push_low = !reset && !halt && phase == WORD_EXEC && opcode == 4'he && ext == 4'he;
    assign call_push_high = !reset && !halt && phase == CALL_HIGH;
    assign return_read_high = phase == RETURN_HIGH;
    assign return_read_low = phase == RETURN_LOW;

    always @(posedge clk) begin
        if (reset) begin
            instruction <= 0;
            phase <= FETCH;
            halt <= 0;
            long_target <= 0;
            return_high <= 0;
        end else if (!halt) begin
            case (phase)
                FETCH: begin instruction <= rom_data; phase <= EXEC; end
                EXEC: begin
                    if (opcode == 4'hf) halt <= 1'b1;
                    else if (long_instruction) phase <= WORD_FETCH;
                    else if (opcode == 4'he && ext == 4'hf) phase <= RETURN_HIGH;
                    else phase <= FETCH;
                end
                WORD_FETCH: begin long_target <= rom_data[8:0]; phase <= WORD_EXEC; end
                WORD_EXEC: begin
                    if (opcode == 4'he && ext == 4'he) phase <= CALL_HIGH;
                    else phase <= FETCH;
                end
                CALL_HIGH: phase <= FETCH;
                RETURN_HIGH: begin return_high <= ram_data[0]; phase <= RETURN_LOW; end
                RETURN_LOW: phase <= FETCH;
                default: phase <= FETCH;
            endcase
        end
    end
endmodule

module teaching_pc(input wire clk, reset, enable, jump,
    input wire [8:0] target, output reg [8:0] value);
    always @(posedge clk) begin
        if (reset) value <= 0;
        else if (enable) value <= jump ? target : value + 9'd1;
    end
endmodule

module teaching_registers(input wire clk, reset,
    input wire acc_write, input wire [7:0] acc_data,
    input wire carry_write, carry_data,
    input wire x_load, input wire [7:0] x_data,
    input wire x_count, x_count_down, sp_push, sp_pop,
    output reg [7:0] acc, x, sp, output reg zero, carry);
    always @(posedge clk) begin
        if (reset) begin
            acc <= 0;
            x <= 0;
            sp <= 0;
            zero <= 1;
            carry <= 0;
        end else begin
            if (acc_write) begin
                acc <= acc_data;
                zero <= acc_data == 0;
                if (carry_write) carry <= carry_data;
            end
            if (x_load) x <= x_data;
            if (x_count) begin
                x <= x_count_down ? x - 8'd1 : x + 8'd1;
                zero <= x_count_down ? (x - 8'd1) == 0 : (x + 8'd1) == 0;
            end
            if (sp_push) sp <= sp + 8'd2;
            if (sp_pop) sp <= sp - 8'd2;
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

module teaching_alu(input wire [7:0] a, b, input wire [3:0] operation,
    input wire carry_in, output reg [7:0] result, output reg carry);
    wire subtract = operation == 4'd5;
    wire [7:0] adder_b = b ^ {8{subtract}};
    wire [7:0] sum;
    wire adder_carry;
    teaching_adder8 adder(.a(a), .b(adder_b), .carry_in(carry_in),
        .sum(sum), .carry_out(adder_carry));
    wire [7:0] and_result, or_result, xor_result;
    teaching_and8 bit_and(.a(a), .b(b), .result(and_result));
    teaching_or8 bit_or(.a(a), .b(b), .result(or_result));
    teaching_xor8 bit_xor(.a(a), .b(b), .result(xor_result));
    always @* begin
        result = b;
        carry = 0;
        case (operation)
            4'd1: begin result = sum; carry = adder_carry; end
            4'd2: result = and_result;
            4'd3: result = or_result;
            4'd4: result = xor_result;
            4'd5: begin result = sum; carry = adder_carry; end
            4'd6: begin result = {a[6:0], 1'b0}; carry = a[7]; end
            4'd7: begin result = {1'b0, a[7:1]}; carry = a[0]; end
            default: begin end
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

`ifdef SYNTHESIS
(* blackbox *) module teaching_rom(input wire [8:0] address, output wire [11:0] data);
endmodule
(* blackbox *) module teaching_ram(input wire clk, input wire [9:0] read_address,
    output wire [7:0] read_data, input wire write_enable, input wire [9:0] write_address,
    input wire [7:0] write_data);
endmodule
`endif
