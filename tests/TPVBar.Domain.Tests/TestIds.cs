using System.Reflection;

namespace TPVBar.Domain.Tests;

/// <summary>Asigna identificadores a entidades sin persistir (sus setters son privados a propósito).</summary>
internal static class TestIds
{
    public static T WithId<T>(this T entity, long id)
        where T : class
    {
        var property = typeof(T).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)!;
        property.SetValue(entity, Convert.ChangeType(id, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
        return entity;
    }
}
