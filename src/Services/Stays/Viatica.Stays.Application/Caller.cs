using Viatica.Stays.Domain;

namespace Viatica.Stays.Application;

/// <summary>
/// Who is asking, as far as the use cases care. The endpoint builds it from the token, so Application never sees
/// ASP.NET Core claims.
/// </summary>
public sealed record Caller(Guid? PartnerId, bool IsAdmin)
{
    public static Caller Anonymous { get; } = new(null, false);

    public bool Owns(StayProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return PartnerId is { } partner && partner == property.PartnerId;
    }

    /// <summary>Everyone sees a published property; the owner sees a draft, and so does an admin.</summary>
    public bool CanSee(StayProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.IsPublished || IsAdmin || Owns(property);
    }
}

/// <summary>A resource and the row version behind its ETag. <see cref="IsNew"/> tells a 201 from a 200.</summary>
public sealed record Resource<T>(T Value, uint Version, bool IsNew = false);
