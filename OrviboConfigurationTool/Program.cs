// The operation of this tool is based on the reverse engineering efforts done by Andrius Štikonas documented here:
// https://stikonas.eu/wordpress/2015/02/24/reverse-engineering-orvibo-s20-socket/
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

Console.WriteLine("This app will assist you in joining an unprovisioned Orvibo S20 to the network.");

static void PurgeConsoleInputBuffer()
{
    while (Console.KeyAvailable)
    { Console.ReadKey(intercept: true); }
}

static bool YesNoPrompt(string prompt)
{
    Console.Write($"{prompt} (Y/N) ");

    PurgeConsoleInputBuffer();
    while (true)
    {
        switch (Console.ReadKey(intercept: true).Key)
        {
            case ConsoleKey.Y:
                Console.WriteLine('Y');
                return true;
            case ConsoleKey.N:
                Console.WriteLine('N');
                return false;
            default:
                Console.Beep();
                continue;
        }
    }
}

static bool IsValidParameter(string parameter)
{
    foreach (char c in parameter)
    {
        if (!Char.IsAscii(c))
        { return false; }

        // This is probably overly restrictive, but I don't want to deal with figuring out whether we need to escape special characters
        if (!Char.IsLetterOrDigit(c))
        { return false; }

    }

    return true;
}

// Get the WiFi AP credentials
const string savedCredentialsFileName = "SavedCredentials.txt";
string? apName = null;
string? apPassword = null;
const string apEncryption = "WPA2PSK,AES";

if (File.Exists(savedCredentialsFileName))
{
    try
    {
        using StreamReader file = File.OpenText(savedCredentialsFileName);
        apName = file.ReadLine()?.Trim();
        apPassword = file.ReadLine()?.Trim();
    }
    catch (Exception ex)
    { Console.Error.WriteLine($"Failed to load saved credentials: {ex}"); }

    // Validate saved parameters
    if (apName is not null && !IsValidParameter(apName))
    { apName = null; }

    if (apPassword is not null && !IsValidParameter(apPassword))
    { apPassword = null; }

    // If any parameter is missing, clear all of them
    if (apName is null || apPassword is null)
    {
        apName = null;
        apPassword = null;
    }
}

while (true)
{
    if (apName is not null && apPassword is not null)
    {
        Console.WriteLine();
        Console.WriteLine("WiFi configuration:");
        Console.WriteLine($"        SSID: {apName}");
        Console.WriteLine($"    Password: {apPassword}");
        Console.WriteLine();

        if (YesNoPrompt("Everything look correct?"))
        { break; }
        else
        {
            apName = null;
            apPassword = null;
        }
    }

    Console.WriteLine();

    while (apName is null)
    {
        Console.Write("Enter the SSID to connect to: ");
        apName = Console.ReadLine()?.Trim();

        if (apName is null)
        {
            Console.Error.WriteLine("STDIN ended.");
            return 1;
        }
        else if (!IsValidParameter(apName))
        { Console.Error.WriteLine("Invalid/unsupported SSID."); }
    }

    while (apPassword is null)
    {
        Console.Write("Enter the password to authenticate with: ");
        apPassword = Console.ReadLine()?.Trim();

        if (apPassword is null)
        {
            Console.Error.WriteLine("STDIN ended.");
            return 1;
        }
        else if (!IsValidParameter(apPassword))
        { Console.Error.WriteLine("Invalid/unsupported password."); }
    }

    Console.WriteLine();
}

// Save the configuration
try
{ File.WriteAllText(savedCredentialsFileName, $"{apName}\n{apPassword}"); }
catch (Exception ex)
{ Console.Error.WriteLine($"Failed to cache saved credentials: {ex}"); }

// Configuration loop setup
Encoding asciiEncoding = (Encoding)Encoding.ASCII.Clone();
asciiEncoding.EncoderFallback = EncoderFallback.ExceptionFallback;
asciiEncoding.DecoderFallback = DecoderFallback.ExceptionFallback;

const int configurationPort = 48899;
IPAddress listenIpAddress = new IPAddress(new byte[] { 10, 10, 100, 150 }); // This is the IP address that the DHCP server of the plug assigns to use when we connect
IPEndPoint listenEndPoint = new(listenIpAddress, configurationPort);
IPEndPoint broadcastEndPoint = new(IPAddress.Broadcast, configurationPort);

Stopwatch rxStopwatch = new();
TimeSpan rxTimeout = TimeSpan.FromSeconds(10);

