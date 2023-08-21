namespace OrviboControl;

internal enum OrviboCommandId : ushort
{
    /// <summary>
    /// Requests all plugs on the network send back their information.
    /// </summary>
    /// <remarks>
    /// Restricted: No
    /// Destination: Broadcast
    /// 
    /// Data C->P:
    ///     None
    /// 
    /// Data P->C:
    ///     6 bytes MAC address
    ///     6 bytes 0x20 padding
    ///     6 bytes MAC address reversed
    ///     6 bytes 0x20 padding
    ///     6 bytes "SOC***" string, likely firmware version
    ///     4 bytes UNKNOWN
    ///     1 byte  Current state (boolean off/on)
    ///     
    /// Note: The plug will send these on its own occasionally, probably to
    ///    allow the app to discover it without broadcasting a discovery packet.
    /// 
    /// Note: 10 responses are sent for some reason.
    /// </remarks>
    DiscoverUnits = 0x7161,

    /// <summary>
    /// Requests a plug with a specific MAC address to send back their information.
    /// </summary>
    /// <remarks>
    /// Restricted: No
    /// Destination: Broadcast
    /// Data C->P:
    ///     6 bytes MAC address
    ///     6 bytes 0x20 padding
    /// 
    /// Data P->C:
    ///     See <see cref="DiscoverUnits"/> P->C data.
    /// </remarks>
    RequestStatus = 0x7167,

    /// <summary>
    /// Enables the destination plug to receive subsquent restricted commands.
    /// </summary>
    /// <remarks>
    /// Restricted: No
    /// Destination: Specific
    ///     (Can actually be broadcast.)
    /// 
    /// Data C->P:
    ///     6 bytes Target MAC address
    ///     6 bytes 0x20 padding
    ///     6 bytes Target MAC address reversed
    ///     6 bytes 0x20 padding
    /// 
    /// Data P->C:
    ///     6 bytes MAC address
    ///     6 bytes 0x20 padding
    ///     5 bytes UNKNOWN
    ///     1 byte  Current state (boolean off/on)
    /// </remarks>
    UnlockCommands = 0x636C,

    /// <summary>
    /// Sets the power state of the plug.
    /// </summary>
    /// <remarks>
    /// Restricted: Yes
    /// Destination: Specific
    ///     (Can actually be broadcast.)
    /// 
    /// Data C->P:
    ///     6 bytes Target MAC address
    ///     6 bytes 0x20 padding
    ///     4 bytes 0x00 UNKNOWN
    ///     1 byte  New state (boolean off/on)
    /// 
    /// Data P->C:
    ///     The plug will send the same data back.
    ///     Additionally, two <see cref="PowerStatusChange"/> packets will be sent back as well.
    /// </remarks>
    SetPowerState = 0x6463,

    /// <summary>
    /// Reports the power state of the plug.
    /// </summary>
    /// <remarks>
    /// Restricted: N/A
    /// Destination: N/A
    /// 
    /// Data C->P:
    ///     The controller is not expected to send this command.
    /// 
    /// Data P->C:
    ///     See <see cref="SetPowerState"/> C->P data.
    /// 
    /// Note: For some reason the plug sends two of these packets back whent he power state changes.
    ///     This packet is definitely sent when the state is changed via <see cref="SetPowerState"/> and when the button is pressed. Not sure about scheduled state changes.
    ///     However, it is only sent when a valid <see cref="UnlockCommands"/> has been sent recently(?).
    /// </remarks>
    PowerStatusChange = 0x7366,
}
