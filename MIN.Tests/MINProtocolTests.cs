using System.Threading;
using Microsoft.Extensions.Logging;
using MIN.Tests.Mock;
using Xunit;
using Xunit.Abstractions;

namespace MIN.Tests
{
    public class MINProtocolTests
    {
        private readonly MINMockTransport transport = new MINMockTransport();
        private readonly ILogger logger;
        private readonly MINMockTimeProvider timeProvider = new MINMockTimeProvider();


        public MINProtocolTests(ITestOutputHelper outputHelper)
        {
            logger = LoggerFactory
                .Create(builder =>
                {
                    builder.AddXUnit(outputHelper);
                })
                .CreateLogger<MINProtocolTests>();
        }
        
        
        [Fact]
        public void Create()
        {
            var protocol = new MINProtocol(transport, logger, timeProvider);
            protocol.Dispose();
        }


        [Fact]
        public void SendFrame()
        {
            var frame = new MINFrame(1, new byte[] { 1, 2, 3 }, false);
            transport.ExpectWrite(MINFrameEncoder.GetFrameData(frame, 0));
            
            var protocol = new MINProtocol(transport, logger, timeProvider);
            protocol.SendFrame(frame.Id, frame.Payload);

            transport.WaitNextEmpty();
            
            protocol.Dispose();
        }


        [Fact]
        public void SendFrameLater()
        {
            // Make sure the worker signals correctly when a new frame arrives later
            var frame = new MINFrame(1, new byte[] { 1, 2, 3 }, false);
            transport.ExpectWrite(MINFrameEncoder.GetFrameData(frame, 0));

            var protocol = new MINProtocol(transport, logger, timeProvider);

            Thread.Sleep(50);
            protocol.SendFrame(frame.Id, frame.Payload);

            transport.WaitNextEmpty();

            protocol.Dispose();
        }
    }
}
