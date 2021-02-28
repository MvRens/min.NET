using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Force.Crc32;
using Microsoft.Extensions.Logging;
using MIN.Abstractions;
using MIN.Default;
using MIN.Exceptions;

/*
 *
 * Some of the code and comments have been ported from the reference Python implementation:
 * https://github.com/min-protocol/min/tree/master/host
 *
 */
namespace MIN
{
    /// <summary>
    /// Implements frame and transport handling for the MIN protocol.
    /// Relies on an IMINTransport implementation for the actual communication.
    /// </summary>
    public class MINProtocol : IMINProtocol
    {
        /// <summary>
        /// Maximum number of outstanding frames to send
        /// </summary>
        public int SendWindowSize { get; set; } = 100;

        /// <summary>
        /// Number of outstanding unacknowledged frames that can be received
        /// </summary>
        public int ReceiveWindowSize { get; set; } = 8;


        /// <summary>
        /// Time before connection assumed to have been lost and retransmissions stopped
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Time before ACK frames are resent
        /// </summary>
        public TimeSpan AckRetransmitTimeout { get; set; } = TimeSpan.FromMilliseconds(25);

        /// <summary>
        /// Time before frames are resent
        /// </summary>
        public TimeSpan FrameRetransmitTimeout { get; set; } = TimeSpan.FromMilliseconds(50);


        private readonly IMINTransport transport;
        private readonly ILogger logger;
        private readonly IMINTimeProvider timeProvider;

        private CancellationTokenSource workerThreadCancellation = new CancellationTokenSource();
        private readonly ManualResetEventSlim sendResetEvent = new ManualResetEventSlim();
        private readonly object resetCompletedLock = new object();
        private TaskCompletionSource<bool> resetCompleted;

        private readonly object statsLock = new object();
        private readonly MINStats stats = new MINStats();


        // Null propagation is fine too, but I like skipping it if the log messages requires extra work to generate 
        // the parameter values, even if it's completely negligable on any machine capable of running .NET.
        private bool LogDebugEnabled => logger != null && logger.IsEnabled(LogLevel.Debug);
        private bool LogInformationEnabled => logger != null && logger.IsEnabled(LogLevel.Information);


        /// <summary>
        /// Creates a new instance of the MINProtocol
        /// </summary>
        /// <param name="transport">The transport implementation to use</param>
        /// <param name="logger">A Microsoft.Extensions.Logging compatible implementation which receives protocol logging. If not provided, no logging will occur.</param>
        /// <param name="timeProvider">An implementation of IMINTimeProvider for testing purposes. Defaults to SystemTimeProvider.</param>
        public MINProtocol(IMINTransport transport, ILogger logger = null, IMINTimeProvider timeProvider = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.logger = logger;
            this.timeProvider = timeProvider ?? new SystemTimeProvider();

        }


        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
        }


        /// <inheritdoc />
        public void Start()
        {
            Stop();
            InternalReset(true);

            workerThreadCancellation = new CancellationTokenSource();
            var workerThread = new Thread(() => RunWorker(workerThreadCancellation));
            workerThread.Start();
        }


        /// <inheritdoc />
        public void Stop()
        {
            // The thread will dispose of the CancellationTokenSource after it's cancelled
            workerThreadCancellation?.Cancel();
            workerThreadCancellation = null;
        }

        
        /// <inheritdoc />
        public MINStats Stats()
        {
            lock (statsLock)
            {
                return new MINStats
                {
                    LastSentFrame = stats.LastSentFrame,
                    SequenceMismatchDrops = stats.SequenceMismatchDrops,
                    RetransmittedFrames = stats.RetransmittedFrames,
                    ResetsReceived = stats.ResetsReceived,
                    SpuriousAcks = stats.SpuriousAcks
                };
            }
        }


        /// <inheritdoc />
        public void SendFrame(byte id, byte[] payload)
        {
            ValidateFrame(id, payload);

            QueueTransport(new QueuedFrame(new MINFrame(id, payload, false)));

            if (LogInformationEnabled)
                logger.LogInformation("Queueing MIN frame: id={id}, transport={transport}, payload={payload}", id,
                    false, BitConverter.ToString(payload));
        }