// Configuration loop
while (true)
{
    // Prompt the user to connect to the plug
    Console.WriteLine();
    Console.WriteLine("Press and hold the button on the plug until it begins rapidly flashing blue, then connect to the 'WiWo-S20' WiFi network.");
    Console.Write("Press enter to configure the AP, press escape to exit. ");

    PurgeConsoleInputBuffer();
    while (true)
    {
        switch (Console.ReadKey(intercept: true).Key)
        {
            case ConsoleKey.Enter:
                Console.WriteLine();
                break;
            case ConsoleKey.Q:
                Console.WriteLine('Q');
                return 0;
            case ConsoleKey.Escape:
                Console.WriteLine();
                return 0;
            default:
                Console.Beep();
                continue;
        }

        break;
    }

    // Open UDP connection
    using UdpClient udp = new(listenEndPoint)
    {
        EnableBroadcast = true
    };

    IPEndPoint deviceEndPoint = broadcastEndPoint;

    void UdpDebugPrint(string prefix, string command)
    {
        string fullMessage = $"[{prefix}] ";

        foreach (char c in command)
        {
            if (c == '\r')
            { fullMessage += @"\r"; }
            else if (c == '\n')
            { fullMessage += @"\n"; }
            else if (c == '\t')
            { fullMessage += @"\t"; }
            else if (c == '\\')
            { fullMessage += @"\\"; }
            else if (c >= ' ' && c <= '~')
            { fullMessage += c; }
            else if (c <= '\xFF')
            { fullMessage += $@"\x{(byte)c:X2}"; }
            else
            { fullMessage += $@"\x{(ushort)c:X4}"; }
        }

        Console.WriteLine(fullMessage);
        Debug.WriteLine(fullMessage);
    }

    [MethodImpl(MethodImplOptions.NoInlining)] // Do not inline since this allocates on the stack
    void SendCommand(string command)
    {
        UdpDebugPrint("TX", command);

        Span<byte> commandBytes = stackalloc byte[asciiEncoding.GetMaxByteCount(command.Length)];
        int length = asciiEncoding.GetBytes(command, commandBytes);
        commandBytes = commandBytes.Slice(0, length);

        if (udp.Send(commandBytes, deviceEndPoint) != commandBytes.Length)
        { throw new InvalidOperationException("The sent datagram length was incorrect."); }
    }

    string? GetResponse()
    {
        // Wait for data
        rxStopwatch.Restart();
        while (udp.Available == 0)
        {
            Thread.Sleep(10);

            if (rxStopwatch.Elapsed > rxTimeout)
            {
                Console.Error.WriteLine("Timed out waiting for response.");
                return null;
            }
        }

        // Receive data
        // For some reason there's no span overloads for UdpClient's receive methods :(
        IPEndPoint endPoint = deviceEndPoint;
        byte[] responseBytes = udp.Receive(ref endPoint);

        // Ignore data sent by us
        //if (myIps.Contains(endPoint.Address))
        if (endPoint.Address.Equals(listenIpAddress))
        { return GetResponse(); }

        // Decode the response
        string response = asciiEncoding.GetString(responseBytes);
        UdpDebugPrint("RX", response);
        return response;
    }

    // Initiate configuration mode
    Console.WriteLine();
    Console.WriteLine("Initiating configuration mode...");
    SendCommand("HF-A11ASSISTHREAD");
    switch (GetResponse())
    {
        case null:
            Console.Error.WriteLine("The device did not respond in a timely manner.");
            continue;
        case string response:
            // Acknowledge the response
            SendCommand("+ok");

            string[] parts = response.Split(',', 3);
            if (parts.Length != 3)
            {
                Console.Error.WriteLine("The device's response was malformed.");
                continue;
            }

            IPAddress? ip;
            PhysicalAddress? mac;
            string host = parts[2];

            if (!IPAddress.TryParse(parts[0], out ip))
            {
                Console.Error.WriteLine("The device's IP address was malformed.");
                continue;
            }

            if (!PhysicalAddress.TryParse(parts[1], out mac))
            {
                Console.Error.WriteLine("The device's MAC address was malformed.");
                continue;
            }

            Console.WriteLine($"Connected to device {host} ({mac}) @ {ip}");
            deviceEndPoint = new IPEndPoint(ip, configurationPort);
            break;
    }

    // Configure the device
    Console.WriteLine();
    Console.WriteLine("Configuring the device...");

    bool SendConfigurationCommand(string command)
    {
        SendCommand(command);
        switch (GetResponse())
        {
            case "+ok\r\n\r\n":
                return true;
            case string errorResponse when errorResponse.StartsWith("+ERR="):
            case "+ERR\r\n\r\n":
                Console.Error.WriteLine("Configuration command failed.");
                return false;
            case null:
                Console.Error.WriteLine("The device did not respond in a timely manner.");
                return false;
            default:
                Console.Error.WriteLine("Device response invalid.");
                return false;
        }
    }

    // Configure SSID
    if (!SendConfigurationCommand($"AT+WSSSID={apName}\r"))
    { continue; }

    // Configure encryption and password
    if (!SendConfigurationCommand($"AT+WSKEY={apEncryption},{apPassword}\r"))
    { continue; }

    // Enter station (WiFi client) mode
    if (!SendConfigurationCommand("AT+WMODE=STA\r"))
    { continue; }

    // Reboot the device
    Console.WriteLine("Configuration complete. The device will now reboot.");
    SendCommand("AT+Z\r");
}
