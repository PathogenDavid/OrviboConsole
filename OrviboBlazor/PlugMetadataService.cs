using OrviboControl;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;

namespace OrviboBlazor;

internal sealed class PlugMetadataService
{
    private readonly object PlugsLock = new();
    private readonly SortedDictionary<MacAddress, PlugMetadata> Plugs = new();

    private readonly string DatabaseDirectory;
    private const string FileNamePrefix = "Plug_";
    private const string FileNameExtension = ".txt";

    private string MakeFilePath(MacAddress macAddress)
        => Path.Combine(DatabaseDirectory, $"{FileNamePrefix}{macAddress.ToString(useSeparators: false)}{FileNameExtension}");

    public ImmutableArray<MacAddress> MacAddresses
    {
        get
        {
            lock (PlugsLock)
            { return Plugs.Keys.ToImmutableArray(); }
        }
    }

    public event Action? ChangesComitted;

    public PlugMetadataService()
    {
        DatabaseDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
        DiscardChanges();
    }

    public void EnsureAllPlugsExist(IEnumerable<MacAddress> macAddresses)
    {
        lock (PlugsLock)
        {
            foreach (MacAddress macAddress in macAddresses)
            { GetMetadata(macAddress); }
        }
    }

    public PlugMetadata GetMetadata(MacAddress macAddress)
    {
        lock (PlugsLock)
        {
            if (Plugs.TryGetValue(macAddress, out PlugMetadata? plug))
            { return plug; }

            plug = new PlugMetadata(macAddress);
            Plugs.Add(macAddress, plug);
            return plug;
        }
    }

    public void Forget(MacAddress macAddress)
    {
        lock (PlugsLock)
        {
            Plugs.Remove(macAddress);

            try
            {
                string fileName = MakeFilePath(macAddress);
                if (File.Exists(fileName))
                { File.Delete(fileName); }
            }
            catch (Exception ex)
            { Debug.WriteLine($"Failed to delete metadata for {macAddress}: {ex}"); }
        }
    }

    public void DiscardChanges()
    {
        lock (PlugsLock)
        {
            Plugs.Clear();

            foreach (string plugFilePath in Directory.EnumerateFiles(DatabaseDirectory, $"{FileNamePrefix}*{FileNameExtension}"))
            {
                ReadOnlySpan<char> fileName = Path.GetFileNameWithoutExtension(plugFilePath.AsSpan());

                if (fileName.Length < FileNamePrefix.Length)
                {
                    Debug.WriteLine($"Failed to load plug metadata from '{Path.GetFileName(plugFilePath)}': Name too short");
                    continue;
                }

                ReadOnlySpan<char> macAddressString = fileName.Slice(FileNamePrefix.Length);
                MacAddress macAddress;

                if (!MacAddress.TryParse(macAddressString, out macAddress))
                {
                    Debug.WriteLine($"Failed to load plug metadata from '{Path.GetFileName(plugFilePath)}': MAC address portion invalid");
                    continue;
                }

                PlugMetadata metadata = new(macAddress);
                try
                {
                    using StreamReader file = File.OpenText(plugFilePath);

                    if (file.ReadLine() is string name)
                    { metadata.Name = name; }

                    if (Boolean.TryParse(file.ReadLine(), out bool scheduleEnabled))
                    { metadata.ScheduleEnabled = scheduleEnabled; }

                    if (TimeOnly.TryParse(file.ReadLine(), out TimeOnly onTime))
                    { metadata.OnTime = onTime; }

                    if (TimeOnly.TryParse(file.ReadLine(), out TimeOnly offTime))
                    { metadata.OffTime = offTime; }
                }
                catch (Exception ex)
                { Debug.WriteLine($"Failed to load plug metadata for {macAddress}: {ex}"); }

                Plugs[macAddress] = metadata;
            }
        }
    }

    public void CommitChanges()
    {
        lock (PlugsLock)
        {
            foreach (PlugMetadata metadata in Plugs.Values)
            {
                using StreamWriter file = new(MakeFilePath(metadata.MacAddress));
                file.WriteLine(metadata.Name);
                file.WriteLine(metadata.ScheduleEnabled);
                file.WriteLine(metadata.OnTime?.ToString() ?? "<null>");
                file.WriteLine(metadata.OffTime?.ToString() ?? "<null>");
            }
        }

        ChangesComitted?.Invoke();
    }

    public ImmutableDictionary<MacAddress, PlugMetadata> CloneCurrentPlugs()
    {
        lock (PlugsLock)
        {
            ImmutableDictionary<MacAddress, PlugMetadata>.Builder builder = ImmutableDictionary.CreateBuilder<MacAddress, PlugMetadata>();

            foreach (PlugMetadata plug in Plugs.Values)
            { builder.Add(plug.MacAddress, plug with { }); }

            return builder.ToImmutable();
        }
    }
}
