using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using MIN.Abstractions;

namespace MIN.SerialPort
{
    /// <summary>
    /// Implements the MIN transport for a SerialPort connection.
    /// </summary>
    // ReSharper disable once UnusedMember.Global - public API
    public class MINSerialTransport : IMINAwaitableTransport
    {
        private System.IO.Ports.SerialPort serialPort;
        private readonly Func<System.IO.Ports.SerialPort> serialPortFactory;
        private readonly ManualResetEventSlim dataAvailableEvent = new ManualResetEventSlim();

        /// <inheritdoc />
        public event EventHandler OnDisconnected;

        
        /// <summary>
        /// Initializes a new instance of the MIN serial transport.
        /// </summary>
        /// <param name="portName">See <see cref="System.IO.Ports.SerialPort.PortName"/></param>
        /// <param name="baudRate">See <see cref="System.IO.Ports.SerialPort.BaudRate"/></param>
        /// <param name="parity">See <see cref="System.IO.Ports.SerialPort.Parity"/></param>
        /// <param name="dataBits">See <see cref="System.IO.Ports.SerialPort.DataBits"/></param>
        /// <param name="stopBits">See <see cref="System.IO.Ports.SerialPort.StopBits"/></param>
        /// <param name="handshake">See <see cref="System.IO.Ports.SerialPort.Handshake"/></param>
        /// <param name="dtrEnable">See <see cref="System.IO.Ports.SerialPort.DtrEnable"/></param>
        public MINSerialTransport(string portName, int baudRate, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One, 
            Handshake handshake = Handshake.None, bool dtrEnable = false)
        {
            serialPortFactory = () => new System.IO.Ports.SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                Handshake = handshake,
                DtrEnable = dtrEnable
            };
        }
            
        
        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                serialPort?.Dispose();
            }
            catch
            {
                // Ignored
            }
        }


        /// <inheritdoc />
        public void Connect(CancellationToken cancellationToken)
        {
            dataAvailableEvent.Reset();
            
            
            while ((serialPort == null || !serialPort.IsOpen) && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (serialPort == null)
                        serialPort = serialPortFactory();

                    Debug.Assert(serialPort != null);
                    serialPort.Open();

                    serialPort.DataReceived += (sender, args) =>
                    {
                        dataAvailableEvent.Set();
                    };
                    
                    serialPort.ErrorReceived += (sender, args) =>
                    {
                        Disconnect();
                    };
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
        }


        private void Disconnect()
        {
            try
            {
                serialPort?.Dispose();
            }
            catch
            {
                // Ignored
            }

            serialPort = null;
            dataAvailableEvent.Reset();

            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }


        /// <inheritdoc />
        public void Reset()
        {
            if (serialPort != null && serialPort.IsOpen)
                serialPort.DiscardInBuffer();
        }


        /// <inheritdoc />
        public void Write(byte[] data, CancellationToken cancellationToken)
        {
            if (serialPort == null || !serialPort.IsOpen)
                return;

            try
            {
                serialPort.Write(data, 0, data.Length);
            }
            catch
            {
                Disconnect();
            }
        }


        /// <inheritdoc />
        public byte[] ReadAll()
        {
            if (serialPort == null || !serialPort.IsOpen)
                return Array.Empty<byte>();

            var bytesToRead = serialPort.BytesToRead;
            if (bytesToRead == 0)
                return Array.Empty<byte>();

            try
            {
                var data = new byte[bytesToRead];
                if (serialPort.Read(data, 0, bytesToRead) < bytesToRead)
                    throw new IOException("SerialPort lied about the available BytesToRead");

                if (serialPort.BytesToRead == 0)
                    dataAvailableEvent.Reset();

                return data;
            }
            catch
            {
                Disconnect();
                return Array.Empty<byte>();
            }
        }


        /// <inheritdoc />
        public WaitHandle DataAvailable()
        {
            return dataAvailableEvent.WaitHandle;
        }
    }
}
