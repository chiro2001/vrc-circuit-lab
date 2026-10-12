// Simulation models for the explicit teaching_rom / teaching_ram synthesis
// black boxes. ROM and RAM reads are asynchronous; RAM writes commit on clk's
// rising edge. CPU reset never initializes either memory.
module teaching_rom(input wire [8:0] address, output wire [11:0] data);
    reg [11:0] words [0:511];
    integer i;
    initial for (i = 0; i < 512; i = i + 1) words[i] = 12'h000;
    assign data = words[address];
endmodule

module teaching_ram(input wire clk, input wire [9:0] read_address,
    output wire [7:0] read_data, input wire write_enable, input wire [9:0] write_address,
    input wire [7:0] write_data);
    reg [7:0] bytes [0:1023];
    integer i;
    initial for (i = 0; i < 1024; i = i + 1) bytes[i] = 8'h00;
    assign read_data = bytes[read_address];
    always @(posedge clk) if (write_enable) bytes[write_address] <= write_data;
endmodule