        /// <inheritdoc />
        public Task QueueFrame(byte id, byte[] payload)
        {
            ValidateFrame(id, payload);

            var taskCompletionSource = new TaskCompletionSource<bool>();
            QueueTransport(new QueuedFrame(new MINFrame(id, payload, false), taskCompletionSource));

            if (LogInformationEnabled)
                logger.LogInformation("Queueing MIN frame: id={id}, transport={transport}, payload={payload}", id, true,
                    BitConverter.ToString(payload));

            return taskCompletionSource.Task;
        }


        /// <inheritdoc />
        public Task Reset()
        {
            logger?.LogDebug("Signalling RESET");
            Task resetTask;

            lock (resetCompletedLock)
            {
                if (resetCompleted == null)
                    resetCompleted = new TaskCompletionSource<bool>();

                resetTask = resetCompleted.Task;
            }
            
            sendResetEvent.Set();
            return resetTask;
        }


        /// <inheritdoc />
        public event MINConnectionStateEventHandler OnConnected;

        /// <inheritdoc />
        public event MINConnectionStateEventHandler OnDisconnected;

        /// <inheritdoc />
        public event MINFrameEventHandler OnFrame;

        
        // ReSharper disable once SuggestBaseTypeForParameter - I like the explicitness
        private static void ValidateFrame(byte id, byte[] payload)
        {
            if (payload.Length > 255)
                throw new MINPayloadTooLargeException(payload.Length);

            if (id > 63)
                throw new MINFrameIdRangeException(id);
        }


        private readonly object transportQueueLock = new object();
        private readonly List<QueuedFrame> transportQueue = new List<QueuedFrame>();
        private readonly ManualResetEventSlim transportQueueEvent = new ManualResetEventSlim(false);


        private void QueueTransport(QueuedFrame queuedFrame)
        {
            lock (transportQueueLock)
            {
                if (transportQueue.Count >= SendWindowSize)
                    throw new MINBufferOverflowException("No space available in transport queue");

                transportQueue.Add(queuedFrame);
                transportQueueEvent.Set();
            }
        }


        // These fields should only be accessed from the thread
        private readonly List<QueuedFrame> transportAwaitingAck = new List<QueuedFrame>();
        private readonly Dictionary<byte, MINFrame> transportStashed = new Dictionary<byte, MINFrame>();
        private DateTime lastAck = DateTime.MinValue;
        private DateTime lastReceivedData = DateTime.MinValue;
        private DateTime lastReceivedFrame = DateTime.MinValue;
        private byte nextSendSequence;
        private byte nextReceiveSequence;
        private byte? nackOutstanding;


