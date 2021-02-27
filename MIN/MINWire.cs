namespace MIN
{
    /// <summary>
    /// Constants for the values sent over the MIN transport.
    /// </summary>
    public static class MINWire
    {
        #pragma warning disable 1591
        public const byte Ack = 0xff;
        public const byte Reset = 0xfe;

        public const byte HeaderByte = 0xaa;
        public const byte StuffByte = 0x55;
        public const byte EofByte = 0x55;


        public const byte IDControlTransportMask = 0x80;
        public const byte IDMask = 0x3f;
        #pragma warning restore 1591
    }

}
