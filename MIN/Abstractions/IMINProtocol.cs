using System;
using System.Threading.Tasks;

namespace MIN.Abstractions
{
    /// <summary>
    /// Contains information about an incoming frame.
    /// </summary>
    public class FrameEventArgs : EventArgs
    {
        /// <summary>
        /// The Id of the frame as specified by the sender.
        /// </summary>
        public byte Id { get; }
        
        /// <summary>
        /// The payload for the frame.
        /// </summary>
        public byte[] Payload { get; }


        /// <inheritdoc />
        public FrameEventArgs(byte id, byte[] payload)
        {
            Id = id;
            Payload = payload;
        }
    }



    /// <summary>
    /// An event handler which is called when an incoming frame arrives.
    /// </summary>
    /// <param name="sender">The MINProtocol implementation from which the frame originates</param>
    /// <param name="e">Information about the incoming frame</param>
    public delegate void FrameEventHandler(object sender, FrameEventArgs e);
    
    
    /// <summary>
    /// Implements frame and transport handling for the MIN protocol.
    /// </summary>
    public interface IMINProtocol : IDisposable
    {
        /// <summary>
        /// Returns statistics on the MIN protocol since the last start.
        /// </summary>
        /// <returns></returns>
        MINStats Stats();


        /// <summary>
        /// Sends a MIN frame with a given ID directly on the wire. Will be silently discarded if any line noise.
        /// </summary>
        /// <param name="id">ID of MIN frame (0 .. 63)</param>
        /// <param name="payload">up to 255 bytes of payload</param>
        void SendFrame(byte id, byte[] payload);

        /// <summary>
        /// Queues a MIN frame for transmission through the transport protocol. Will be retransmitted until it is
        /// delivered or the connection has timed out.
        /// </summary>
        /// <param name="id">ID of MIN frame (0 .. 63)</param>
        /// <param name="payload">up to 255 bytes of payload</param>
        /// <returns>A task which will be completed when the message has been delivered, or fault when it has failed</returns>
        Task QueueFrame(byte id, byte[] payload);


        /// <summary>
        /// An event which is called when an incoming frame arrives.
        /// </summary>
        event FrameEventHandler OnFrame;
    }
    
    
    /// <summary>
    /// Statistics on the MIN protocol since the last start.
    /// </summary>
    public class MINStats
    {
        // <summary>
        // The maximum number of frames in the outstanding buffer. (not actually implemented in the Python reference? skipped)
        // </summary>
        //public int LongestTransportFifo { get; set; }
        
        /// <summary>
        /// The timestamp of the last sent frame.
        /// </summary>
        public DateTime LastSentFrame { get; set; }
        
        /// <summary>
        /// The number of times a frame was dropped due to the sequence being out of range.
        /// </summary>
        public int SequenceMismatchDrops { get; set; }
        
        /// <summary>
        /// The number of retransmitted frames.
        /// </summary>
        public int RetransmittedFrames { get; set; }

        /// <summary>
        /// The number of resets received.
        /// </summary>
        public int ResetsReceived { get; set; }

        // <summary>
        // The number of duplicate frames. (not actually implemented in the Python reference? skipped)
        // </summary>
        //public int DuplicateFrames { get; set; }

        // <summary>
        // The number of mismatched acks. (not actually implemented in the Python reference? skipped)
        // </summary>
        //public int MismatchedAcks { get; set; }
        
        /// <summary>
        /// The number of spurious acks received.
        /// </summary>
        public int SpuriousAcks { get; set; }
    }
}
