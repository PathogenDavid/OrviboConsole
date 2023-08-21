using OrviboControl;
using System;

namespace OrviboBlazor;

internal sealed record PlugMetadata
{
    public MacAddress MacAddress { get; }
    public string Name { get; set; } = "Unnamed Plug";
    public bool ScheduleEnabled { get; set; } = false;
    public TimeOnly? OnTime { get; set; } = null;
    public TimeOnly? OffTime { get; set; } = null;
    public bool HasSchedule => OnTime is not null && OffTime is not null;

    public string OnTimeString
    {
        get => OnTime?.ToString() ?? "";
        set
        {
            if (String.IsNullOrEmpty(value))
            { OnTime = null; }
            else if (TimeOnly.TryParse(value, out TimeOnly time))
            { OnTime = time; }
        }
    }

    public string OffTimeString
    {
        get => OffTime?.ToString() ?? "";
        set
        {
            if (String.IsNullOrEmpty(value))
            { OffTime = null; }
            else if (TimeOnly.TryParse(value, out TimeOnly time))
            { OffTime = time; }
        }
    }

    public PlugMetadata(MacAddress macAddress)
        => MacAddress = macAddress;

    public (DateTime DateTime, bool PowerState) GetNextEvent(DateTime now)
    {
        if (OnTime is not TimeOnly onTime || OffTime is not TimeOnly offTime)
        { throw new InvalidOperationException("This metadata does not have a schedule."); }

        TimeOnly currentTime = TimeOnly.FromDateTime(now);
        DateOnly today = DateOnly.FromDateTime(now);
        DateOnly tomorrow = today.AddDays(1);

        DateTime nextOn = currentTime < onTime ? today.ToDateTime(onTime) : tomorrow.ToDateTime(onTime);
        DateTime nextOff = currentTime < offTime ? today.ToDateTime(offTime) : tomorrow.ToDateTime(offTime);

        if (nextOn < nextOff)
        { return (nextOn, true); }
        else
        { return (nextOff, false); }
    }
}
