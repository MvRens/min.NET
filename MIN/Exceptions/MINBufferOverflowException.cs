using System;

namespace MIN.Exceptions
{
    /// <inheritdoc />
    public class MINBufferOverflowException : Exception
    {
        /// <inheritdoc />
        public MINBufferOverflowException(string message)
            : base(message)
        {
        }
    }
}
