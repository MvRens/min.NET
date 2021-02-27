using System;

namespace MIN.Exceptions
{
    /// <summary>
    /// Thrown when a frame ID exceeds the maximum allowed value of 63.
    /// </summary>
    public class MINFrameIdRangeException : Exception
    {
        /// <inheritdoc />
        public MINFrameIdRangeException(byte actualId)
            : base($"Frame ID for the MIN protocol must be less than or equal to 63, is: {actualId}")
        {
        }
    }
}
