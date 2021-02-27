using System;
using MIN.Abstractions;

namespace MIN.Default
{
    /// <summary>
    /// Implements IMINTimeProvider for DateTime.Now.
    /// </summary>
    public class SystemTimeProvider : IMINTimeProvider
    {
        /// <inheritdoc />
        public DateTime Now()
        {
            return DateTime.Now;
        }
    }
}
