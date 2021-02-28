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

            // ComputeAndWriteToEnd writes it in the reverse order as expected by the target, do it ourselves
            var checksum = Crc32Algorithm.Compute(frameData, 0, frameData.Length - 4);
            var offset = frameData.Length - 4;

            frameData[offset] = (byte)(checksum >> 24);
            frameData[offset + 1] = (byte)(checksum >> 16);
            frameData[offset + 2] = (byte)(checksum >> 8);
            frameData[offset + 3] = (byte)checksum;
            

            // Header + Payload with room for stuffing and a bit more (512 bytes at most, so no need to optimize) + Eof
            var stuffed = new MemoryStream(3 + frameData.Length * 2 + 1);
            stuffed.Write(new[] { MINWire.HeaderByte, MINWire.HeaderByte, MINWire.HeaderByte }, 0, 3);
            var headerByteCount = 0;

            foreach (var frameByte in frameData)
            {
                stuffed.WriteByte(frameByte);

                if (frameByte == MINWire.HeaderByte)
                {
                    headerByteCount++;

                    // ReSharper disable once InvertIf - reads more clearly this way
                    if (headerByteCount == 2)
                    {
                        stuffed.WriteByte(MINWire.StuffByte);
                        headerByteCount = 0;
                    }
                }
                else
                    headerByteCount = 0;
            }

            stuffed.WriteByte(MINWire.EofByte);
            return stuffed.ToArray();
        }

    }
}
