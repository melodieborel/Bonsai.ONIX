﻿namespace Bonsai.ONIX
{
    public static class DS90UB9xConfiguration
    {
        public enum Register
        {
            //Managed register map
            ENABLE = 0x00008000,
            READSZ = 0x00008001,
            TRIGGER = 0x00008002,
            TRIGGER_OFFSET = 0x00008003,
            PIXEL_GATE = 0x00008004,
            SYNC_BITS = 0x00008005,
            GPIO_DIR = 0x00008006,
            GPIO_VALUE = 0x00008007
        }

        public enum TriggerMode
        {
            CONTINUOUS = 0,
            HSYNC_EDGE_POSITIVE = 0b0001,
            HSYNC_EDGE_NEGATIVE = 0b1001,
            HSYNC_LEVEL_POSITIVE = 0b0101,
            HSYNC_LEVEL_NEGATIVE = 0b1101,
            VSYNC_EDGE_POSITIVE = 0b0011,
            VSYNC_EDGE_NEGATIVE = 0b1011,
            VSYNC_LEVEL_POSITIVE = 0b0111,
            VSYNC_LEVEL_NEGATIVE = 0b1111,
        }

        public enum PixelGate
        {
            DISABLED = 0,
            HSYNC_POSITIVE = 0b001,
            HSYNC_NEGATIVE = 0b101,
            VSYNC_POSITIVE = 0b011,
            VSYNC_NEGATIVE = 0b111
        }

        public const uint DeserializerDefaultAddress = 0x30;
        public const uint SerializerDefaultAddress = 0x58;
        
        public enum DeserializerRegister
        {
            //i2c registers of the deserializer. Only one value used now, might be filled in the future with common-use values for ease of configuration
            PORT_MODE = 0x6D
        }

        public enum DeserializerModes
        {
            RAW12BITLF = 1,
            RAW12BITHF = 2,
            RAW10BIT = 3
        }

    }
}
