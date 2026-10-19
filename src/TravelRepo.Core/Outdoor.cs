using System.Text.Json.Nodes;

namespace TravelRepo.Core;

/// <summary>How much the weather matters for a schedule item.</summary>
public enum Exposure
{
    /// <summary>Indoors, or a stay. Weather is shown but never warned about.</summary>
    Indoor,
    /// <summary>On the way by car, bus, train or plane. Only severe weather matters.</summary>
    Travel,
    /// <summary>Not known; could be outside. Only strong weather is worth a warning.</summary>
    Possibly,
    /// <summary>Outside: a walk, a boat trip, a beach. Rain, wind, heat and cold matter.</summary>
    Outdoor
}

/// <summary>Where an <see cref="Exposure"/> comes from, so clients can explain it.</summary>
public enum ExposureSource
{
    /// <summary>The item's own <c>outdoor</c> field.</summary>
    Explicit,
    /// <summary>The <c>outdoor</c> field of an enclosing item.</summary>
    Inherited,
    /// <summary>The transport component's type.</summary>
    Transport,
    /// <summary>The item is a stay.</summary>
    Accommodation,
    /// <summary>The item's category, without a hint in the title.</summary>
    Category,
    /// <summary>A word in the title, such as "hike" or "museum".</summary>
    Title
}

/// <summary>
/// The estimated exposure of an item. <see cref="Word"/> is the title word that decided it, <see cref="Category"/>
/// the category that was used, and <see cref="OnWater"/> marks ferries and ships, where wind matters more.
/// </summary>
public sealed record ExposureEstimate(Exposure Exposure, ExposureSource Source, string? Category = null, string? Word = null, bool OnWater = false);

/// <summary>
/// Whether a schedule item happens outdoors. The optional, inheritable <c>outdoor</c> field (true or false) states it.
/// Without it, the estimate uses the transport type, the category and a short list of English and German words in the
/// title; titles in other languages fall back to the category. The estimate is a presentation aid for weather and is
/// never written into the trip.
/// </summary>
public static class OutdoorEstimates
{
    /// <summary>The field that states whether an item happens outdoors.</summary>
    public const string Field = "outdoor";

    // Matching modes: '=' whole word, '^' start of a word, '*' anywhere in a word (for German compounds).
    private static readonly (string Stem, char Mode)[] OutdoorWords =
    [
        ("hike", '^'), ("hiking", '^'), ("trek", '^'), ("tramp", '^'), ("trail", '^'), ("walk", '^'), ("boardwalk", '*'), ("beach", '^'), ("swim", '^'),
        ("snorkel", '^'), ("dive", '='), ("diving", '^'), ("surf", '^'), ("kayak", '^'), ("canoe", '^'), ("raft", '^'), ("boat", '^'), ("cruise", '^'),
        ("sail", '^'), ("picnic", '^'), ("lookout", '^'), ("viewpoint", '^'), ("summit", '^'), ("glacier", '^'), ("waterfall", '^'), ("falls", '='),
        ("garden", '^'), ("park", '='), ("parks", '='), ("grounds", '='), ("zoo", '='), ("safari", '^'), ("climb", '^'), ("bungy", '^'), ("bungee", '^'),
        ("zipline", '^'), ("skydiv", '^'), ("paraglid", '^'), ("bike", '^'), ("biking", '^'), ("cycling", '^'), ("ski", '^'), ("golf", '^'),
        ("camping", '^'), ("sunset", '^'), ("sunrise", '^'), ("whale", '^'), ("dolphin", '^'), ("seal", '='), ("seals", '='), ("bird", '^'),
        ("wildlife", '^'), ("alpine", '^'), ("outdoor", '^'), ("openair", '='),
        ("wander", '*'), ("spaziergang", '*'), ("rundweg", '*'), ("pfad", '^'), ("strand", '*'), ("schwimm", '*'), ("schnorchel", '*'), ("tauch", '*'),
        ("kajak", '^'), ("kanu", '^'), ("boot", '^'), ("schiff", '*'), ("segel", '*'), ("picknick", '*'), ("aussicht", '*'), ("gipfel", '*'),
        ("gletscher", '*'), ("wasserfall", '*'), ("garten", '*'), ("gärten", '*'), ("nationalpark", '*'), ("naturpark", '*'), ("stadtpark", '*'),
        ("freizeitpark", '*'), ("klettern", '*'), ("radtour", '*'), ("fahrrad", '*'), ("mountainbike", '*'), ("zelten", '^'), ("sonnenuntergang", '*'),
        ("sonnenaufgang", '*'), ("walbeobachtung", '*'), ("delfin", '*'), ("robben", '*'), ("vogel", '*'), ("freiluft", '*'), ("freibad", '*'), ("draußen", '=')
    ];

