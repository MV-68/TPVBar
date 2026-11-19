namespace TPVBar.Application.Common;

/// <summary>
/// Estrategia horaria: los instantes se guardan en UTC (timestamptz). La zona horaria del
/// establecimiento (Europe/Madrid) solo se usa para decidir "qué día es" en reglas de negocio
/// (vigencia de impuestos, jornada) y para mostrar horas al usuario.
/// </summary>
public static class BusinessTime
{
    private static readonly TimeZoneInfo Zone = Resolve();

    public static DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    /// <summary>Instante UTC en que empieza el día local indicado (medianoche de Europe/Madrid).</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone), TimeSpan.Zero);
    }

    public static DateOnly ToLocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);

    public static DateOnly Today(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return DateOnly.FromDateTime(ToLocal(time.GetUtcNow()).DateTime);
    }

    private static TimeZoneInfo Resolve()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        }
    }
}
