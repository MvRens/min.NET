using System;
using System.Threading;
using System.Threading.Tasks;

namespace MIN.Abstractions
{
    /// <summary>
    /// Implements the low-level transport for the MIN protocol.
    /// </summary>
    /// <remarks>
    /// All methods are guaranteed to be called from the same worker thread,
    /// but be aware that the object is constructed outside of this thread.
    /// </remarks>
    public interface IMINTransport : IDisposable
    {
        /// <summary>
        /// Connects the transport layer. Can block until succesful or the CancellationToken is signalled.
        /// </summary>
        /// <param name="cancellationToken">A CancellationToken which will be signalled when the MINProtocol is disposed.</param>
        void Connect(CancellationToken cancellationToken);

        /// <summary>
        /// Write the raw data to the transport layer.
        /// </summary>
        /// <param name="data">The raw data to send</param>
        /// <param name="cancellationToken">A CancellationToken which will be signalled when the MINProtocol is disposed.</param>
        void Write(byte[] data, CancellationToken cancellationToken);

        /// <summary>
        /// Read all available bytes from the transport layer. Must not block if no data is available.
        /// </summary>
        /// <returns>The raw data available</returns>
        byte[] ReadAll();
    }


    /// <summary>
    /// Extends the IMINTransport interface to indicate a transport supports
    /// signalling when data is available to read. If not implemented the MINProtocol
    /// will fall back to polling.
    /// </summary>
    /// <remarks>
    /// Note that ReadAll can still be called regularly even when the DataAvailable
    /// event has not been signalled, due to other events occuring in the worker.
    /// </remarks>
    public interface IMINAwaitableTransport : IMINTransport
    {
        /// <summary>
        /// Returns a WaitHandle which should be signalled when data is available to read.
        /// </summary>
        WaitHandle DataAvailable();
    }
}
