using System;
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
    public class MINSerialTransport : IMINTransport
    {
        private readonly System.IO.Ports.SerialPort serialPort;

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
            serialPort = new System.IO.Ports.SerialPort(portName, baudRate, parity, dataBits, stopBits)
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
                serialPort.Dispose();
            }
            catch
            {
                // Ignored
            }
        }


        /// <inheritdoc />
        public void Connect(CancellationToken cancellationToken)
        {
            // TODO retry
            serialPort.Open();
            
            // TODO detect disconnects and report back
        }

        
        /// <inheritdoc />
        public void Write(byte[] data, CancellationToken cancellationToken)
        {
            if (!serialPort.IsOpen)
                return;
            
            serialPort.Write(data, 0, data.Length);
        }


        /// <inheritdoc />
        public byte[] ReadAll()
        {
            if (!serialPort.IsOpen)
                return Array.Empty<byte>();
            
            var bytesToRead = serialPort.BytesToRead;
            if (bytesToRead == 0)
                return Array.Empty<byte>();

            var data = new byte[bytesToRead];
            if (serialPort.Read(data, 0, bytesToRead) < bytesToRead)
                throw new IOException("SerialPort lied about the available BytesToRead");

            return data;
        }
    }
}
