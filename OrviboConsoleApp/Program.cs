using OrviboControl;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

Trace.Listeners.Add(new ConsoleTraceListener());

object consoleLock = new();

using PlugEnumerator enumerator = new();
Dictionary<int, Plug> plugs = new();
enumerator.PacketReceived += p => PacketReceivedOrSent(isReceive: true, p);
enumerator.PacketSent += p => PacketReceivedOrSent(isReceive: false, p);
enumerator.PlugsChanged += ShowMenu;
enumerator.DiscoverPlugs();

void PacketReceivedOrSent(bool isReceive, ReadOnlySpan<byte> packet)
{
    StringBuilder log = new(4 + packet.Length * 3);
    log.Append(isReceive ? "[RX]" : "[TX]");

    foreach (byte b in packet)
    { log.Append($" {b:X02}"); }

    lock (consoleLock)
    { Console.WriteLine(log.ToString()); }
}

void ShowMenu()
{
    lock (consoleLock)
    {
        Console.WriteLine("========================================================================");
        Console.WriteLine("Commands:");
        Console.WriteLine("    d - Send discovery request");
        Console.WriteLine("    q - Quit");
        plugs.Clear();
        int i = 0;
        foreach (Plug plug in enumerator.Plugs.Values.OrderBy(p => p.MacAddress))
        {
            i++;
            plugs.Add(i, plug);
            Console.WriteLine($"    {(i < 10 ? i.ToString() : i == 10 ? "0" : " ")} - {plug.MacAddress} [{(plug.IsPowered ? "ON " : "OFF")}] (Firmware: {plug.FirmwareVersion}, Last Seen {(DateTime.Now - plug.LastSeen).TotalSeconds:0} seconds ago))");
        }
        Console.WriteLine("========================================================================");
        Console.WriteLine();
    }
}

while (true)
{
    ShowMenu();

    switch (Console.ReadKey(intercept: true).Key)
    {
        case ConsoleKey.D:
            Console.WriteLine("Sending discovery request...");
            enumerator.DiscoverPlugs();
            break;
        case ConsoleKey.Q:
        case ConsoleKey.Escape:
            Console.WriteLine("Goodbye");
            return;
        case ConsoleKey key when (key >= ConsoleKey.D0 && key <= ConsoleKey.D9):
            int index = key - ConsoleKey.D0;

            if (index == 0)
            { index = 10; }

            if (plugs.TryGetValue(index, out Plug? plug))
            { plug.SetPowerState(!plug.IsPowered); }
            break;
    }

    Console.WriteLine();
}
