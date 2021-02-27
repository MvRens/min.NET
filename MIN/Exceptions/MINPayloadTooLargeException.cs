using System;

namespace MIN.Exceptions
{
    /// <summary>
    /// Thrown when a payload exceeds the maximum allowed size of 255.
    /// </summary>
    public class MINPayloadTooLargeException : Exception
    {
        /// <inheritdoc />
        public MINPayloadTooLargeException(int actualSize)
            : base($"Payload larger than the allowed 255 bytes for the MIN protocol, is: {actualSize} bytes")
        {
        }
    }
}
