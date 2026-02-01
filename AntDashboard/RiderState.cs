using System;

namespace AntDashboard
{
    internal sealed class RiderState
    {
        public int RiderNumber { get; }
        public ushort HrDeviceNumber { get; set; }
        public ushort PowerDeviceNumber { get; set; }
        public int HeartRate { get; set; }
        public int Power { get; set; }
        public int Cadence { get; set; }
        public DateTime LastSeenUtc { get; set; }

        public RiderState(int riderNumber)
        {
            RiderNumber = riderNumber;
            Reset();
        }

        public void Reset()
        {
            HrDeviceNumber = 0;
            PowerDeviceNumber = 0;
            HeartRate = 0;
            Power = 0;
            Cadence = -1;
            LastSeenUtc = DateTime.MinValue;
        }

        public int LastSeenMs()
        {
            if (LastSeenUtc == DateTime.MinValue)
            {
                return int.MaxValue;
            }

            var elapsed = DateTime.UtcNow - LastSeenUtc;
            return (int)Math.Max(0, elapsed.TotalMilliseconds);
        }
    }
}
