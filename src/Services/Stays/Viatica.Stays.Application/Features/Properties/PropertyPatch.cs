using System.Text.RegularExpressions;
using Moongazing.OrionGuard.DependencyInjection;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Properties;

/// <summary>
/// The body of a PATCH: whatever is present replaces what is stored, whatever is missing stays. Objects such as the
/// address are replaced whole, so a client never has to wonder what half an address means. A field cannot be set
/// back to <c>null</c>, because none of them may be.
/// </summary>
public sealed record PropertyPatch(
    string? Name = null,
    string? Description = null,
    Address? Address = null,
    GeoPoint? Location = null,
    int? StarRating = null,
    IReadOnlyList<string>? Amenities = null,
    TimeOnly? CheckInFrom = null,
    TimeOnly? CheckOutUntil = null,
    CancellationPolicy? CancellationPolicy = null)
{
    /// <summary>A create is a patch with every field present, which lets one set of rules check both.</summary>
    public static PropertyPatch From(PropertyDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return new PropertyPatch(
            details.Name, details.Description, details.Address, details.Location, details.StarRating,
            details.Amenities, details.CheckInFrom, details.CheckOutUntil, details.CancellationPolicy);
    }

    public PropertyDetails ApplyTo(PropertyDetails current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return new PropertyDetails(
            Name ?? current.Name,
            Description ?? current.Description,
            Address ?? current.Address,
            Location ?? current.Location,
            StarRating ?? current.StarRating,
            Amenities ?? current.Amenities,
            CheckInFrom ?? current.CheckInFrom,
            CheckOutUntil ?? current.CheckOutUntil,
            CancellationPolicy ?? current.CancellationPolicy);
    }
}

/// <summary>
/// The rules for what a partner may write about a property, written once over a patch and shared by the create
/// and the update. A field that is absent passes: a create cannot omit one, because its body does not bind without
/// it, and an update leaves it alone.
/// </summary>
/// <remarks>
/// Targets name the JSON fields of the request body, not the command's own members, so a client's form can mark
/// the right input.
/// </remarks>
public abstract partial class PropertyRulesValidator<TRequest> : AbstractValidator<TRequest>
{
    private const int MaxAmenities = 50;

    protected PropertyRulesValidator(Func<TRequest, PropertyPatch> patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        RuleFor(r => patch(r).Name is not { } v || (!string.IsNullOrWhiteSpace(v) && v.Length <= 200), "name must be 1 to 200 characters.", "Name");
        RuleFor(r => patch(r).Description is not { } v || v.Length <= 4000, "description must be at most 4000 characters.", "Description");
        RuleFor(r => patch(r).StarRating is not { } v || v is >= 0 and <= 5, "starRating must be between 0 and 5.", "StarRating");

        RuleFor(r => patch(r).Address is not { } a || (!string.IsNullOrWhiteSpace(a.Line1) && a.Line1.Length <= 200), "address.line1 must be 1 to 200 characters.", "Address.Line1");
        RuleFor(r => patch(r).Address is not { } a || (!string.IsNullOrWhiteSpace(a.City) && a.City.Length <= 100), "address.city must be 1 to 100 characters.", "Address.City");
        RuleFor(r => patch(r).Address is not { } a || a.Region is null || a.Region.Length <= 100, "address.region must be at most 100 characters.", "Address.Region");
        RuleFor(r => patch(r).Address is not { } a || (!string.IsNullOrWhiteSpace(a.PostalCode) && a.PostalCode.Length <= 20), "address.postalCode must be 1 to 20 characters.", "Address.PostalCode");
        RuleFor(r => patch(r).Address is not { } a || CountryCode().IsMatch(a.CountryCode), "address.countryCode must be two capital letters, such as PT.", "Address.CountryCode");

        RuleFor(r => patch(r).Location is not { } l || l.Latitude is >= -90 and <= 90, "location.latitude must be between -90 and 90.", "Location.Latitude");
        RuleFor(r => patch(r).Location is not { } l || l.Longitude is >= -180 and <= 180, "location.longitude must be between -180 and 180.", "Location.Longitude");

        RuleFor(
            r => patch(r).Amenities is not { } a || (a.Count <= MaxAmenities && a.All(static x => !string.IsNullOrWhiteSpace(x) && x.Length <= 50)),
            $"amenities holds at most {MaxAmenities} entries of 1 to 50 characters.",
            "Amenities");

        RuleFor(
            r => patch(r).CancellationPolicy is not { } c || c.FreeUntilHoursBeforeStart is >= 0 and <= 8760,
            "cancellationPolicy.freeUntilHoursBeforeStart must be between 0 and 8760.",
            "CancellationPolicy.FreeUntilHoursBeforeStart");
    }

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryCode();
}