        private void RunWorker(CancellationTokenSource cancellationTokenSource)
        {
            var waitHandles = new List<WaitHandle>
            {
                transportQueueEvent.WaitHandle,
                sendResetEvent.WaitHandle,
                cancellationTokenSource.Token.WaitHandle
            };
            
            
            var usePolling = false;
            if (transport is IMINAwaitableTransport awaitableTransport)
                waitHandles.Add(awaitableTransport.DataAvailable());
            else
                usePolling = true;

            var waitHandlesArray = waitHandles.ToArray();


            // TODO connect transport, handle OnDisconnect
            transport.Connect(cancellationTokenSource.Token);
            OnConnected?.Invoke(this, EventArgs.Empty);

            
            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    DateTime now;
                    bool remoteActive;

                    var yield = true;
                    try
                    {
                        if (sendResetEvent.IsSet)
                        {
                            logger?.LogDebug("Sending RESET");
                            var resetFrame = new MINFrame(MINWire.Reset, Array.Empty<byte>(), true);

                            WriteFrameData(resetFrame, 0, cancellationTokenSource.Token);
                            WriteFrameData(resetFrame, 0, cancellationTokenSource.Token);

                            transport.Reset();
                            InternalReset(false);
                            
                            sendResetEvent.Reset();

                            lock (resetCompletedLock)
                            {
                                resetCompleted?.TrySetResult(true);
                                resetCompleted = null;
                            }
                        }
                        
                        var incomingData = transport.ReadAll();
                        if (incomingData.Length > 0)
                        {
                            ProcessIncomingData(incomingData, cancellationTokenSource.Token);
                            yield = false;
                        }


                        var queuedFrame = GetNextQueuedFrame();
                        if (queuedFrame != null)
                        {
                            if (ProcessQueuedFrame(queuedFrame, cancellationTokenSource.Token))
                                FrameSent(queuedFrame);

                            yield = false;
                        }


                        now = timeProvider.Now();
                        remoteActive = now - lastReceivedFrame < IdleTimeout;

                        if (now - lastAck > AckRetransmitTimeout && remoteActive)
                            SendAck(cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }



                    // If any frames were processed, spin once more to check again
                    if (!yield)
                        continue;


                    // Wait for the next thing to do to prevent an idle loop
                    var timeout = Timeout.Infinite;

                    if (remoteActive)
                        // Frames were received recently, the Ack is expected to be resent regularly.
                        timeout = (int)Math.Max(0, (AckRetransmitTimeout - (now - lastAck)).TotalMilliseconds);


                    var oldestFrame = transportAwaitingAck.FirstOrDefault();
                    if (oldestFrame != null)
                    {
                        // There are unacknowledged frames, it is expected to be resent regularly.
                        var timeUntilResend = (int)(FrameRetransmitTimeout - (now - oldestFrame.LastSentTime)).TotalMilliseconds;
                        if (timeout == Timeout.Infinite || timeout > timeUntilResend)
                            timeout = timeUntilResend;
                    }


                    // Wait for the transport to have data available or fall back to polling, or any of the above timeouts
                    if (usePolling && timeout == Timeout.Infinite || timeout > 10)
                        timeout = 10;

                    WaitHandle.WaitAny(waitHandlesArray, timeout);
                }
                catch (Exception e)
                {
                    logger?.LogError(e, "Unhandled exception in MIN protocol worker thread: {message}", e.Message);
                    Thread.Sleep(500);
                }
            }

