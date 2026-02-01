using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ANT_Managed_Library;

namespace AntDashboard
{
    internal sealed class AntManager
    {
        private const byte NetworkNumber = 0;
        private static readonly byte[] NetworkKey = { 0xB9, 0xA5, 0x21, 0xFB, 0xBD, 0x72, 0xC3, 0x45 };
        private const byte DeviceTypeHeartRate = 0x78; // 120
        private const byte DeviceTypePower = 0x0B; // 11
        private const ushort HeartRatePeriod = 8070;
        private const ushort PowerPeriod = 8182;
        private const byte RfFrequency = 57;
        private const byte PairingBit = 0x80;

        private readonly object _sync = new object();
        private readonly RiderState[] _riders;
        private readonly Dictionary<byte, ChannelDefinition> _channelsByNumber = new Dictionary<byte, ChannelDefinition>();
        private readonly Dictionary<ushort, int> _hrDeviceMap = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, int> _powerDeviceMap = new Dictionary<ushort, int>();

        private ANT_Device _device;

        public AntManager(int riderCount)
        {
            _riders = Enumerable.Range(1, riderCount).Select(index => new RiderState(index)).ToArray();
        }

        public RiderState[] GetSnapshot()
        {
            lock (_sync)
            {
                return _riders.Select(CloneRider).ToArray();
            }
        }

        public void Start()
        {
            _device = new ANT_Device();

            // API naming differs across ANT_NET builds; call via reflection when needed.
            AntReflection.TryInvoke(_device, new[] { "resetSystem", "ResetSystem" });
            AntReflection.TryInvoke(_device, new[] { "setNetworkKey", "SetNetworkKey" }, NetworkNumber, NetworkKey);

            InitializeChannels(DeviceTypeHeartRate, HeartRatePeriod, isPower: false, startChannelNumber: 0, channelsNeeded: _riders.Length);
            InitializeChannels(DeviceTypePower, PowerPeriod, isPower: true, startChannelNumber: (byte)_riders.Length, channelsNeeded: _riders.Length);
        }

        public void ResetPairing()
        {
            lock (_sync)
            {
                foreach (var rider in _riders)
                {
                    rider.Reset();
                }

                _hrDeviceMap.Clear();
                _powerDeviceMap.Clear();
            }

            foreach (var definition in _channelsByNumber.Values)
            {
                ReconfigureChannel(definition);
            }

            Console.WriteLine("Pairing reset: channels re-opened with wildcard device numbers.");
        }

        private void InitializeChannels(byte deviceType, ushort period, bool isPower, byte startChannelNumber, int channelsNeeded)
        {
            for (byte i = 0; i < channelsNeeded; i++)
            {
                var channelNumber = (byte)(startChannelNumber + i);
                var channel = AntReflection.GetChannel(_device, channelNumber);
                var definition = new ChannelDefinition(channelNumber, channel, deviceType, period, isPower);

                _channelsByNumber[channelNumber] = definition;
                ReconfigureChannel(definition);
                AntReflection.AttachChannelResponse(channel, OnChannelResponse);
            }
        }

        private void ReconfigureChannel(ChannelDefinition definition)
        {
            AntReflection.TryInvoke(definition.Channel, new[] { "closeChannel", "CloseChannel" });
            AntReflection.TryInvoke(definition.Channel, new[] { "unassignChannel", "UnassignChannel" });

            // Channel type uses BASE_Slave_Receive (0x00). Use byte to avoid enum mismatches.
            AntReflection.TryInvoke(definition.Channel, new[] { "assignChannel", "AssignChannel" }, (byte)0x00, NetworkNumber);

            // Pairing: wildcard device number (0) + pairing bit in transmission type.
            var transmissionType = (byte)(0x00 | PairingBit);
            AntReflection.TryInvoke(definition.Channel, new[] { "setChannelId", "SetChannelId", "setChannelID", "SetChannelID" }, (ushort)0, definition.DeviceType, transmissionType);
            AntReflection.TryInvoke(definition.Channel, new[] { "setChannelPeriod", "SetChannelPeriod" }, definition.Period);

            if (!AntReflection.SetChannelRfFrequency(definition.Channel, RfFrequency))
            {
                Console.WriteLine("RF frequency setter not found; inspect ANT_Channel methods in logs.");
            }

            AntReflection.TryInvoke(definition.Channel, new[] { "openChannel", "OpenChannel" });
        }

