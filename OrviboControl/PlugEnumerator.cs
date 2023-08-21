using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OrviboControl;

public sealed class PlugEnumerator : IDisposable
{
    private const int Port = 10000;
    private readonly UdpClient UdpClient;
    private readonly Thread ReceiveThread;
    private readonly EventWaitHandle QuitHandle = new(false, EventResetMode.ManualReset);

    private readonly IPEndPoint InterfaceEndpoint;
    private readonly IPEndPoint BroadcastEndpoint = new(IPAddress.Broadcast, Port);

    public delegate void PacketEvent(ReadOnlySpan<byte> packet);
    public event PacketEvent? PacketReceived;
    public event PacketEvent? PacketSent;

    private ImmutableDictionary<MacAddress, Plug> _Plugs = ImmutableDictionary<MacAddress, Plug>.Empty;
    public ImmutableDictionary<MacAddress, Plug> Plugs => _Plugs;
    public event Action? PlugsChanged;
    private readonly Stopwatch PlugsChangedDelayStopwatch = new();
    private readonly TimeSpan PlugsChangedDelay = TimeSpan.FromSeconds(1);

    public PlugEnumerator(IPAddress interfaceIp)
    {
        InterfaceEndpoint = new(interfaceIp, Port);
        UdpClient = new UdpClient(InterfaceEndpoint)
        {
            EnableBroadcast = true
        };

        ReceiveThread = new Thread(ReceiveThreadEntry)
        {
            Name = $"{nameof(PlugEnumerator)} UDP Receive Thread"
        };
        ReceiveThread.Start();
    }

    private static IPAddress FindDefaultIPAddress()
    {
        IPAddress? ipv6Candidate = null;

        // The default auto-select logic isn't ideal, so we do our own.
        // We prefer an interface which is up, has a default gateway, and an assigned IP address
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            { continue; }

            IPInterfaceProperties ipProperties = nic.GetIPProperties();

            if (ipProperties.GatewayAddresses.Count == 0)
            { continue; }

            if (ipProperties.UnicastAddresses.Count == 0)
            { continue; }

            // Prefer IPv4
            foreach (UnicastIPAddressInformation address in ipProperties.UnicastAddresses)
            {
                switch (address.Address.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        return address.Address;
                    case AddressFamily.InterNetworkV6 when ipv6Candidate is null:
                        ipv6Candidate = address.Address;
                        break;
                }
            }
        }

        // If we found an IPv6 candidate, allow that
        if (ipv6Candidate is not null)
        { return ipv6Candidate; }

