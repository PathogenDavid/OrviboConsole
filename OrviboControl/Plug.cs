using System;
using System.Threading;

namespace OrviboControl;

public sealed class Plug
{
    private PlugEnumerator Parent;
    public MacAddress MacAddress { get; }
    public string FirmwareVersion { get; internal set; }
    public bool IsPowered { get; internal set; }
    public DateTime LastSeen { get; internal set; }
    private DateTime LastEnabled { get; set; } = DateTime.MinValue;

    internal Plug(PlugEnumerator parent, MacAddress macAddress, string firmwareVersion, bool isPowered)
    {
        Parent = parent;
        MacAddress = macAddress;
        FirmwareVersion = firmwareVersion;
        IsPowered = isPowered;
        LastSeen = DateTime.Now;
    }

    internal void EnableCommands()
    {
        const int packetLength
            = 6 // Target MAC address
            + 6 // 0x20 padding
            + 6 // Target MAC address (reversed)
            + 6 // 0x20 padding
        ;
        Span<byte> packet = stackalloc byte[packetLength];
        MacAddress.CopyTo(packet);
        packet.Slice(6, 6).Fill(0x20);
        packet.Slice(0, 12).CopyTo(packet.Slice(12));
        packet.Slice(12, 6).Reverse();

        Parent.SendCommand(OrviboCommandId.UnlockCommands, packet);
        LastEnabled = DateTime.Now;
    }

    public void SetPowerState(bool state)
    {
        EnableCommands();
        // The plug occastionally seems to have issues with being toggled immediately after enablement.
        // Could potentially just wait for the acknowledgement of the unlock command instead
        Thread.Sleep(100);

        const int packetLength
            = 6 // Target MAC address
            + 6 // 0x20 padding
            + 4 // 0x00 Unknown
            + 1 // New state
        ;
        Span<byte> packet = stackalloc byte[packetLength];
        MacAddress.CopyTo(packet);
        packet.Slice(6, 6).Fill(0x20);
        packet[16] = (byte)(state ? 1 : 0);

        Parent.SendCommand(OrviboCommandId.SetPowerState, packet);
    }
}