        private void OnChannelResponse(ANT_Response response)
        {
            var messageId = AntReflection.TryGetMessageId(response);
            if (messageId.HasValue && messageId != 0x4E && messageId != 0x4F)
            {
                return;
            }

            var data = AntReflection.TryGetResponseData(response);
            if (data == null || data.Length < 9)
            {
                return;
            }

            var channelNumber = data[0];
            if (!_channelsByNumber.TryGetValue(channelNumber, out var definition))
            {
                return;
            }

            var payload = new byte[8];
            Array.Copy(data, 1, payload, 0, 8);

            var deviceNumber = AntReflection.TryGetDeviceNumber(definition.Channel);
            var riderIndex = GetOrAssignRider(definition, deviceNumber);
            if (riderIndex < 0)
            {
                return;
            }

            UpdateRider(definition, riderIndex, deviceNumber, payload);

#if DEBUG
            var page = payload[0];
            Console.WriteLine($"[ANT] Ch {channelNumber} Dev {deviceNumber} Page 0x{page:X2} Data {BitConverter.ToString(payload)}");
#endif
        }

        private int GetOrAssignRider(ChannelDefinition definition, ushort deviceNumber)
        {
            lock (_sync)
            {
                if (deviceNumber != 0)
                {
                    if (definition.IsPower && _powerDeviceMap.TryGetValue(deviceNumber, out var riderIndex))
                    {
                        return riderIndex;
                    }

                    if (!definition.IsPower && _hrDeviceMap.TryGetValue(deviceNumber, out riderIndex))
                    {
                        return riderIndex;
                    }
                }

                var available = _riders.FirstOrDefault(r => definition.IsPower ? r.PowerDeviceNumber == 0 : r.HrDeviceNumber == 0);
                if (available == null)
                {
                    return -1;
                }

                if (definition.IsPower)
                {
                    available.PowerDeviceNumber = deviceNumber;
                    if (deviceNumber != 0)
                    {
                        _powerDeviceMap[deviceNumber] = available.RiderNumber - 1;
                    }
                }
                else
                {
                    available.HrDeviceNumber = deviceNumber;
                    if (deviceNumber != 0)
                    {
                        _hrDeviceMap[deviceNumber] = available.RiderNumber - 1;
                    }
                }

                return available.RiderNumber - 1;
            }
        }

        private void UpdateRider(ChannelDefinition definition, int riderIndex, ushort deviceNumber, byte[] payload)
        {
            lock (_sync)
            {
                var rider = _riders[riderIndex];
                if (definition.IsPower)
                {
                    if (rider.PowerDeviceNumber == 0)
                    {
                        rider.PowerDeviceNumber = deviceNumber;
                    }

                    Decoders.DecodePower(payload, out var power, out var cadence);
                    rider.Power = power;
                    rider.Cadence = cadence;
                }
                else
                {
                    if (rider.HrDeviceNumber == 0)
                    {
                        rider.HrDeviceNumber = deviceNumber;
                    }

                    rider.HeartRate = Decoders.DecodeHeartRate(payload);
                }

                rider.LastSeenUtc = DateTime.UtcNow;
            }
        }

        private static RiderState CloneRider(RiderState rider)
        {
            return new RiderState(rider.RiderNumber)
            {
                HrDeviceNumber = rider.HrDeviceNumber,
                PowerDeviceNumber = rider.PowerDeviceNumber,
                HeartRate = rider.HeartRate,
                Power = rider.Power,
                Cadence = rider.Cadence,
                LastSeenUtc = rider.LastSeenUtc
            };
        }

        private sealed class ChannelDefinition
        {
            public byte ChannelNumber { get; }
            public ANT_Channel Channel { get; }
            public byte DeviceType { get; }
            public ushort Period { get; }
            public bool IsPower { get; }

            public ChannelDefinition(byte channelNumber, ANT_Channel channel, byte deviceType, ushort period, bool isPower)
            {
                ChannelNumber = channelNumber;
                Channel = channel;
                DeviceType = deviceType;
                Period = period;
                IsPower = isPower;
            }
        }

        private static class AntReflection
        {
            public static ANT_Channel GetChannel(ANT_Device device, byte channelNumber)
            {
                var method = device.GetType().GetMethod("getChannel") ?? device.GetType().GetMethod("GetChannel");
                if (method == null)
                {
                    throw new InvalidOperationException("ANT_Device.getChannel not found.");
                }

                return (ANT_Channel)method.Invoke(device, new object[] { channelNumber });
            }

