using System;

namespace MIN.Abstractions
{
    /// <summary>
    /// Abstracts the current system time for unit testing.
    /// </summary>
    public interface IMINTimeProvider
    {
        /// <summary>
        /// "Now. Whatever you’re looking at now, is happening now."
        /// "Well, what happened to then?"
        /// "We just passed it."
        /// - Spaceballs
        /// </summary>
        /// <returns>The current system time</returns>
        DateTime Now();
    }
}