    private static readonly (string Stem, char Mode)[] IndoorWords =
    [
        ("museum", '*'), ("gallery", '^'), ("galerie", '*'), ("cave", '^'), ("caves", '^'), ("höhle", '*'), ("aquarium", '*'), ("cinema", '^'), ("kino", '='),
        ("theatre", '^'), ("theater", '^'), ("workshop", '*'), ("indoor", '^'), ("planetarium", '*'), ("library", '^'), ("bibliothek", '*'), ("mall", '='),
        ("einkaufszentrum", '*'), ("exhibition", '^'), ("ausstellung", '*'), ("parliament", '^'), ("parlament", '*'), ("cooking", '^'), ("kochkurs", '*'),
        ("brewery", '^'), ("brauerei", '*'), ("bowling", '^'), ("drinnen", '=')
    ];

    /// <summary>The item's stated <c>outdoor</c> value, own or inherited, or null when nothing states it.</summary>
    public static (bool Value, bool Inherited)? Stated(TripSnapshot trip, Entity item)
    {
        InheritedValue resolved;
        try { resolved = TripQueries.Resolve(trip, item.Id, Field); } catch (DomainException) { resolved = new(item.Data[Field], item.Id, false); }
        return resolved.Value is JsonValue v && v.TryGetValue<bool>(out var flag) ? (flag, resolved.Inherited) : null;
    }

    /// <summary>Estimates the exposure of a schedule item.</summary>
    public static ExposureEstimate For(TripSnapshot trip, Entity item)
    {
        if (Stated(trip, item) is { } stated) return new(stated.Value ? Exposure.Outdoor : Exposure.Indoor, stated.Inherited ? ExposureSource.Inherited : ExposureSource.Explicit);
        if (Stays.IsStay(item)) return new(Exposure.Indoor, ExposureSource.Accommodation);
        if (item.Data["components"]?["transport"] is JsonObject)
            return ScheduleCategories.TransportType(item) switch
            {
                "walking" or "bicycle" => new(Exposure.Outdoor, ExposureSource.Transport),
                "ferry" or "ship" => new(Exposure.Travel, ExposureSource.Transport, OnWater: true),
                _ => new(Exposure.Travel, ExposureSource.Transport)
            };
        string? category = null;
        try { category = TripQueries.Resolve(trip, item.Id, "category").Value?.ToString(); } catch (DomainException) { category = item.Data["category"]?.ToString(); }
        var kind = ScheduleCategories.FromCategory(category);
        var basis = kind switch
        {
            ScheduleKind.Nature or ScheduleKind.Sightseeing => Exposure.Outdoor,
            ScheduleKind.Food or ScheduleKind.Culture or ScheduleKind.Shopping or ScheduleKind.Nightlife or ScheduleKind.Meeting or ScheduleKind.Other or ScheduleKind.Accommodation => Exposure.Indoor,
            ScheduleKind.Transport => Exposure.Travel,
            _ => Exposure.Possibly
        };
        var label = category ?? "activity";
        var outdoor = Find(item.Title, OutdoorWords); var indoor = Find(item.Title, IndoorWords);
        // A title word moves the category's estimate: towards outdoors or indoors, or to "possibly" when both appear.
        if (outdoor is not null && indoor is not null) return new(Exposure.Possibly, ExposureSource.Title, label, outdoor + ", " + indoor);
        if (outdoor is not null && basis != Exposure.Outdoor) return new(basis == Exposure.Indoor ? Exposure.Possibly : Exposure.Outdoor, ExposureSource.Title, label, outdoor);
        if (indoor is not null && basis != Exposure.Indoor) return new(Exposure.Indoor, ExposureSource.Title, label, indoor);
        return new(basis, ExposureSource.Category, label);
    }

    /// <summary>The first title word that matches the list, as written in the title.</summary>
    private static string? Find(string title, (string Stem, char Mode)[] words)
    {
        var text = title.Replace("open air", "openair", StringComparison.OrdinalIgnoreCase).Replace("open-air", "openair", StringComparison.OrdinalIgnoreCase);
        foreach (var word in Words(text))
        {
            if (word.Length < 3) continue;
            var lower = word.ToLowerInvariant();
            foreach (var (stem, mode) in words)
                if (mode switch { '=' => lower == stem, '^' => lower.StartsWith(stem, StringComparison.Ordinal), _ => lower.Contains(stem, StringComparison.Ordinal) }) return word;
        }
        return null;
    }

    private static List<string> Words(string text)
    {
        var parts = new List<string>(); var start = 0;
        for (var i = 0; i <= text.Length; i++)
            if (i == text.Length || !char.IsLetter(text[i])) { if (i > start) parts.Add(text[start..i]); start = i + 1; }
        return parts;
    }
}
