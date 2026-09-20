namespace BakeSmartPatri.Services;

/// <summary>Provides the current business time in Costa Rica on Windows and Linux hosts.</summary>
public static class BusinessClock
{
    private static readonly TimeZoneInfo CostaRica = ResolveCostaRicaTimeZone();
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, CostaRica);

    private static TimeZoneInfo ResolveCostaRicaTimeZone()
    {
        foreach (var id in new[] { "America/Costa_Rica", "Central America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }
}
