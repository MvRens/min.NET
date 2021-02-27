namespace MIN
{
    /// <summary>
    /// Represents an unencoded MIN frame. Intended for internal use and unit testing.
    /// </summary>
    public class MINFrame
    {
        /// <summary>
        /// The ID of the frame, including control bits.
        /// </summary>
        public byte Id { get; }

        /// <summary>
        /// The payload.
        /// </summary>
        public byte[] Payload { get; }
        
        /// <summary>
        /// Whether the frame expects an ACK.
        /// </summary>
        public bool Transport { get; }


        /// <summary>
        /// Initializes an instance of a MINFrame.
        /// </summary>
        /// <param name="id">The ID of the frame, including control bits.</param>
        /// <param name="payload">The payload.</param>
        /// <param name="transport">Whether the frame expects an ACK.</param>
        public MINFrame(byte id, byte[] payload, bool transport)
        {
            Id = id;
            Payload = payload;
            Transport = transport;
        }
    }
}
