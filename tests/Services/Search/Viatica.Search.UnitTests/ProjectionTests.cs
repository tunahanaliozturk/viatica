using Viatica.Contracts;
using Viatica.Search.Application.Projection;

namespace Viatica.Search.UnitTests;

public sealed class ProjectionTests
{
    private static readonly Guid Property = Guid.Parse("0192f0c4-0000-7000-8000-000000000001");
    private static readonly Guid Double = Guid.Parse("0192f0c4-0000-7000-8000-0000000000a1");
    private static readonly Guid Suite = Guid.Parse("0192f0c4-0000-7000-8000-0000000000a2");
    private static readonly DateOnly Night = Events.Today.AddDays(10);

    private readonly StayProjection _stays = new();

    private StayDocument? Apply(StayDocument? document, IIntegrationEvent message, out ProjectionOutcome outcome) =>
        _stays.Apply(document, message, Events.FirstDay, out outcome);

    [Fact]
    public void A_listing_creates_the_document_with_its_rooms()
    {
        StayDocument? document = Apply(null, Events.Listing(Property, 3, roomTypes: [Double, Suite]), out ProjectionOutcome outcome);

        outcome.ShouldBe(ProjectionOutcome.Applied);
        document!.Published.ShouldBeTrue();
        document.ListingVersion.ShouldBe(3);
        document.City.ShouldBe("Lisbon");
        document.Amenities.ShouldBe(["pool", "wifi"]);
        document.Rooms.Select(static r => r.Id).ShouldBe([Double, Suite]);
        document.Rooms.ShouldAllBe(static r => r.Listed);
    }

    [Fact]
    public void A_listing_with_an_older_or_equal_version_is_dropped()
    {
        StayDocument? document = Apply(null, Events.Listing(Property, 5, name: "Current"), out _);

        Apply(document, Events.Listing(Property, 4, name: "Older"), out ProjectionOutcome older);
        Apply(document, Events.Listing(Property, 5, name: "Duplicate"), out ProjectionOutcome duplicate);

        older.ShouldBe(ProjectionOutcome.Dropped);
        duplicate.ShouldBe(ProjectionOutcome.Dropped);
        document!.Name.ShouldBe("Current");
    }

    [Fact]
    public void An_unpublish_overtaken_by_nothing_hides_the_property_and_blocks_older_listings()
    {
        StayDocument? document = Apply(null, Events.Listing(Property, 2, roomTypes: [Double]), out _);
        document = Apply(document, Events.Unpublished(Property, 4), out _);
        document!.Published.ShouldBeFalse();

        // A listing from before the unpublish, delivered late, must not bring the property back.
        Apply(document, Events.Listing(Property, 3, roomTypes: [Double]), out ProjectionOutcome late);

        late.ShouldBe(ProjectionOutcome.Dropped);
        document.Published.ShouldBeFalse();

        // A listing after it does (the partner published again).
        document = Apply(document, Events.Listing(Property, 5, roomTypes: [Double]), out _);
        document!.Published.ShouldBeTrue();
    }

    [Fact]
    public void An_unpublish_that_arrives_before_the_listing_it_follows_wins_over_that_listing()
    {
        StayDocument? document = Apply(null, Events.Unpublished(Property, 4), out _);

        Apply(document, Events.Listing(Property, 3), out ProjectionOutcome outcome);

        outcome.ShouldBe(ProjectionOutcome.Dropped);
        document!.Published.ShouldBeFalse();
    }

    [Fact]
    public void Availability_before_the_listing_is_kept_and_the_room_stays_unlisted_until_the_listing_arrives()
    {
        StayDocument? document = Apply(null, Events.Nights(Property, Double, 1, Night, 3, 2, 100m), out ProjectionOutcome outcome);

        outcome.ShouldBe(ProjectionOutcome.Applied);
        document!.Published.ShouldBeFalse();
        document.Rooms.Single().Listed.ShouldBeFalse();

        document = Apply(document, Events.Listing(Property, 1, roomTypes: [Double]), out _);

        document!.Published.ShouldBeTrue();
        RoomDocument room = document.Rooms.Single();
        room.Listed.ShouldBeTrue();
        room.Open["2"].ShouldBe([new DayRange(Night.DayNumber, Night.DayNumber + 2)]);
    }

    [Fact]
    public void Availability_for_two_room_types_is_kept_apart()
    {
        StayDocument? document = Apply(null, Events.Listing(Property, 1, roomTypes: [Double, Suite]), out _);
        document = Apply(document, Events.Nights(Property, Double, 1, Night, 2, 5, 100m), out _);
        document = Apply(document, Events.Nights(Property, Suite, 1, Night, 2, 0, 300m), out _);

        document!.Rooms.Single(r => r.Id == Double).Open["1"].ShouldNotBeEmpty();
        document.Rooms.Single(r => r.Id == Suite).Open["1"].ShouldBeEmpty();
    }

    [Fact]
    public void A_sold_out_night_splits_the_run_that_a_stay_would_need()
    {
        StayDocument? document = Apply(null, Events.Listing(Property, 1, roomTypes: [Double]), out _);
        document = Apply(document, Events.Nights(Property, Double, 1, Night, 5, 2, 100m), out _);
        document = Apply(document, Events.Nights(Property, Double, 2, Night.AddDays(2), 1, 0, 100m), out _);

        document!.Rooms.Single().Open["1"].ShouldBe(
        [
            new DayRange(Night.DayNumber, Night.DayNumber + 1),
            new DayRange(Night.DayNumber + 3, Night.DayNumber + 4),
        ]);
    }