        // Otherwise let Windows decide
        return IPAddress.Any;
    }

    public PlugEnumerator()
        : this(FindDefaultIPAddress())
    { }

    private void ReceiveThreadEntry()
    {
        bool myIpListIsStale = true;
        void NetworkAddressChanged(object? sender, EventArgs e)
            => myIpListIsStale = true;

        try
        {
            // Enumerate our own IP addresses so we can ignore our own broadcast packets
            // (We do this now since connecting to the device causes our configuration to change)
            List<IPAddress> myIps = new();
            NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
            EnumerateMyIps();

            void EnumerateMyIps()
            {
                myIps.Clear();
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                { myIps.AddRange(adapter.GetIPProperties().UnicastAddresses.Select(a => a.Address)); }

                myIpListIsStale = false;
            }

            while (true)
            {
                // Check if we should quit
                if (QuitHandle.WaitOne(0))
                { return; }

                // Check if we have a pending plugs changed event
                if (PlugsChangedDelayStopwatch.IsRunning && PlugsChangedDelayStopwatch.Elapsed >= PlugsChangedDelay)
                {
                    // Enable commands on all plugs
                    // We do this here instead of when we add a new plug because if we send the command while the plug is busy sending acknowledgements it will not be received
                    foreach (Plug plug in Plugs.Values)
                    { plug.EnableCommands(); }

                    PlugsChangedDelayStopwatch.Stop();
                    PlugsChanged?.Invoke();
                }

                // If there's no data available, sleep for a while and try again later
                if (UdpClient.Available == 0)
                {
                    QuitHandle.WaitOne(100);
                    continue;
                }

                // Receive data
                IPEndPoint? endPoint = null;
                byte[] packet = UdpClient.Receive(ref endPoint); // This is marked as ref but it's really out

                // Ignore packets from ourselves
                if (Volatile.Read(ref myIpListIsStale))
                { EnumerateMyIps(); }

                if (myIps.Contains(endPoint.Address))
                { continue; }

                // Handle the packet
                PacketReceived?.Invoke(packet);
                {
                    const int headerSize = sizeof(ushort) + sizeof(ushort) + sizeof(OrviboCommandId); // Magic + Message length + Command ID
                    if (packet.Length < headerSize)
                    {
                        Debug.WriteLine($"Ignoring invalid packet: Too short for header.");
                        continue;
                    }

                    // Magic (0x6864 big endian)
                    if (packet[0] != 0x68 || packet[1] != 0x64)
                    {
                        Debug.WriteLine($"Ignoring invalid packet: Invalid magic.");
                        continue;
                    }

                    // Message length (big endian)
                    ushort packetSize = (ushort)((packet[2] << 8) | packet[3]);
                    if (packet.Length != packetSize)
                    {
                        Debug.WriteLine($"Ignoring invalid packet: Invalid message length.");
                        continue;
                    }

                    // Command ID
                    OrviboCommandId commandId = (OrviboCommandId)((packet[4] << 8) | packet[5]);

                    // Command data
                    ReadOnlySpan<byte> data = packet;
                    data = data.Slice(headerSize);

                    HandlePacket(commandId, data);
                }
            }
        }
        finally
        { NetworkChange.NetworkAddressChanged -= NetworkAddressChanged; }
    }

    private void HandlePacket(OrviboCommandId commandId, ReadOnlySpan<byte> data)
    {
        Debug.WriteLine($"Handling {commandId}...");

        if (commandId == OrviboCommandId.DiscoverUnits)
        {
            const int expectedLength
                = 1 // Unknown
                + 6 // MAC address
                + 6 // Padding
                + 6 // MAC address (reversed)
                + 6 // Padding
                + 6 // Firmware version
                + 4 // Unknown
                + 1 // Current state
            ;
            if (data.Length != expectedLength)
            {
                Debug.WriteLine($"Ignoring malformed {commandId} packet.");
                return;
            }

            MacAddress macAddress = new(data.Slice(1, 6)); // 6 bytes MAC address
            Span<char> firmwareVersion = stackalloc char[6];
            Encoding.ASCII.GetChars(data.Slice(25, 6), firmwareVersion);
            bool isPowered = data[35] != 0;

            if (Plugs.TryGetValue(macAddress, out Plug? plug))
            {
                plug.LastSeen = DateTime.Now;
                if (!firmwareVersion.SequenceEqual(plug.FirmwareVersion))
                { plug.FirmwareVersion = firmwareVersion.ToString(); }
                plug.IsPowered = isPowered;
                PlugsChangedDelayStopwatch.Restart();
            }
            else
            {
                plug = new Plug(this, macAddress, firmwareVersion.ToString(), isPowered);

                if (!ImmutableInterlocked.TryAdd(ref _Plugs, macAddress, plug))
                { Debug.Fail("Only the receive thread should be adding new plugs!"); }

                PlugsChangedDelayStopwatch.Restart();
            }
        }
        else if (commandId == OrviboCommandId.PowerStatusChange || commandId == OrviboCommandId.SetPowerState)
        {
            const int expectedLength
                = 6 // MAC address
                + 6 // Padding
                + 4 // Unknown
                + 1 // Current state
            ;
            if (data.Length != expectedLength)
            {
                Debug.WriteLine($"Ignoring malformed {commandId} packet.");
                return;
            }

            MacAddress macAddress = new(data.Slice(0, 6));
            bool isPowered = data[16] != 0;

            if (Plugs.TryGetValue(macAddress, out Plug? plug))
            {
                plug.LastSeen = DateTime.Now;
                plug.IsPowered = isPowered;
                PlugsChangedDelayStopwatch.Restart();
            }
            else
            {
                // This is the first time we've seen this plug so we do a discovery request so we get all of its info
                DiscoverPlugs();
            }
        }
        else if (commandId == OrviboCommandId.UnlockCommands)
        { } // Note that UnlockCommands is *not* quite the same as PowerStatusChange, don't try to handle it above. The unknown portion if 5 bytes instead of 4.
        else
        { Debug.WriteLine($"Ignoring unrecognized command {commandId}."); }
    }

    internal void SendCommand(OrviboCommandId commandId, ReadOnlySpan<byte> data)
    {
        CheckDisposed();

        const int headerSize = sizeof(ushort) + sizeof(ushort) + sizeof(OrviboCommandId); // Magic + Message length + Command ID
        ushort packetSize = checked((ushort)(headerSize + data.Length));
        Span<byte> packet = packetSize < 4096 ? stackalloc byte[packetSize] : new byte[packetSize];

        // Magic (0x6864 big endian)
        packet[0] = 0x68;
        packet[1] = 0x64;

        // Message length (big endian)
        packet[2] = (byte)((packetSize >> 8) & 0xFF);
        packet[3] = (byte)(packetSize & 0xFF);

        // Command ID (big endian)
        ushort commandIdShort = (ushort)commandId;
        packet[4] = (byte)((commandIdShort >> 8) & 0xFF);
        packet[5] = (byte)(commandIdShort & 0xFF);

        // Command data
        data.CopyTo(packet.Slice(6));

        // Send the message
        UdpClient.Send(packet, BroadcastEndpoint);
        PacketSent?.Invoke(packet);
    }

    public void DiscoverPlugs()
        => SendCommand(OrviboCommandId.DiscoverUnits, Array.Empty<byte>());

    private bool Disposed = false;
    private void CheckDisposed()
    {
        if (Disposed)
        { throw new ObjectDisposedException(nameof(PlugEnumerator)); }
    }

    public void Dispose()
    {
        CheckDisposed();

        Disposed = true;
        QuitHandle.Set();
        ReceiveThread.Join();
        UnsafeEx.Clear(ReceiveThread);
        UnsafeEx.Clear(QuitHandle);
        UdpClient.Dispose();
        UnsafeEx.Clear(UdpClient);
    }
}
