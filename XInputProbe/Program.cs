using DMC5DualSense.Bridge;

var input = XInputReader.ReadFirstConnected();
Console.WriteLine($"connected={input.Connected};lt={input.LeftTrigger:0.000};rt={input.RightTrigger:0.000}");
