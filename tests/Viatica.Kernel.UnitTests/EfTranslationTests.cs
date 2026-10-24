using Microsoft.EntityFrameworkCore;
using Viatica.Kernel.Querying;

namespace Viatica.Kernel.UnitTests;

/// <summary>
/// The filter, ordering and keyset predicates through the real Npgsql SQL generator. <c>ToQueryString</c> runs
/// the whole translation pipeline without opening a connection, so an expression EF Core cannot translate fails
/// here exactly as it would in a service.
/// </summary>
public sealed class EfTranslationTests
{
    private static string Sql(string? filter, string? orderBy = null, IReadOnlyList<string>? after = null)
    {
        using var db = new StaysDb();
        return Stays.Fields.Parse(filter, orderBy, top: 20, after).ApplyTo(db.Stays.AsNoTracking()).ToQueryString();
    }

    [Fact]
    public void Every_operator_and_type_translates_to_sql()
    {
        string sql = Sql(
            $"city gt 'lisbon' and fromPrice le 80.5 and stars ne 3 and refundable eq true and availableFrom ge 2026-12-01 " +
            $"and updatedAt lt 2026-11-01T05:00:00Z and partnerId eq '{Stays.Partner}' and kind eq 'Hostel' and discount eq 5 " +
            "and city in ('porto', 'faro')");

        sql.ShouldContain("s.city > @");
        sql.ShouldContain("s.from_price <= @");
        sql.ShouldContain("s.stars <> @");
        sql.ShouldContain("s.refundable = @");
        sql.ShouldContain("s.available_from >= @");
        sql.ShouldContain("s.updated_at < @");
        sql.ShouldContain("s.partner_id = @");
        sql.ShouldContain("s.kind = @");
        sql.ShouldContain("s.discount = @");
        sql.ShouldContain("s.city = ANY (@");
        sql.ShouldContain("LIMIT @");
    }

    [Fact]
    public void Caller_values_are_parameters_never_sql_text()
    {
        string sql = Sql("city eq 'x'' OR 1=1 --'");

        // ToQueryString prints parameter values as "-- @name='value'" comments above the statement; the statement
        // itself must hold only the placeholder.
        string statement = string.Join('\n', sql.Split('\n').Where(static line => !line.StartsWith("-- ", StringComparison.Ordinal)));
        statement.ShouldContain("WHERE s.city = @");
        statement.ShouldNotContain("1=1");
        sql.ShouldContain("1=1");
    }

    [Fact]
    public void Keyset_paging_becomes_an_indexable_seek_predicate()
    {
        string sql = Sql(null, "fromPrice desc, city", ["80", "porto", "17"]);

        sql.ShouldContain("ORDER BY s.from_price DESC, s.city, s.id");
        sql.ShouldContain("s.from_price < @");
        sql.ShouldContain("s.from_price = @");
        sql.ShouldContain("s.city > @");
        sql.ShouldContain("s.id > @");
    }

    [Fact]
    public void A_guid_key_seeks_with_a_plain_comparison()
    {
        var fields = new QueryFields<Partner>().Key("id", static p => p.Id).Field("name", static p => p.Name, sortable: true);
        using var db = new StaysDb();

        string sql = fields.Parse(null, "name", 20, ["acme", Stays.Partner.ToString()])
            .ApplyTo(db.Partners.AsNoTracking()).ToQueryString();

        sql.ShouldContain("p.id > @");
    }

    public sealed class Partner
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class StaysDb : DbContext
    {
        public DbSet<StayRow> Stays => Set<StayRow>();

        public DbSet<Partner> Partners => Set<Partner>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseNpgsql("Host=unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StayRow>(stay =>
            {
                stay.ToTable("stays");
                stay.Property(static s => s.City).HasColumnName("city");
                stay.Property(static s => s.FromPrice).HasColumnName("from_price");
                stay.Property(static s => s.Stars).HasColumnName("stars");
                stay.Property(static s => s.Refundable).HasColumnName("refundable");
                stay.Property(static s => s.AvailableFrom).HasColumnName("available_from");
                stay.Property(static s => s.UpdatedAt).HasColumnName("updated_at");
                stay.Property(static s => s.PartnerId).HasColumnName("partner_id");
                stay.Property(static s => s.Kind).HasColumnName("kind");
                stay.Property(static s => s.Discount).HasColumnName("discount");
                stay.Property(static s => s.Id).HasColumnName("id");
            });
            modelBuilder.Entity<Partner>(partner =>
            {
                partner.ToTable("partners");
                partner.Property(static p => p.Id).HasColumnName("id");
                partner.Property(static p => p.Name).HasColumnName("name");
            });
        }
    }
}
