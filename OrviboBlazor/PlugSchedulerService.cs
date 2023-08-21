using OrviboControl;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace OrviboBlazor;

internal sealed class PlugSchedulerService : IDisposable
{
    private readonly PlugEnumerator PlugEnumerator;
    private readonly PlugMetadataService PlugMetadataService;
    private ImmutableDictionary<MacAddress, PlugMetadata> CachedPlugMetadata;

    // MAC address => Manual override expiration time
    private readonly ConcurrentDictionary<MacAddress, DateTime> ManualOverrides = new();

    private readonly Thread ScheduleThread;
    private readonly EventWaitHandle QuitEventHandle = new(false, EventResetMode.ManualReset);
    private readonly EventWaitHandle WakeUpHandle = new(true, EventResetMode.AutoReset);

    public PlugSchedulerService(PlugEnumerator plugEnumerator, PlugMetadataService plugMetadataService)
    {
        PlugEnumerator = plugEnumerator;
        PlugMetadataService = plugMetadataService;

        PlugEnumerator.PlugsChanged += PlugsChanged;
        PlugMetadataService.ChangesComitted += PlugMetadataChanged;

        PlugMetadataChanged();

        ScheduleThread = new Thread(ScheduleThreadEntry)
        {
            Name = "Plug Schedule Thread"
        };
        ScheduleThread.Start();
    }

    [MemberNotNull(nameof(CachedPlugMetadata))]
    private void PlugMetadataChanged()
    {
        ImmutableDictionary<MacAddress, PlugMetadata> newMetadata = PlugMetadataService.CloneCurrentPlugs();

        // For any metadata which lacks an on/off time, ensure the schedule is disabled
        foreach (PlugMetadata metadata in newMetadata.Values)
        {
            if (metadata.OnTime is null || metadata.OffTime is null)
            { metadata.ScheduleEnabled = false; }
        }

        CachedPlugMetadata = newMetadata;

        // For any metadata with the schedule disabled, delete any old overrides
        if (ManualOverrides.Count > 0)
        {
            foreach (PlugMetadata metadata in CachedPlugMetadata.Values)
            {
                if (!metadata.ScheduleEnabled)
                { ManualOverrides.TryRemove(metadata.MacAddress, out _); }
            }
        }

        // Wake up the schedule thread
        WakeUpHandle.Set();
    }

    private void ScheduleThreadEntry()
    {
        const int quitEventIndex = 0;
        WaitHandle[] waitHandles = new[] { QuitEventHandle, WakeUpHandle };
        bool missingSomePlugs = false;

        Stopwatch discoveryPingStopwatch = new();
        discoveryPingStopwatch.Start();
        TimeSpan timeBetweenDiscovery = TimeSpan.FromMinutes(25);

        TimeSpan timeout = timeBetweenDiscovery;
        TimeSpan tolerance = TimeSpan.FromSeconds(1); // Always wait an extra second to avoid thrashing if we wake up sligthly too early

        while (true)
        {
            // Wait for the next event
            int wakeReason = WaitHandle.WaitAny(waitHandles, timeout + tolerance);
            if (wakeReason == quitEventIndex)
            { return; }

            // Check if we want to initiate a discovery ping
            // We do this if we're missing plugs and we were woken up via timeout or if it's been a while since we've done discovery
            if ((missingSomePlugs && wakeReason == WaitHandle.WaitTimeout) || discoveryPingStopwatch.Elapsed > timeBetweenDiscovery)
            {
                discoveryPingStopwatch.Restart();
                PlugEnumerator.DiscoverPlugs();
                Thread.Sleep(100); // Let things stabilize since the plugs don't like getting too many messages in succession and this is a broadcast message
            }

            // Check each plug's metadata to see if it has a schedule
            missingSomePlugs = false;
            DateTime now = DateTime.Now;
            DateTime nextEventForAnyPlug = now + (timeBetweenDiscovery - discoveryPingStopwatch.Elapsed); // The next event will always be discovery by default
            foreach (PlugMetadata metadata in CachedPlugMetadata.Values)
            {
                if (!metadata.ScheduleEnabled)
                { continue; }

                // Check if there's a manual override for this plug
                if (ManualOverrides.TryGetValue(metadata.MacAddress, out DateTime expirationTime))
                {
                    if (DateTime.Now < expirationTime)
                    { continue; }

                    // The override is expired, try to remove it from the override list
                    // (If this fails, another thread created another override while we were checking.)
                    if (!ManualOverrides.TryRemove(new KeyValuePair<MacAddress, DateTime>(metadata.MacAddress, expirationTime)))
                    { continue; }
                }

                Debug.Assert(metadata.HasSchedule);

                // Determine when the next event for this plug will be and the expected current state
                (DateTime thisPlugNextEvent, bool nextPowerState) = metadata.GetNextEvent(now);
                bool expectedPowerState = !nextPowerState;

                // Check if this plug's event is the next event that will happen
                if (thisPlugNextEvent < nextEventForAnyPlug)
                { nextEventForAnyPlug = thisPlugNextEvent; }

                // Check if this plug is available
                // We consider the plug to be done if we haven't seen it since the minimum discovery time
                Plug? plug;
                if (!PlugEnumerator.Plugs.TryGetValue(metadata.MacAddress, out plug) || (now - plug.LastSeen) > timeBetweenDiscovery)
                {
                    // If this plug is not available, note as such and move on to the next one
                    missingSomePlugs = true;
                    continue;
                }

                // Transition the plug's state if necessary
                if (plug.IsPowered != expectedPowerState)
                {
                    Debug.WriteLine($"Transitioning '{metadata.Name}' ({metadata.MacAddress}) {plug.IsPowered} -> {expectedPowerState}");
                    plug.SetPowerState(expectedPowerState);

                    // Queue a retry in case the message isn't received
                    DateTime checkTime = now + TimeSpan.FromMinutes(5);
                    if (checkTime < nextEventForAnyPlug)
                    { nextEventForAnyPlug = checkTime; }
                }
            }

            // Determine how long to wait for the next event
            timeout = nextEventForAnyPlug - DateTime.Now;
        }
    }

    private void PlugsChanged()
        => WakeUpHandle.Set();

    public void NoteManualOverride(MacAddress macAddress)
    {
        PlugMetadata? metadata;

        if (!CachedPlugMetadata.TryGetValue(macAddress, out metadata))
        { return; }

        if (!metadata.HasSchedule)
        { return; }

        ManualOverrides[macAddress] = metadata.GetNextEvent(DateTime.Now).DateTime;
    }

    public bool IsOverridden(MacAddress macAddress)
    {
        if (ManualOverrides.TryGetValue(macAddress, out DateTime overrideExpiration))
        { return overrideExpiration > DateTime.Now; }
        else
        { return false; }
    }

    public void ClearOverrides()
    {
        ManualOverrides.Clear();
        WakeUpHandle.Set();
    }

    public void Dispose()
    {
        QuitEventHandle.Set();
        ScheduleThread.Join();
        QuitEventHandle.Dispose();
        WakeUpHandle.Dispose();

        PlugMetadataService.ChangesComitted -= PlugMetadataChanged;
        PlugEnumerator.PlugsChanged -= PlugsChanged;

        UnsafeEx.Clear(PlugEnumerator);
        UnsafeEx.Clear(PlugMetadataService);
        UnsafeEx.Clear(CachedPlugMetadata);
        UnsafeEx.Clear(ManualOverrides);
        UnsafeEx.Clear(ScheduleThread);
        UnsafeEx.Clear(QuitEventHandle);
        UnsafeEx.Clear(WakeUpHandle);
    }
}
