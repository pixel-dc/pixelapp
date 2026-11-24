namespace Pixela.Domain.Chat;

/// <summary>Periodo de consumo: el mes UTC en formato aaaamm.</summary>
public static class UsagePeriod
{
    public static int Of(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return (utc.Year * 100) + utc.Month;
    }
}
