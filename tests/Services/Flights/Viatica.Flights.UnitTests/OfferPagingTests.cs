using Viatica.Flights.Application.Features.Search;
using Viatica.Flights.Application.Offers;
using Viatica.Kernel.Querying;

namespace Viatica.Flights.UnitTests;

/// <summary>
/// Keyset paging over a cached result set, in memory. The skiptoken signing is the Kernel's and tested there; here
/// the position is carried by hand, the way the web layer carries it.
/// </summary>
public sealed class OfferPagingTests
{
    // 30 offers with many price ties, so the tie-breakers (departure, then id) are what decide the order.
    private static readonly FlightOffer[] Offers =
    [
        .. Enumerable.Range(0, 30).Select(i =>
        {
            FlightOffer offer = Make.SupplierOffer(
                i % 4 == 0 ? "ZC" : i % 4 == 1 ? "YZ" : i % 4 == 2 ? "JG" : "ZJ", 200m + (i % 5) * 10, departureHour: 6 + (i % 7), index: i).Offer;
            return offer with { Id = i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture) };
        }),
    ];

    private static StoredSearch Search() =>
        new(Guid.CreateVersion7(), Make.Now.AddMinutes(15), [], [.. Offers.OrderBy(static o => o.Total.Amount).ThenBy(static o => o.Departure).ThenBy(static o => o.Id, StringComparer.Ordinal)]);

    private static List<FlightOffer> PageThrough(string? filter, string? orderBy, int top, out int pages)
    {
        StoredSearch search = Search();
        var seen = new List<FlightOffer>();
        IReadOnlyList<string>? after = null;
        pages = 0;

        do
        {
            CollectionQuery<OfferRow> query = OfferRow.Fields.Parse(filter, orderBy, top, after);
            FlightSearchPage page = OfferPaging.Page(search, query);
            seen.AddRange(page.Offers.Value);
            after = page.Offers.NextPosition;
            pages++;
        }
        while (after is not null && pages < 100);

        return seen;
    }

    [Fact]
    public void Paging_visits_every_offer_once_in_price_then_departure_order()
    {
        List<FlightOffer> seen = PageThrough(null, null, top: 7, out int pages);

        pages.ShouldBe(5);
        seen.Select(static o => o.Id).ShouldBe(Search().Offers.Select(static o => o.Id));
        seen.Select(static o => o.Id).ShouldBeUnique();
        seen.Zip(seen.Skip(1)).ShouldAllBe(static pair =>
            pair.First.Total.Amount < pair.Second.Total.Amount
            || (pair.First.Total.Amount == pair.Second.Total.Amount && pair.First.Departure <= pair.Second.Departure));
    }

    [Fact]
    public void A_page_is_cut_at_top_and_the_last_page_has_no_next_position()
    {
        CollectionQuery<OfferRow> first = OfferRow.Fields.Parse(null, null, 20);
        FlightSearchPage page = OfferPaging.Page(Search(), first);

        page.Offers.Value.Count.ShouldBe(20);
        page.Offers.NextPosition.ShouldNotBeNull();

        FlightSearchPage last = OfferPaging.Page(Search(), OfferRow.Fields.Parse(null, null, 20, page.Offers.NextPosition));
        last.Offers.Value.Count.ShouldBe(10);
        last.Offers.NextPosition.ShouldBeNull();
    }

    [Fact]
    public void The_default_first_page_is_twenty()
    {
        OfferPaging.Page(Search(), OfferRow.FirstPage()).Offers.Value.Count.ShouldBe(20);
    }

    [Fact]
    public void A_filter_narrows_the_set_and_paging_still_covers_all_of_it()
    {
        List<FlightOffer> seen = PageThrough("airline eq 'ZC' and totalAmount le 230.00", null, top: 2, out _);

        seen.ShouldNotBeEmpty();
        seen.ShouldAllBe(static o => o.Airline == "ZC" && o.Total.Amount <= 230m);
        seen.Count.ShouldBe(Offers.Count(static o => o.Airline == "ZC" && o.Total.Amount <= 230m));
    }

    [Fact]
    public void Another_order_is_total_too()
    {
        List<FlightOffer> seen = PageThrough(null, "departure desc, totalAmount asc", top: 4, out _);

        seen.Count.ShouldBe(30);
        seen.Select(static o => o.Id).ShouldBeUnique();
        seen.Zip(seen.Skip(1)).ShouldAllBe(static pair => pair.First.Departure >= pair.Second.Departure);
    }

    [Theory]
    [InlineData("airline eq 'ZC' or cabin eq 'Economy'")]
    [InlineData("airlines eq 'ZC'")]
    [InlineData("1 eq 1")]
    public void Anything_outside_the_allowlist_is_refused(string filter) =>
        Should.Throw<Viatica.Kernel.ApiErrorException>(() => OfferRow.Fields.Parse(filter, null, 5)).Error.Code.ShouldBe("InvalidFilter");

    [Fact]
    public void A_field_that_cannot_be_compared_cannot_be_sorted_on() =>
        Should.Throw<Viatica.Kernel.ApiErrorException>(() => OfferRow.Fields.Parse(null, "refundable", 5)).Error.Code.ShouldBe("InvalidOrderBy");
}