            cancellationTokenSource.Dispose();
        }


        private enum ReceiveState
        {
            SearchingForSOF,
            ReceivingIDControl,
            ReceivingLength,
            ReceivingSequence,
            ReceivingPayload,
            ReceivingChecksum3,
            ReceivingChecksum2,
            ReceivingChecksum1,
            ReceivingChecksum0,
            ReceivingEOF
        }

        private struct ReceivingFrame
        {
            public byte IDControl;
            public byte Sequence;
            public byte[] PayloadBuffer;
            public byte PayloadPosition;
            public uint Checksum;


            public bool IsControlOrTransport => (IDControl & MINWire.IDControlTransportMask) != 0;
        }

        private ReceiveState receiveState = ReceiveState.SearchingForSOF;
        private int receiveHeaderBytesSeen;
        private ReceivingFrame receivingFrame;
        

        // ReSharper disable once ParameterTypeCanBeEnumerable.Local - I like the explicitness
        private void ProcessIncomingData(byte[] data, CancellationToken cancellationToken)
        {
            lastReceivedData = timeProvider.Now();

            if (LogDebugEnabled)
                logger.LogDebug("Received bytes: {bytes}", BitConverter.ToString(data));

            foreach (var dataByte in data)
            {
                if (ContinueSearchForSOF(dataByte))
                    continue;

                switch (receiveState)
                {
                    case ReceiveState.SearchingForSOF:
                        break;

                    case ReceiveState.ReceivingIDControl:
                        receivingFrame.IDControl = dataByte;

                        receiveState = receivingFrame.IsControlOrTransport
                            ? ReceiveState.ReceivingSequence
                            : ReceiveState.ReceivingLength;
                        break;

                    case ReceiveState.ReceivingSequence:
                        receivingFrame.Sequence = dataByte;
                        receiveState = ReceiveState.ReceivingLength;
                        break;

                    case ReceiveState.ReceivingLength:
                        receivingFrame.PayloadBuffer = new byte[dataByte];
                        receivingFrame.PayloadPosition = 0;
                        receiveState = dataByte > 0 ? ReceiveState.ReceivingPayload : ReceiveState.ReceivingChecksum3;
                        break;

                    case ReceiveState.ReceivingPayload:
                        receivingFrame.PayloadBuffer[receivingFrame.PayloadPosition] = dataByte;
                        if (receivingFrame.PayloadPosition == receivingFrame.PayloadBuffer.Length - 1)
                            receiveState = ReceiveState.ReceivingChecksum3;
                        else
                            receivingFrame.PayloadPosition++;

                        break;

                    case ReceiveState.ReceivingChecksum3:
                        receivingFrame.Checksum = (uint) dataByte << 24;
                        receiveState = ReceiveState.ReceivingChecksum2;
                        break;

                    case ReceiveState.ReceivingChecksum2:
                        receivingFrame.Checksum |= (uint) dataByte << 16;
                        receiveState = ReceiveState.ReceivingChecksum1;
                        break;

                    case ReceiveState.ReceivingChecksum1:
                        receivingFrame.Checksum |= (uint) dataByte << 8;
                        receiveState = ReceiveState.ReceivingChecksum0;
                        break;

                    case ReceiveState.ReceivingChecksum0:
                        receivingFrame.Checksum |= dataByte;

                        var computedChecksum = Crc32Algorithm.Compute(receivingFrame.IsControlOrTransport
                            ? new[]
                            {
                                receivingFrame.IDControl, receivingFrame.Sequence,
                                (byte) receivingFrame.PayloadBuffer.Length
                            }
                            : new[] { receivingFrame.IDControl, (byte) receivingFrame.PayloadBuffer.Length });

                        computedChecksum = Crc32Algorithm.Append(computedChecksum, receivingFrame.PayloadBuffer);
                        if (receivingFrame.Checksum != computedChecksum)
                        {
                            logger?.LogWarning("CRC mismatch: expected=0x{expectedCRC}, actual=0x{actualCRC}), id={id}, sequence={sequence}, payload={payload}, frame dropped",
                                computedChecksum.ToString("x8"), receivingFrame.Checksum.ToString("x8"), 
                                receivingFrame.IDControl, receivingFrame.Sequence, BitConverter.ToString(receivingFrame.PayloadBuffer));

                            // Frame fails checksum, is dropped
                            receiveState = ReceiveState.SearchingForSOF;
                        }
                        else
                        {
                            // Checksum passes, wait for EOF
                            receiveState = ReceiveState.ReceivingEOF;
                        }

                        break;

                    case ReceiveState.ReceivingEOF:
                        if (dataByte == MINWire.EofByte)
                        {
                            // Frame received OK, pass up frame for handling
                            ProcessReceivedFrame(cancellationToken);
                        }
                        else
                        {
                            // Log "No EOF received, dropping frame"
                        }

                        // Look for next frame
                        receiveState = ReceiveState.SearchingForSOF;
                        break;

                    default:
                        // min_logger.error("Unexpected state, state machine reset")
                        // Should never get here but in case we do just reset
                        receiveState = ReceiveState.SearchingForSOF;
                        break;
                }
            }
        }


        private bool ContinueSearchForSOF(byte dataByte)
        {
            if (receiveHeaderBytesSeen == 2)
            {
                receiveHeaderBytesSeen = 0;
                switch (dataByte)
                {
                    case MINWire.HeaderByte:
                        receiveState = ReceiveState.ReceivingIDControl;
                        return true;

                    case MINWire.StuffByte:
                        // Discard this byte; carry on receiving the next character
                        return true;

                    default:
                        // By here something must have gone wrong, give up on this frame and look for new header
                        receiveState = ReceiveState.SearchingForSOF;
                        return true;
                }
            }

            if (dataByte == MINWire.HeaderByte)
                receiveHeaderBytesSeen++;
            else
                receiveHeaderBytesSeen = 0;

            return receiveState == ReceiveState.SearchingForSOF;
        }


        /// <summary>
        /// Handle a received MIN frame. Because this runs on a host with plenty of CPU time and memory we stash out-of-order frames
        /// and send negative acknowledgements(NACKs) to ask for missing ones.This greatly improves the performance in the presence
        /// of line noise: a dropped frame will be specifically requested to be resent and then the stashed frames appended in the
        /// right order.
        ///
        /// Note that the automatic retransmit of frames must be tuned carefully so that a window + a NACK received + retransmission
        /// of missing frames + ACK for the complete set is faster than the retransmission timeout otherwise there is unnecessary
        /// retransmission of frames which wastes bandwidth.
        ///
        /// The embedded version of this code does not implement NACKs: generally the MCU will not have enough memory to stash out-of-order
        /// frames for later reassembly.
        /// </summary>
        private void ProcessReceivedFrame(CancellationToken cancellationToken)
        {
            if (!receivingFrame.IsControlOrTransport)
            {
                var frame = new MINFrame(receivingFrame.IDControl, receivingFrame.PayloadBuffer, false);
                FrameReceived(frame);
                return;
            }

            logger?.LogDebug("MIN frame received: ID/control={id}, sequence={sequence}",
                receivingFrame.IDControl.ToString("x2"), receivingFrame.Sequence);

            switch (receivingFrame.IDControl)
            {
                case MINWire.Ack:
                    if (!ProcessReceivedAckFrame())
                        lock (statsLock)
                        {
                            stats.SpuriousAcks++;
                        }

                    break;

                case MINWire.Reset:
                    ProcessReceivedResetFrame();
                    break;

                default:
                    ProcessReceivedTransportFrame(cancellationToken);
                    break;
            }
        }


        private bool ProcessReceivedAckFrame()
        {
            if (transportAwaitingAck.Count == 0)
            {
                //nextReceiveSequence = receivingFrame.Sequence;
                return false;
            }

            // The ACK number indicates the serial number of the next packet wanted, so any previous packets can be marked off
            var ackedSequence = receivingFrame.Sequence;
            var maxSequence = nextSendSequence;

            // Normalize all the values to account for the overflowing sequence number
            unchecked
            {
                var minSequence = transportAwaitingAck[0].Sequence;

                maxSequence -= minSequence;
                ackedSequence -= minSequence;
            }

            if (ackedSequence > maxSequence)
                return false;


            // Due to the normalization, ackedSequence is now equal to the number of acked frames
            logger?.LogDebug("ACK received: sequence={sequence}, count={ackedSequence}", receivingFrame.Sequence,
                ackedSequence);

            if (ackedSequence > transportAwaitingAck.Count)
                throw new InvalidOperationException("Frame queue is out of sync with the sequence generator");

            foreach (var queuedFrame in transportAwaitingAck.Take(ackedSequence))
                queuedFrame.TaskCompletionSource?.TrySetResult(true);

            transportAwaitingAck.RemoveRange(0, ackedSequence);
            return true;
        }


        private void ProcessReceivedResetFrame()
        {
            logger?.LogDebug("RESET received: sequence = {sequence}", receivingFrame.Sequence);

            lock (statsLock)
            {
                stats.ResetsReceived++;
            }

            InternalReset(true);
            
            // TODO raise OnReset event?
        }


        private void ProcessReceivedTransportFrame(CancellationToken cancellationToken)
        {
            lastReceivedFrame = timeProvider.Now();

            var frame = new MINFrame((byte) (receivingFrame.IDControl & MINWire.IDMask), receivingFrame.PayloadBuffer,
                true);

            if (receivingFrame.Sequence == nextReceiveSequence)
                ProcessExpectedReceivedTransportFrame(frame, cancellationToken);
            else
                ProcessUnexpectedReceivedTransportFrame(frame, cancellationToken);
        }


        private void ProcessExpectedReceivedTransportFrame(MINFrame frame, CancellationToken cancellationToken)
        {
            logger?.LogDebug("MIN application frame received: id={id}, sequence={sequence})",
                frame.Id, receivingFrame.Sequence);

            FrameReceived(frame);

            // Now see if there are stashed frames it joins up with and 'receive' those
            while (true)
            {
                unchecked
                {
                    nextReceiveSequence++;
                }

                if (nextReceiveSequence == nackOutstanding)
                    // The missing frames we asked for have joined up with the main sequence
                    nackOutstanding = null;

                if (!transportStashed.TryGetValue(nextReceiveSequence, out var stashedFrame))
                    break;

                logger?.LogDebug("MIN application stashed frame recovered: sequence={sequence}, id={id}",
                    nextReceiveSequence, stashedFrame.Id);

                FrameReceived(stashedFrame);
                transportStashed.Remove(nextReceiveSequence);
            }

            // If there are stashed frames left then it means that the stashed ones have missing frames in the sequence
            if (!nackOutstanding.HasValue && transportStashed.Count > 0)
            {
                // We can send a NACK to ask for those too, starting with the earliest sequence number
                // TODO the reference implementation also takes the lowest sequence number, but is that always correct with it wrapping? I can't quite wrap my head around it (pun intended)
                var earliestSequence = transportStashed.Keys.Min();

                // Check it's within the window size from us
                var missingFrames = unchecked((byte) (earliestSequence - nextReceiveSequence));

                if (missingFrames < ReceiveWindowSize)
                {
                    nackOutstanding = earliestSequence;
                    SendNack(earliestSequence, cancellationToken);
                }
                else
                {
                    // Something has gone wrong here: stale stuff is hanging around, give up and reset
                    logger?.LogError("Stale frames in the stashed area, resetting");
                    nackOutstanding = null;
                    transportStashed.Clear();
                    SendAck(cancellationToken);
                }
            }
            else
            {
                SendAck(cancellationToken);
            }
        }


        private void ProcessUnexpectedReceivedTransportFrame(MINFrame frame, CancellationToken cancellationToken)
        {
            logger?.LogDebug("Unexpected MIN application frame received: id={id}, sequence={sequence})",
                receivingFrame.IDControl, receivingFrame.Sequence);

            // If the frames come within the window size in the future sequence range then we accept them and assume some were missing
            // (They may also be duplicates, in which case we store them over the top of the old ones)
            var ahead = unchecked((byte) (receivingFrame.Sequence - nextReceiveSequence));
            if (ahead < ReceiveWindowSize)
            {
                // We want to only NACK a range of frames once, not each time otherwise we will overload with retransmissions
                if (!nackOutstanding.HasValue)
                {
                    // If we are missing specific frames then send a NACK to specifically request them
                    SendNack(receivingFrame.Sequence, cancellationToken);
                    nackOutstanding = receivingFrame.Sequence;
                }
                else
                {
                    logger?.LogDebug("NACK already outstanding, skipping");
                }

                // Hang on to this frame because we will join it up later with the missing ones that are re-sent
                if (transportStashed.ContainsKey(receivingFrame.Sequence))
                    transportStashed[receivingFrame.Sequence] = frame;
                else
                    transportStashed.Add(receivingFrame.Sequence, frame);

                logger?.LogDebug("MIN application frame stashed: id={id}, sequence={sequence}",
                    receivingFrame.IDControl, receivingFrame.Sequence);
            }
            else
            {
                logger?.LogWarning("Frame stale? Discarding: id={id}, sequence={sequence})", receivingFrame.IDControl,
                    receivingFrame.Sequence);

                if (transportStashed.TryGetValue(receivingFrame.Sequence, out var stashedFrame) &&
                    !stashedFrame.Payload.SequenceEqual(receivingFrame.PayloadBuffer))
                    logger?.LogError("Inconsistency between new and stashed frame payloads");

                lock (statsLock)
                {
                    // Out of range (may be an old retransmit duplicate that we don't want) - throw it away
                    stats.SequenceMismatchDrops++;
                }
            }
        }


        private void InternalReset(bool resetUnsentQueue)
        {
            if (resetUnsentQueue)
                lock (transportQueueLock)
                {
                    transportQueue.Clear();
                    transportQueueEvent.Reset();
                }

            transportAwaitingAck.Clear();
            transportStashed.Clear();
            nextSendSequence = 0;
            nextReceiveSequence = 0;

            var now = timeProvider.Now();
            lastReceivedData = now;
            lastReceivedFrame = DateTime.MinValue;
            lastAck = now;

            lock (statsLock)
            {
                stats.LastSentFrame = DateTime.MinValue;
            }
        }

        private void FrameReceived(MINFrame frame)
        {
            lastReceivedFrame = timeProvider.Now();

            OnFrame?.Invoke(this, new MINFrameEventArgs(frame.Id, frame.Payload));
        }


        private QueuedFrame GetNextQueuedFrame()
        {
            lock (transportQueueLock)
            {
                // Frames still to send
                if (transportQueue.Count > 0)
                {
                    var queuedFrame = transportQueue[0];
                    transportQueue.RemoveAt(0);

                    if (transportQueue.Count == 0)
                        transportQueueEvent.Reset();

                    if (!queuedFrame.Frame.Transport)
                        return queuedFrame;


                    // Move to the awaiting ack queue
                    queuedFrame.Sequence = nextSendSequence;
                    unchecked
                    {
                        nextSendSequence++;
                    }

                    transportAwaitingAck.Add(queuedFrame);
                    return queuedFrame;
                }
            }

            // Maybe retransmits
            var oldestFrame = transportAwaitingAck.FirstOrDefault();
            return oldestFrame != null && timeProvider.Now() - oldestFrame.LastSentTime > FrameRetransmitTimeout
                ? oldestFrame
                : null;
        }


        private bool ProcessQueuedFrame(QueuedFrame queuedFrame, CancellationToken cancellationToken)
        {
            var now = timeProvider.Now();

            if (queuedFrame.Sent)
            {
                // Frame is being retransmitted, make sure we got at least some data back
                // recently to prevent flooding the output
                var remoteConnected = now - lastReceivedData < IdleTimeout;
                if (!remoteConnected)
                    return false;
            }
            else
            {
                queuedFrame.Sent = true;
            }

            queuedFrame.LastSentTime = now;
            lock (statsLock)
            {
                stats.LastSentFrame = now;
            }

            WriteFrameData(queuedFrame.Frame, queuedFrame.Sequence, cancellationToken);
            return true;
        }


        private void WriteFrameData(MINFrame frame, byte sequence, CancellationToken cancellationToken)
        {
            var data = MINFrameEncoder.GetFrameData(frame, sequence);
            if (LogDebugEnabled)
                logger.LogDebug("Sending MIN frame, on wire bytes={bytes}", BitConverter.ToString(data));

            transport.Write(data, cancellationToken);
        }


        private static void FrameSent(QueuedFrame queuedFrame)
        {
            if (queuedFrame.AwaitingAck)
                // Frame has already been moved to the transportAwaitingAck list
                return;

            if (queuedFrame.Frame.Transport)
                queuedFrame.AwaitingAck = true;
            else
                queuedFrame.TaskCompletionSource?.TrySetResult(true);
        }


        private void SendAck(CancellationToken cancellationToken)
        {
            logger?.LogDebug("Sending ACK: expected next sequence={sequence}", nextReceiveSequence);

            // For a regular ACK we request no additional retransmits
            WriteFrameData(
                new MINFrame(MINWire.Ack, new[] { nextReceiveSequence }, true),
                nextReceiveSequence,
                cancellationToken);

            lastAck = timeProvider.Now();
        }


        private void SendNack(byte to, CancellationToken cancellationToken)
        {
            logger?.LogDebug("Sending ACK: sequence={sequence}, to={to}", nextReceiveSequence, to);

            // For a NACK we send an ACK but also request some frame retransmits
            WriteFrameData(
                new MINFrame(MINWire.Ack, new[] { to }, true),
                nextReceiveSequence,
                cancellationToken);

            lastAck = timeProvider.Now();
        }


        private class QueuedFrame
        {
            public MINFrame Frame { get; }
            public TaskCompletionSource<bool> TaskCompletionSource { get; }

            public bool Sent { get; set; }
            public bool AwaitingAck { get; set; }

            public DateTime LastSentTime { get; set; }
            public byte Sequence { get; set; }

            public QueuedFrame(MINFrame frame, TaskCompletionSource<bool> taskCompletionSource = null)
            {
                Frame = frame;
                TaskCompletionSource = taskCompletionSource;
            }
        }
    }
}