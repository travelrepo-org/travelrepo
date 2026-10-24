using System.Globalization;
using System.Text.Json.Nodes;

namespace TravelRepo.Core;

/// <summary>How to reach a place or provider: any of phone, e-mail and website.</summary>
public sealed record ContactDetails(string? Phone, string? Email, string? Website)
{
    public bool IsEmpty => Phone is null && Email is null && Website is null;

    /// <summary>Reads a <c>contact</c> object; returns null when it has none of the three values.</summary>
    public static ContactDetails? Read(JsonNode? node)
    {
        static string? Text(JsonNode? n) => n is JsonValue v && v.ToString().Trim() is { Length: > 0 } s ? s : null;
        var contact = new ContactDetails(Text(node?["phone"]), Text(node?["email"]), Text(node?["website"]));
        return contact.IsEmpty ? null : contact;
    }
}

/// <summary>Reading place entities.</summary>
public static class Places
{
    /// <summary>The place's position in decimal degrees, or null when it has none or it is out of range.</summary>
    public static (double Latitude, double Longitude)? Location(Entity? place)
    {
        if (place?.Data["location"] is not { } location) return null;
        return double.TryParse(location["latitude"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) && double.TryParse(location["longitude"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            && lat is >= -90 and <= 90 && lon is >= -180 and <= 180 ? (lat, lon) : null;
    }

    /// <summary>The formatted address, if the place has one.</summary>
    public static string? Address(Entity? place) => place?.Data["address"]?["formatted"]?.ToString() is { Length: > 0 } address ? address : null;

    public static ContactDetails? Contact(Entity? place) => ContactDetails.Read(place?.Data["contact"]);
}