            public static void TryInvoke(object target, string[] methodNames, params object[] args)
            {
                var method = FindMethod(target, methodNames, args);
                if (method == null)
                {
                    Console.WriteLine($"Method not found on {target.GetType().Name}: {string.Join(", ", methodNames)}");
                    return;
                }

                method.Invoke(target, args);
            }

            public static bool SetChannelRfFrequency(ANT_Channel channel, byte rfFrequency)
            {
                var method = FindMethod(channel, new[] { "setChannelRFFrequency", "setChannelRFFreq", "SetChannelRFFrequency", "SetChannelRFFreq" }, rfFrequency);
                if (method == null)
                {
                    LogAvailableMethods(channel, "RF frequency setter not found on ANT_Channel. Available methods:");
                    return false;
                }

                method.Invoke(channel, new object[] { rfFrequency });
                return true;
            }

            public static void AttachChannelResponse(ANT_Channel channel, Action<ANT_Response> handler)
            {
                var eventNames = new[] { "channelResponse", "ChannelResponse" };
                var eventInfo = eventNames.Select(name => channel.GetType().GetEvent(name)).FirstOrDefault(info => info != null);
                if (eventInfo == null)
                {
                    Console.WriteLine("channelResponse event not found on ANT_Channel.");
                    LogAvailableEvents(channel);
                    return;
                }

                var delegateInstance = Delegate.CreateDelegate(eventInfo.EventHandlerType, handler.Target, handler.Method);
                eventInfo.AddEventHandler(channel, delegateInstance);
            }

            public static byte[] TryGetResponseData(ANT_Response response)
            {
                var methodNames = new[] { "getData", "GetData", "getDataPayload", "GetDataPayload", "getPayload", "GetPayload" };
                var method = FindMethod(response, methodNames, Array.Empty<object>());
                if (method != null)
                {
                    return method.Invoke(response, null) as byte[];
                }

                var property = response.GetType().GetProperty("data") ?? response.GetType().GetProperty("Data");
                if (property != null)
                {
                    return property.GetValue(response, null) as byte[];
                }

                return null;
            }

            public static byte? TryGetMessageId(ANT_Response response)
            {
                var property = response.GetType().GetProperty("messageID") ?? response.GetType().GetProperty("MessageID") ?? response.GetType().GetProperty("messageId") ?? response.GetType().GetProperty("MessageId");
                if (property == null)
                {
                    return null;
                }

                var value = property.GetValue(response, null);
                if (value == null)
                {
                    return null;
                }

                if (value is byte byteValue)
                {
                    return byteValue;
                }

                if (value.GetType().IsEnum)
                {
                    return Convert.ToByte(value);
                }

                return null;
            }

            public static ushort TryGetDeviceNumber(ANT_Channel channel)
            {
                var method = channel.GetType().GetMethod("getChannelId") ?? channel.GetType().GetMethod("GetChannelId") ??
                             channel.GetType().GetMethod("getChannelID") ?? channel.GetType().GetMethod("GetChannelID");
                if (method == null)
                {
                    return 0;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 3 && parameters.All(p => p.ParameterType.IsByRef))
                {
                    object[] args = { (ushort)0, (byte)0, (byte)0 };
                    method.Invoke(channel, args);
                    return (ushort)args[0];
                }

                return 0;
            }

            private static MethodInfo FindMethod(object target, string[] methodNames, params object[] args)
            {
                var argTypes = args.Select(arg => arg?.GetType()).ToArray();
                foreach (var name in methodNames)
                {
                    var methods = target.GetType().GetMethods().Where(m => m.Name == name).ToArray();
                    foreach (var method in methods)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length != argTypes.Length)
                        {
                            continue;
                        }

                        var match = true;
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            if (argTypes[i] == null)
                            {
                                continue;
                            }

                            if (!parameters[i].ParameterType.IsAssignableFrom(argTypes[i]))
                            {
                                match = false;
                                break;
                            }
                        }

                        if (match)
                        {
                            return method;
                        }
                    }
                }

                return null;
            }

            private static void LogAvailableMethods(object target, string header)
            {
                Console.WriteLine(header);
                foreach (var method in target.GetType().GetMethods().OrderBy(m => m.Name))
                {
                    Console.WriteLine($" - {method.Name}");
                }
            }

            private static void LogAvailableEvents(object target)
            {
                Console.WriteLine("Available events:");
                foreach (var evt in target.GetType().GetEvents().OrderBy(e => e.Name))
                {
                    Console.WriteLine($" - {evt.Name}");
                }
            }
        }
    }
}
