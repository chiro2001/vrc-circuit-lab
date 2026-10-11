`timescale 1ns/1ps
module teaching_cpu_tb;
    localparam MAX_CYCLES = 8192;
    reg clk = 0, reset = 0;
    wire [8:0] pc;
    wire [11:0] instruction;
    wire [2:0] phase;
    wire [7:0] acc, x, sp, out;
    wire zero, carry, halt;
    wire [7:0] terminal_data, terminal_sequence;
    wire terminal_write;
    reg reset_vector [0:MAX_CYCLES-1];
    string program_path, ram_path, reset_path, trace_path, waveform_path, final_ram_path;
    integer cycles = 256, cycle, trace_file, waveform_file, final_ram_file;
    integer i;
    reg [11:0] program_words [0:511];
    reg [7:0] initial_ram [0:255];
    wire [7:0] ram_read_address, ram_read_data, ram_write_address, ram_write_data;
    wire ram_write_enable;
    teaching_cpu dut(.*);
    assign ram_read_address = dut.ram_read_address;
    assign ram_read_data = dut.ram_data;
    assign ram_write_address = dut.ram_write_address;
    assign ram_write_data = dut.ram_write_data;
    assign ram_write_enable = dut.ram_write_enable;
    always #5 clk = ~clk;

    task automatic tick;
        reg reset_write_attempt;
        begin
            @(negedge clk);
            reset = reset_vector[cycle];
            #1;
            reset_write_attempt = reset && !halt &&
                ((phase == 3'd1 && (instruction[11:8] == 4'h9 ||
                                    (instruction[11:8] == 4'he && instruction[7:4] == 4'h5))) ||
                 (phase == 3'd3 && instruction[11:8] == 4'he && instruction[7:4] == 4'he) ||
                 phase == 3'd4);
            $fdisplay(waveform_file,
                "%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d",
                cycle, 0, 0, pc, instruction, phase, acc, x, sp, out, zero, carry, halt,
                terminal_data, terminal_write, terminal_sequence, dut.rom.address, dut.rom.data,
                ram_read_address, ram_read_data, ram_write_address, ram_write_data, ram_write_enable);
            @(posedge clk); #1;
            $fdisplay(trace_file,
                "%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d",
                cycle, reset, pc, instruction, phase, acc, x, sp, out, zero, carry, halt,
                terminal_data, terminal_write, terminal_sequence, dut.rom.address, dut.rom.data,
                ram_read_address, ram_read_data, ram_write_address, ram_write_data, ram_write_enable);
            $fdisplay(waveform_file,
                "%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d",
                cycle, 1, 1, pc, instruction, phase, acc, x, sp, out, zero, carry, halt,
                terminal_data, terminal_write, terminal_sequence, dut.rom.address, dut.rom.data,
                ram_read_address, ram_read_data, ram_write_address, ram_write_data, ram_write_enable);
            if (reset_write_attempt) begin
                // This records the attempted store before the edge. The public
                // write-enable is already gated low by reset at that same edge.
                $display("RESET_WRITE_SUPPRESSED cycle=%0d raw=1 effective=%0d", cycle, ram_write_enable);
            end
            cycle = cycle + 1;
        end
    endtask

    initial begin
        if (!$value$plusargs("program=%s", program_path)) program_path = "program.hex";
        if (!$value$plusargs("ram=%s", ram_path)) ram_path = "ram.hex";
        if (!$value$plusargs("resets=%s", reset_path)) reset_path = "resets.mem";
        if (!$value$plusargs("trace=%s", trace_path)) trace_path = "trace.csv";
        if (!$value$plusargs("waveform=%s", waveform_path)) waveform_path = "waveform.csv";
        if (!$value$plusargs("final_ram=%s", final_ram_path)) final_ram_path = "final-ram.hex";
        if ($value$plusargs("cycles=%d", cycles)) begin end
        if (cycles > MAX_CYCLES) $fatal(1, "cycles exceeds MAX_CYCLES");
        $readmemh(program_path, program_words);
        $readmemh(ram_path, initial_ram);
        $readmemh(reset_path, reset_vector);
        for (i = 0; i < 512; i = i + 1) dut.rom.words[i] = program_words[i];
        for (i = 0; i < 256; i = i + 1) dut.ram.bytes[i] = initial_ram[i];
        trace_file = $fopen(trace_path, "w");
        waveform_file = $fopen(waveform_path, "w");
        if (!trace_file || !waveform_file) $fatal(1, "Unable to open trace output");
        $fdisplay(trace_file,
            "cycle,reset,pc,instruction,phase,acc,x,sp,out,zero,carry,halt,terminal_data,terminal_write,terminal_sequence,rom_address,rom_data,ram_read_address,ram_read_data,ram_write_address,ram_write_data,ram_write_enable");
        $fdisplay(waveform_file,
            "cycle,half,clk,pc,instruction,phase,acc,x,sp,out,zero,carry,halt,terminal_data,terminal_write,terminal_sequence,rom_address,rom_data,ram_read_address,ram_read_data,ram_write_address,ram_write_data,ram_write_enable");
        cycle = 0;
        for (i = 0; i < cycles; i = i + 1) tick();
        final_ram_file = $fopen(final_ram_path, "w");
        if (!final_ram_file) $fatal(1, "Unable to open final RAM output");
        for (i = 0; i < 256; i = i + 1) $fdisplay(final_ram_file, "%02x", dut.ram.bytes[i]);
        $fclose(final_ram_file);
        $fclose(trace_file);
        $fclose(waveform_file);
        $display("TEACHING_CPU_PASS cycles=%0d pc=%0d acc=%02x out=%02x halt=%0d", cycles, pc, acc, out, halt);
        $finish;
    end
endmodule