    [Fact]
    public void Availability_delivered_out_of_order_ends_where_in_order_delivery_ends()
    {
        IIntegrationEvent[] events =
        [
            Events.Listing(Property, 1, roomTypes: [Double]),
            Events.Nights(Property, Double, 1, Night, 4, 3, 100m),
            Events.Nights(Property, Double, 2, Night.AddDays(1), 2, 0, 110m),
            Events.Nights(Property, Double, 3, Night.AddDays(2), 2, 1, 120m),
        ];

        StayDocument? inOrder = null;
        foreach (IIntegrationEvent message in events)
        {
            inOrder = Apply(inOrder, message, out _);
        }

        // Every ordering of the four, each event also delivered a second time at the end.
        foreach (int[] order in Permutations(events.Length))
        {
            StayDocument? shuffled = null;
            foreach (int index in order.Concat(order))
            {
                shuffled = Apply(shuffled, events[index], out _);
            }

            Snapshot(shuffled!).ShouldBe(Snapshot(inOrder!), string.Join(",", order));
        }
    }

    [Fact]
    public void Availability_that_adds_nothing_new_is_dropped_and_leaves_the_document_alone()
    {
        StayDocument? document = Apply(null, Events.Listing(Property, 1, roomTypes: [Double]), out _);
        document = Apply(document, Events.Nights(Property, Double, 2, Night, 2, 3, 100m), out _);

        Apply(document, Events.Nights(Property, Double, 2, Night, 2, 3, 100m), out ProjectionOutcome duplicate);
        Apply(document, Events.Nights(Property, Double, 1, Night, 2, 0, 90m), out ProjectionOutcome older);

        duplicate.ShouldBe(ProjectionOutcome.Dropped);
        older.ShouldBe(ProjectionOutcome.Dropped);
        document!.Rooms.Single().Open["1"].ShouldNotBeEmpty();
    }

    [Fact]
    public void A_room_type_the_next_listing_no_longer_names_is_unlisted_but_keeps_its_nights()
    {
        StayDocument? document = Apply(null, Events.Listing(Property, 1, roomTypes: [Double, Suite]), out _);
        document = Apply(document, Events.Nights(Property, Suite, 1, Night, 2, 1, 300m), out _);

        document = Apply(document, Events.Listing(Property, 2, roomTypes: [Double]), out _);

        RoomDocument suite = document!.Rooms.Single(r => r.Id == Suite);
        suite.Listed.ShouldBeFalse();
        suite.Open["1"].ShouldNotBeEmpty();

        document = Apply(document, Events.Listing(Property, 3, roomTypes: [Double, Suite]), out _);
        document!.Rooms.Single(r => r.Id == Suite).Listed.ShouldBeTrue();
    }

    [Fact]
    public void Nights_priced_in_another_currency_are_left_out()
    {
        var message = new StayAvailabilityChanged(Property, Double, 1, [new DateAvailability(Night, 5, new Price(100m, "USD"))]);

        StayDocument? document = Apply(null, message, out ProjectionOutcome outcome);

        outcome.ShouldBe(ProjectionOutcome.Dropped);
        document.ShouldBeNull();
    }

    [Fact]
    public void Cars_follow_the_same_rules()
    {
        var cars = new CarProjection();
        Guid location = Guid.NewGuid();
        Guid category = Guid.NewGuid();
        int day = Events.Today.AddDays(3).DayNumber;

        CarLocationDocument? document = cars.Apply(null, Events.Days(location, category, 2, Events.Today.AddDays(3), 3, 4, 50m), Events.FirstDay, out _);
        document = cars.Apply(document, Events.Location(location, 1, null, category), Events.FirstDay, out _);
        cars.Apply(document, Events.Location(location, 1, null, category), Events.FirstDay, out ProjectionOutcome duplicate);
        cars.Apply(document, Events.Days(location, category, 1, Events.Today.AddDays(3), 3, 0, 50m), Events.FirstDay, out ProjectionOutcome stale);
        document = cars.Apply(document, new CarLocationUnpublished(location, 2), Events.FirstDay, out _);

        duplicate.ShouldBe(ProjectionOutcome.Dropped);
        stale.ShouldBe(ProjectionOutcome.Dropped);
        document!.Published.ShouldBeFalse();
        document.Categories.Single().Open["1"].ShouldBe([new DayRange(day, day + 2)]);
    }

    private static string Snapshot(StayDocument document) =>
        string.Join("|", document.Rooms.Select(static r => $"{r.Id}:{r.Listed}:{string.Join(",", r.Open.SelectMany(static o => o.Value.Select(range => $"{o.Key}[{range.From}-{range.To}]")))}:{string.Join(",", r.Rates)}"));

    private static IEnumerable<int[]> Permutations(int count)
    {
        int[] items = [.. Enumerable.Range(0, count)];
        return Recurse(items, 0);

        static IEnumerable<int[]> Recurse(int[] items, int start)
        {
            if (start == items.Length)
            {
                yield return [.. items];
                yield break;
            }

            for (int i = start; i < items.Length; i++)
            {
                (items[start], items[i]) = (items[i], items[start]);
                foreach (int[] permutation in Recurse(items, start + 1))
                {
                    yield return permutation;
                }

                (items[start], items[i]) = (items[i], items[start]);
            }
        }
    }
}
