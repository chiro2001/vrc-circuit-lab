`timescale 1ns/1ps
module teaching_cpu_tb;
    reg clk = 0, reset = 1;
    reg [1:0] program_select = 0;
    wire [3:0] pc;
    wire [11:0] instruction;
    wire phase;
    wire [7:0] acc, out;
    wire zero, carry, halt;
    wire [7:0] terminal_data, terminal_sequence;
    wire terminal_write;
    teaching_cpu dut(.*);
    always #5 clk = ~clk;

    reg [3:0] model_pc = 0;
    reg [11:0] model_instruction = 0;
    reg model_phase = 0, model_zero = 1, model_carry = 0, model_halt = 0;
    reg [7:0] model_acc = 0, model_out = 0;
    reg model_write = 0;
    reg [7:0] model_sequence = 0;
    reg [8:0] arithmetic;
    reg [7:0] result;
    reg [31:0] random_state = 32'h1432abcd;
    integer cycle = 0, trace_file, waveform_file, i, count_wraps = 0, count_cycles = 1800;
    string trace_path, waveform_path, segment = "count";
    string terminal_capture = "";

    // Independent architectural ROM and model; no DUT internal signals are used.
    function automatic [11:0] model_rom(input reg [1:0] program_id, input reg [3:0] address);
        if (program_id == 0) begin
            case (address)
                0: model_rom = {4'd0, 8'd0};
                1: model_rom = {4'd5, 8'd0};
                2: model_rom = {4'd1, 8'd1};
                3: model_rom = {4'd5, 8'd0};
                4: model_rom = {4'd6, 8'd2};
                default: model_rom = {4'd15, 8'd0};
            endcase
        end else if (program_id == 1) begin
            case (address)
                0: model_rom = {4'd0, 8'ha5};
                1: model_rom = {4'd2, 8'h0f};
                2: model_rom = {4'd3, 8'h80};
                3: model_rom = {4'd4, 8'h85};
                4: model_rom = {4'd7, 8'd6};
                5: model_rom = {4'd0, 8'hff};
                6: model_rom = {4'd3, 8'h3c};
                7: model_rom = {4'd5, 8'd0};
                8: model_rom = {4'd1, 8'hc4};
                9: model_rom = {4'd7, 8'd11};
                10: model_rom = {4'd0, 8'hee};
                11: model_rom = {4'd5, 8'd0};
                12: model_rom = {4'd0, 8'h55};
                13: model_rom = {4'd4, 8'hff};
                14: model_rom = {4'd5, 8'd0};
                default: model_rom = {4'd15, 8'd0};
            endcase
        end else if (program_id == 2) begin
            case (address)
                0: model_rom = {4'd0, 8'd72};
                1, 3, 5, 6, 8, 10: model_rom = {4'd5, 8'd0};
                2: model_rom = {4'd0, 8'd69};
                4: model_rom = {4'd0, 8'd76};
                7: model_rom = {4'd0, 8'd79};
                9: model_rom = {4'd0, 8'd10};
                default: model_rom = {4'd15, 8'd0};
            endcase
        end else begin
            model_rom = {4'd15, 8'd0};
        end
    endfunction

    task automatic tick(input bit r, input reg [1:0] p);
        @(negedge clk);
        reset = r; program_select = p;
        #1;
        $fdisplay(waveform_file, "%0d,0,0,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d", cycle + 1,
            pc, instruction, phase, acc, out, zero, carry, halt, terminal_data, terminal_write, terminal_sequence);
        model_write = 0;
        if (r) begin
            model_pc = 0; model_instruction = 0; model_phase = 0;
            model_acc = 0; model_out = 0; model_zero = 1;
            model_carry = 0; model_halt = 0;
            model_sequence = 0;
        end else if (!model_halt) begin
            if (!model_phase) begin
                model_instruction = model_rom(p, model_pc);
                model_phase = 1;
            end else begin
                case (model_instruction[11:8])
                    0, 1, 2, 3, 4: begin
                        arithmetic = {1'b0, model_acc} + {1'b0, model_instruction[7:0]};
                        case (model_instruction[11:8])
                            0: result = model_instruction[7:0];
                            1: result = arithmetic[7:0];
                            2: result = model_acc & model_instruction[7:0];
                            3: result = model_acc | model_instruction[7:0];
                            4: result = model_acc ^ model_instruction[7:0];
                        endcase
                        if (segment == "count" && model_acc == 255 && result == 0)
                            count_wraps = count_wraps + 1;
                        model_acc = result;
                        model_zero = (result == 0);
                        model_carry = (model_instruction[11:8] == 1) && arithmetic[8];
                    end
                    5: begin
                        model_out = model_acc;
                        model_write = 1;
                        model_sequence = model_sequence + 1;
                    end
                    15: model_halt = 1;
                    default: begin end
                endcase
                if (!model_halt) begin
                    if (model_instruction[11:8] == 6 ||
                        (model_instruction[11:8] == 7 && model_zero))
                        model_pc = model_instruction[3:0];
                    else model_pc = model_pc + 1;
                    model_phase = 0;
                end
            end
        end
        @(posedge clk); #1;
        cycle = cycle + 1;
        if (pc !== model_pc || instruction !== model_instruction || phase !== model_phase ||
            acc !== model_acc || out !== model_out || zero !== model_zero ||
            carry !== model_carry || halt !== model_halt || terminal_data !== model_out ||
            terminal_write !== model_write || terminal_sequence !== model_sequence)
            $fatal(1, "cycle=%0d segment=%s pc=%h/%h ir=%h/%h phase=%b/%b acc=%h/%h out=%h/%h z=%b/%b c=%b/%b h=%b/%b",
                cycle, segment, pc, model_pc, instruction, model_instruction, phase, model_phase,
                acc, model_acc, out, model_out, zero, model_zero, carry, model_carry, halt, model_halt);
        if (segment == "text" && terminal_write)
            terminal_capture = {terminal_capture, $sformatf("%c", terminal_data)};
        $fdisplay(trace_file, "%0d,%s,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d",
            cycle, segment, r, p, pc, instruction, phase, acc, out, zero, carry, halt, count_wraps,
            terminal_data, terminal_write, terminal_sequence);
        $fdisplay(waveform_file, "%0d,1,1,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d", cycle,
            pc, instruction, phase, acc, out, zero, carry, halt, terminal_data, terminal_write, terminal_sequence);
    endtask

    initial begin
        if (!$value$plusargs("trace=%s", trace_path)) trace_path = "cpu-trace.csv";
        if (!$value$plusargs("waveform=%s", waveform_path)) waveform_path = "cpu-waveform.csv";
        if ($value$plusargs("count_cycles=%d", count_cycles)) begin end
        trace_file = $fopen(trace_path, "w");
        waveform_file = $fopen(waveform_path, "w");
        if (!trace_file || !waveform_file) $fatal(1, "Cannot open trace");
        $fdisplay(trace_file, "cycle,segment,reset,program_select,pc,instruction,phase,acc,out,zero,carry,halt,count_wraps,terminal_data,terminal_write,terminal_sequence");
        $fdisplay(waveform_file, "cycle,half,clk,pc,instruction,phase,acc,out,zero,carry,halt,terminal_data,terminal_write,terminal_sequence");
        tick(1, 0);
        for (i = 0; i < count_cycles; i = i + 1) tick(0, 0);
        if (count_cycles >= 1600 && count_wraps < 1) $fatal(1, "Counter did not wrap through carry and zero");
        segment = "logic";
        tick(1, 1);
        for (i = 0; i < 40; i = i + 1) tick(0, 1);
        if (!halt || out != 8'haa || acc != 8'haa || pc != 15 || !phase || carry || zero)
            $fatal(1, "Logic program final state differs");
        // A halted CPU ignores even a changed ROM selector, until synchronous reset.
        for (i = 0; i < 16; i = i + 1) tick(0, i[0]);
        segment = "text";
        tick(1, 2);
        for (i = 0; i < 60; i = i + 1) tick(0, 2);
        if (terminal_capture != "HELLO\n" || terminal_sequence != 6 || terminal_write || !halt)
            $fatal(1, "Text output must preserve repeated letters and stop at HALT: %s", terminal_capture);
        segment = "reset_out";
        tick(1, 2); tick(0, 2); tick(0, 2); tick(0, 2);
        tick(1, 2); // reset wins over a pending EXEC OUT
        if (terminal_write || terminal_sequence || terminal_data)
            $fatal(1, "Reset must not produce a character event");
        segment = "reserved";
        tick(1, 3);
        for (i = 0; i < 8; i = i + 1) tick(0, 3);
        if (!halt || terminal_write || terminal_sequence) $fatal(1, "Reserved program must halt without writes");
        segment = "random";
        tick(1, 0);
        for (i = 0; i < 300; i = i + 1) begin
            random_state = random_state * 32'd1664525 + 32'd1013904223;
            tick((i % 23 == 0) || (random_state[3:0] == 0), random_state[17:16]);
        end
        $fclose(trace_file);
        $fclose(waveform_file);
        $display("TEACHING_CPU_PASS vectors=%0d counter_wraps=%0d text_bytes=6 repeated_L_pass=1", cycle, count_wraps);
        $finish;
    end
endmodule
