using System;

namespace AntDashboard
{
    internal static class Decoders
    {
        public static int DecodeHeartRate(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                return 0;
            }

            return payload[7];
        }

        public static void DecodePower(byte[] payload, out int powerWatts, out int cadence)
        {
            powerWatts = 0;
            cadence = -1;

            if (payload == null || payload.Length < 8)
            {
                return;
            }

            var page = payload[0];
            var instantPower = (payload[5] << 8) | payload[4];
            powerWatts = instantPower;

            switch (page)
            {
                case 0x10: // Standard Power-Only
                    cadence = payload[6] == 0xFF ? -1 : payload[6];
                    break;
                case 0x11: // Wheel Torque
                case 0x12: // Crank Torque
                    cadence = payload[3] == 0xFF ? -1 : payload[3];
                    break;
                default:
                    cadence = -1;
                    break;
            }
        }
    }
}
