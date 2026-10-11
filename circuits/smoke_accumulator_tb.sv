`timescale 1ns/1ps
module smoke_accumulator_tb;
    reg clk = 0;
    reg reset = 1;
    reg load = 0;
    reg enable = 0;
    reg [3:0] data = 0;
    wire [3:0] q;
    wire zero, parity;
    reg [3:0] expected = 0;
    reg [31:0] random_state = 32'h12345678;
    integer cycle = 0;
    integer trace_file;
    integer initial_q, addend, controls, i;
    string trace_path;

    smoke_accumulator dut(.*);
    always #5 clk = ~clk;

    task tick(input bit r, input bit l, input bit e, input reg [3:0] d);
        @(negedge clk);
        reset = r; load = l; enable = e; data = d;
        if (r) expected = 0;
        else if (l) expected = d;
        else if (e) expected = expected + d;
        @(posedge clk);
        #1;
        cycle = cycle + 1;
        if (q !== expected || zero !== (expected == 0) || parity !== (^expected))
            $fatal(1, "cycle=%0d q=%0d expected=%0d zero=%b parity=%b", cycle, q, expected, zero, parity);
        $fdisplay(trace_file, "%0d,%0d,%0d,%0d,%0d,%0d,%0d,%0d", cycle, r, l, e, d, q, zero, parity);
    endtask

    initial begin
        if (!$value$plusargs("trace=%s", trace_path)) trace_path = "smoke-trace.csv";
        trace_file = $fopen(trace_path, "w");
        if (!trace_file) $fatal(1, "Cannot open trace output");
        $fdisplay(trace_file, "cycle,reset,load,enable,data,q,zero,parity");
        tick(1, 0, 0, 0);
        // Exhaust every 4-bit addition, including the carry chain and wraparound.
        for (initial_q = 0; initial_q < 16; initial_q = initial_q + 1)
            for (addend = 0; addend < 16; addend = addend + 1) begin
                tick(0, 1, 0, initial_q);
                tick(0, 0, 1, addend);
            end
        // All control combinations: check hold and reset/load priority.
        for (controls = 0; controls < 8; controls = controls + 1)
            for (addend = 0; addend < 16; addend = addend + 1)
                tick(controls[2], controls[1], controls[0], addend);
        // Deterministic randomized transitions; identical for RTL and synthesized netlist.
        for (i = 0; i < 256; i = i + 1) begin
            random_state = random_state * 32'd1664525 + 32'd1013904223;
            tick(random_state[15] & random_state[16], random_state[12], random_state[11], random_state[7:4]);
        end
        $fclose(trace_file);
        $display("HDL_SMOKE_PASS vectors=%0d", cycle);
        $finish;
    end
endmodule
