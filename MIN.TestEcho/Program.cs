using System;
using System.Diagnostics;
using System.Threading;
using MIN.Abstractions;
using MIN.SerialPort;
using Serilog;
using Serilog.Extensions.Logging;

namespace MIN.TestEcho
{
    public static class Program
    {
        private static ILogger logger;
        private static readonly ManualResetEventSlim EchoReceived = new ManualResetEventSlim();
        
        
        public static void Main()
        {
            logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File("MINTestEcho.log")
                .CreateLogger();
            
            // Assumes the target is running the echo sketch
            var protocol = new MINProtocol(
                new MINSerialTransport("COM11", 115200), 
                new SerilogLoggerProvider(logger).CreateLogger(null));
            
            protocol.OnFrame += ProtocolOnOnFrame;
            protocol.Start();

            protocol.Reset().Wait();
            protocol.SendFrame(42, new byte[] { 1, 2, 3 });
            
            logger.Information("Waiting for echo response...");
            EchoReceived.Wait();
            
            protocol.Dispose();

            if (Debugger.IsAttached)
            {
                Console.WriteLine("Press any Enter key to continue");
                Console.ReadLine();
            }
        }

        
        private static void ProtocolOnOnFrame(object sender, MINFrameEventArgs e)
        {
            logger.Information("Echo received: id={id}, payload={payload}", e.Id, BitConverter.ToString(e.Payload));
            EchoReceived.Set();
        }
    }
}
