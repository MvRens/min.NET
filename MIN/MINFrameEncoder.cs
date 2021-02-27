using System;
using System.IO;
using Force.Crc32;

namespace MIN
{
    /// <summary>
    /// Helper class for encoding a MIN frame into it's equivelant on-wire bytes. Separated from MINProtocol for unit testing purposes.
    /// </summary>
    public static class MINFrameEncoder
    {
        /// <summary>
        /// Get the on-wire byte sequence for the frame, including stuff bytes after every 0xaa 0xaa pair
        /// </summary>
        public static byte[] GetFrameData(MINFrame frame, byte sequence)
        {
            byte[] frameData;

            if (frame.Transport)
            {
                frameData = new byte[3 + frame.Payload.Length + 4];
                frameData[0] = (byte)(frame.Id | MINWire.IDControlTransportMask);
                frameData[1] = sequence;
                frameData[2] = (byte)frame.Payload.Length;
                Buffer.BlockCopy(frame.Payload, 0, frameData, 3, frame.Payload.Length);
            }
            else
            {
                frameData = new byte[2 + frame.Payload.Length + 4];
                frameData[0] = frame.Id;
                frameData[1] = (byte)frame.Payload.Length;
                Buffer.BlockCopy(frame.Payload, 0, frameData, 2, frame.Payload.Length);
            }

            Crc32Algorithm.ComputeAndWriteToEnd(frameData);

            // Header + Payload with room for stuffing and a bit more (512 bytes at most, so no need to optimize) + Eof
            var stuffed = new MemoryStream(4 + frame.Payload.Length * 2);
            stuffed.Write(new[] { MINWire.HeaderByte, MINWire.HeaderByte, MINWire.HeaderByte }, 0, 3);
            var count = 0;

            foreach (var frameByte in frameData)
            {
                stuffed.WriteByte(frameByte);

                if (frameByte == MINWire.HeaderByte)
                {
                    count++;

                    // ReSharper disable once InvertIf - reads more clearly this way
                    if (count == 2)
                    {
                        stuffed.WriteByte(MINWire.StuffByte);
                        count = 0;
                    }
                }
                else
                    count = 0;
            }

            stuffed.WriteByte(MINWire.EofByte);
            return stuffed.ToArray();
        }

    }
}
