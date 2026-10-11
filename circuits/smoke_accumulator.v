// A small synchronous circuit: reset > load > enabled addition > hold.
module smoke_accumulator (
    input wire clk,
    input wire reset,
    input wire load,
    input wire enable,
    input wire [3:0] data,
    output reg [3:0] q,
    output wire zero,
    output wire parity
);
    always @(posedge clk) begin
        if (reset)
            q <= 4'b0000;
        else if (load)
            q <= data;
        else if (enable)
            q <= q + data;
    end

    assign zero = (q == 4'b0000);
    assign parity = ^q;
endmodule
