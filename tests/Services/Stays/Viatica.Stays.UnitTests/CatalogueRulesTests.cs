using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Stays.Application;
using Viatica.Stays.Application.Features.Properties;
using Viatica.Stays.Application.Features.RoomTypes;
using Viatica.Stays.Domain;

namespace Viatica.Stays.UnitTests;

public sealed class CatalogueRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Partner = Guid.CreateVersion7();

    private static readonly RoomTypeDetails DoubleRoom = new("Double", 2, "1 double", ["balcony"], Money.Eur(120m));

    private static StayProperty NewProperty() => StayProperty.Create(Guid.CreateVersion7(), Partner, ValidatorTests.Details(), Now);

    private static RoomType AddRoom(StayProperty property, RoomTypeDetails? details = null)
    {
        var room = RoomType.Create(Guid.CreateVersion7(), property.Id, details ?? DoubleRoom, Now);
        property.RoomTypes.Add(room);
        property.RoomTypesChanged(Now);
        return room;
    }

    [Fact]
    public void A_new_property_is_a_draft_at_version_one()
    {
        StayProperty property = NewProperty();

        property.Status.ShouldBe(PropertyStatus.Draft);
        property.ListingVersion.ShouldBe(1);
        property.Details.ShouldBe(ValidatorTests.Details());
    }

    [Fact]
    public void A_property_without_a_room_type_cannot_be_published()
    {
        StayProperty property = NewProperty();

        var error = Should.Throw<ApiErrorException>(() => property.Publish(Now));

        error.Category.ShouldBe(ErrorCategory.Conflict);
        error.Error.Code.ShouldBe(StayErrorCode.NoRoomTypes);
        property.Status.ShouldBe(PropertyStatus.Draft);
    }

    [Fact]
    public void Every_change_moves_the_version_up_and_none_moves_it_back()
    {
        StayProperty property = NewProperty();
        var versions = new List<long> { property.ListingVersion };

        property.Revise(ValidatorTests.Details() with { Name = "New name" }, Now);
        versions.Add(property.ListingVersion);
        AddRoom(property);
        versions.Add(property.ListingVersion);
        property.Publish(Now).ShouldBeTrue();
        versions.Add(property.ListingVersion);
        property.Unpublish(Now).ShouldBeTrue();
        versions.Add(property.ListingVersion);
        property.Publish(Now).ShouldBeTrue();
        versions.Add(property.ListingVersion);

        versions.ShouldBe([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public void Publishing_twice_or_unpublishing_a_draft_changes_nothing()
    {
        StayProperty property = NewProperty();
        AddRoom(property);

        property.Unpublish(Now).ShouldBeFalse();
        property.Publish(Now).ShouldBeTrue();
        long version = property.ListingVersion;
        property.Publish(Now).ShouldBeFalse();

        property.ListingVersion.ShouldBe(version);
    }

    [Fact]
    public void Details_with_the_same_amenities_in_another_list_are_equal()
    {
        PropertyDetails one = ValidatorTests.Details();
        PropertyDetails two = ValidatorTests.Details() with { Amenities = ["wifi", "pool"] };
        PropertyDetails other = ValidatorTests.Details() with { Amenities = ["wifi"] };

        two.ShouldBe(one);
        other.ShouldNotBe(one);
    }

    [Fact]
    public void A_patch_replaces_what_it_carries_and_keeps_the_rest()
    {
        PropertyDetails merged = new PropertyPatch(Name: "Renamed", StarRating: 5).ApplyTo(ValidatorTests.Details());

        merged.Name.ShouldBe("Renamed");
        merged.StarRating.ShouldBe(5);
        merged.Address.ShouldBe(ValidatorTests.Details().Address);
        merged.Amenities.ShouldBe(["wifi", "pool"]);
    }

    [Fact]
    public void Guests_must_fit_the_rooms()
    {
        RoomType room = RoomType.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), DoubleRoom, Now);

        room.EnsureFits(guests: 4, rooms: 2);

        var error = Should.Throw<ApiErrorException>(() => room.EnsureFits(guests: 5, rooms: 2));
        error.Error.Code.ShouldBe(StayErrorCode.OccupancyExceeded);
        error.Error.Target.ShouldBe("guests");
    }

    [Fact]
    public void Visibility_follows_ownership_and_publication()
    {
        StayProperty draft = NewProperty();
        var owner = new Caller(Partner, IsAdmin: false);
        var rival = new Caller(Guid.CreateVersion7(), IsAdmin: false);
        var admin = new Caller(null, IsAdmin: true);

        owner.CanSee(draft).ShouldBeTrue();
        admin.CanSee(draft).ShouldBeTrue();
        rival.CanSee(draft).ShouldBeFalse();
        Caller.Anonymous.CanSee(draft).ShouldBeFalse();

        AddRoom(draft);
        draft.Publish(Now);
        rival.CanSee(draft).ShouldBeTrue();
        Caller.Anonymous.CanSee(draft).ShouldBeTrue();
        rival.Owns(draft).ShouldBeFalse();
    }

    [Fact]
    public void The_snapshot_carries_the_whole_listing_at_its_version()
    {
        StayProperty property = NewProperty();
        RoomType room = AddRoom(property);
        property.Publish(Now);

        StayListingUpdated snapshot = ListingEvents.Snapshot(property);

        snapshot.PropertyId.ShouldBe(property.Id);
        snapshot.PartnerId.ShouldBe(Partner);
        snapshot.ListingVersion.ShouldBe(property.ListingVersion);
        snapshot.Name.ShouldBe("Casa do Mar");
        snapshot.Address.City.ShouldBe("Lisbon");
        snapshot.Location.Latitude.ShouldBe(38.71);
        snapshot.CheckInFrom.ShouldBe(new TimeOnly(15, 0));
        snapshot.CancellationPolicy.ShouldBe(new Viatica.Contracts.CancellationPolicy(48, Viatica.Contracts.CancellationPenalty.FirstUnit));
        snapshot.RoomTypes.ShouldHaveSingleItem().RoomTypeId.ShouldBe(room.Id);
        snapshot.PartitionKey.ShouldBe(property.Id.ToString("N"));
    }

    [Fact]
    public void A_room_type_patch_changes_only_what_it_names()
    {
        RoomTypeDetails merged = new RoomTypePatch(MaxOccupancy: 3).ApplyTo(DoubleRoom);

        merged.MaxOccupancy.ShouldBe(3);
        merged.Name.ShouldBe("Double");
        merged.BaseRate.ShouldBe(Money.Eur(120m));
    }

    [Fact]
    public void Stay_dates_count_nights_not_days()
    {
        var dates = new StayDates(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 4));

        dates.Nights.ShouldBe(3);
        dates.EachNight().ShouldBe([new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 3)]);
    }
}
