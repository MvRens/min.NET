using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using MIN.Abstractions;

namespace MIN.Tests.Mock
{
    public class MINMockTransport : IMINAwaitableTransport
    {
        private readonly object expectedQueuesLock = new object();
        private readonly Queue<byte[]> expectedWrites = new Queue<byte[]>();
        private readonly ManualResetEventSlim expectedReadsEvent = new ManualResetEventSlim();
        private readonly Queue<byte[]> expectedReads = new Queue<byte[]>();
        private ManualResetEventSlim emptyEvent = new ManualResetEventSlim();

        public void Dispose()
        {
        }


        public void Validate()
        {
            expectedWrites.Should().BeEmpty();
            expectedReads.Should().BeEmpty();
        }


        public void ExpectWrite(byte[] data)
        {
            lock (expectedQueuesLock)
            {
                expectedWrites.Enqueue(data);
            }
        }


        public void ExpectRead(byte[] data)
        {
            lock (expectedQueuesLock)
            {
                expectedReads.Enqueue(data);
                expectedReadsEvent.Set();
            }
        }


        public void WaitNextEmpty()
        {
            emptyEvent.Wait();
        }
            
        
        public void Connect(CancellationToken cancellationToken)
        {
        }

        
        public void Write(byte[] data, CancellationToken cancellationToken)
        {
            lock (expectedQueuesLock)
            {
                expectedWrites.Should().NotBeEmpty();
                data.Should().Equal(expectedWrites.Dequeue());
                
                if (expectedWrites.Count == 0)
                    CheckEmpty();
            }
        }


        public byte[] ReadAll()
        {
            lock (expectedQueuesLock)
            {
                var hasData = expectedReads.Count > 0;
                var nextData = hasData ? expectedReads.Dequeue() : Array.Empty<byte>();

                if (!hasData || expectedReads.Count != 0) 
                    return nextData;
                
                expectedReadsEvent.Reset();
                CheckEmpty();

                return nextData;
            }
        }
        

        public WaitHandle DataAvailable()
        {
            return expectedReadsEvent.WaitHandle;
        }


        private void CheckEmpty()
        {
            if (expectedReads.Count == 0 && expectedWrites.Count == 0)
                emptyEvent.Set();
            else
                emptyEvent.Reset();
        }
    }
}
