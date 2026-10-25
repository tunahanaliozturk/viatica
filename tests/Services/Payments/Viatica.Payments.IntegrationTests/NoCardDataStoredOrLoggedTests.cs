using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viatica.Contracts;
using Viatica.Payments.Domain;
using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>
/// PAY-1: the card number never leaves this service except to the provider. It is never stored, never logged, and never
/// in an event or an error message. Every claim is checked against a full run of the paths that handle a card, and every
/// check has a control that proves it would see a leak if there were one.
/// </summary>
public sealed class NoCardDataStoredOrLoggedTests(DefaultPayments payments) : IClassFixture<DefaultPayments>, IAsyncLifetime
{
    private const string Cvc = "9871";

    // The numbers a run uses, in every form a person or a framework might write them.
    private static readonly string[] Numbers =
    [
        Cards.Visa, Cards.Mastercard, Cards.Declined, Cards.InsufficientFunds, Cards.TimesOutButHappens, Cards.TimesOutAndDoesNotHappen,
        "4242424242424241", // fails the Luhn check
        "4111111111111111", // passes it, and is not a test card
    ];

    private readonly Scenario scenario = new(payments);

    public async ValueTask InitializeAsync() => await payments.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<string> Forms(string number)
    {
        yield return number;
        yield return string.Join(' ', Enumerable.Range(0, (number.Length + 3) / 4).Select(i => number.Substring(i * 4, Math.Min(4, number.Length - (i * 4)))));
        yield return string.Join('-', Enumerable.Range(0, (number.Length + 3) / 4).Select(i => number.Substring(i * 4, Math.Min(4, number.Length - (i * 4)))));
    }

    private static IEnumerable<string> AllForms => Numbers.SelectMany(Forms);

    [Fact]
    public void The_log_capture_would_see_a_leak()
    {
        // A control for the log assertion below: this proves the capture holds messages, structured values and
        // exceptions, so an empty search result means the number was not there and not that nothing was captured.
#pragma warning disable CA1873 // The arguments are constants; the rule cannot see that the logger is a capture.
        ILogger canary = payments.Host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("canary");
        canary.LogInformation("in the message: {Value}", "CanaryInMessage");
        canary.LogDebug("only in the state: {Hidden}", "CanaryInState");
        canary.LogError(new InvalidOperationException("CanaryInException"), "failed");
#pragma warning restore CA1873

        payments.Host.Logs.Containing("CanaryInMessage").ShouldNotBeEmpty();
        payments.Host.Logs.Containing("CanaryInState").ShouldNotBeEmpty();
        payments.Host.Logs.Containing("CanaryInException").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_database_scan_would_see_a_leak()
    {
        // A control for the database assertion below: a card number planted in a table of the service schema, in a text
        // column and in a json column, is found by the same scan. Without this an empty result proves nothing.
        await payments.Host.InDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("create table payments.scan_canary (note text, document jsonb)");
            await db.Database.ExecuteSqlRawAsync("insert into payments.scan_canary values ('card 4242 4242 4242 4242', '{{\"n\":\"5555555555554444\"}}')");
            return 0;
        });

        try
        {
            IReadOnlyList<string> found = await ScanAsync(["4242 4242 4242 4242", "5555555555554444"]);

            found.ShouldContain(static hit => hit.StartsWith("scan_canary.note", StringComparison.Ordinal));
            found.ShouldContain(static hit => hit.StartsWith("scan_canary.document", StringComparison.Ordinal));
        }
        finally
        {
            await payments.Host.InDbAsync(async db => await db.Database.ExecuteSqlRawAsync("drop table payments.scan_canary"));
        }
    }

    [Fact]
    public async Task A_full_run_stores_logs_and_publishes_no_card_data()
    {
        List<string> answers = [];

        // Tokenise everything: good cards, cards that fail later, cards that fail now, and a bad body.
        foreach (string number in Numbers)
        {
            foreach (string form in new[] { number, string.Join(' ', Forms(number).Skip(1).Take(1)) })
            {
                using HttpResponseMessage response = await scenario.TokeniseAsync(new { number = form, expMonth = 12, expYear = 2030, cvc = Cvc });
                answers.Add(await response.Content.ReadAsStringAsync());
            }
        }

        using (HttpResponseMessage expired = await scenario.TokeniseAsync(new { number = Cards.Visa, expMonth = 1, expYear = 2020, cvc = Cvc }))
        {
            answers.Add(await expired.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage badBody = await scenario.TokeniseAsync(new { number = 4242424242424242, expMonth = 12, expYear = 2030 }))
        {
            answers.Add(await badBody.Content.ReadAsStringAsync());
        }

        // The provider is down while a card is tokenised.
        await payments.Sim.SetKnobsAsync(new { Outage = true });
        using (HttpResponseMessage down = await scenario.TokeniseAsync(new { number = Cards.Visa, expMonth = 12, expYear = 2030, cvc = Cvc }))
        {
            answers.Add(await down.Content.ReadAsStringAsync());
        }

        await payments.Sim.SetKnobsAsync(new { Outage = false });

        // Money moving: authorise, capture, refund and void on good cards, a decline, and both timeout cards, so that
        // the lookup and resend paths run too.
        PlacedPayment captured = await scenario.CapturedAsync(100m);
        Guid refund = Guid.CreateVersion7();
        await scenario.SendAsync(new PaymentRefundRequested(captured.TripId, captured.PaymentId, refund, Scenario.Eur(25m)));
        await scenario.NextAsync(nameof(PaymentRefunded), captured.TripId);

        PlacedPayment voided = await scenario.AuthorizedAsync(40m);
        await scenario.SendAsync(new PaymentVoidRequested(voided.TripId, voided.PaymentId));
        await scenario.NextAsync(nameof(PaymentVoided), voided.TripId);

        PaymentAuthorizationRequested declined = Scenario.AuthorizationFor(await scenario.TokenAsync(Cards.InsufficientFunds), 10m);
        await scenario.SendAsync(declined);
        await scenario.NextAsync(nameof(PaymentDeclined), declined.TripId);

        PlacedPayment performed = await scenario.AuthorizedAsync(60m, Cards.TimesOutButHappens);
        await scenario.SendAsync(new PaymentCaptureRequested(performed.TripId, performed.PaymentId, Scenario.Eur(60m)));
        await scenario.NextAsync(nameof(PaymentCaptured), performed.TripId);

        PlacedPayment notPerformed = await scenario.AuthorizedAsync(60m, Cards.TimesOutAndDoesNotHappen);
        await scenario.SendAsync(new PaymentVoidRequested(notPerformed.TripId, notPerformed.PaymentId));
        await scenario.NextAsync(nameof(PaymentVoided), notPerformed.TripId);

        // 1. The log is full of what happened, and the card is nowhere in it.
        payments.Host.Logs.Containing("concluded as").ShouldNotBeEmpty();
        payments.Host.Logs.Containing("looking the operation up").ShouldNotBeEmpty();
        payments.Host.Logs.Entries.Count(static e => e.StartsWith("Viatica.Payments", StringComparison.Ordinal)).ShouldBeGreaterThan(10);
        foreach (string form in AllForms)
        {
            payments.Host.Logs.Containing(form).ShouldBeEmpty($"'{form}' was logged");
        }

        payments.Host.Logs.Entries.Where(static e => e.Contains("cvc", StringComparison.OrdinalIgnoreCase))
            .ShouldNotContain(static e => e.Contains(Cvc, StringComparison.Ordinal), "a security code was logged");

        // 2. No table has it, in any text, json or binary column.
        IReadOnlyList<string> inDatabase = await ScanAsync([.. AllForms, "cvc"]);
        inDatabase.ShouldBeEmpty("a card was stored: " + string.Join(", ", inDatabase));

        // 3. No event has it. The probe has read this run's events, so a finding of none is a finding.
        await scenario.NextAsync(nameof(PaymentVoided), notPerformed.TripId);
        payments.Probe.Received.Count.ShouldBeGreaterThan(8);
        foreach (ProbedMessage message in payments.Probe.Received)
        {
            string raw = Encoding.UTF8.GetString(message.Value);
            AllForms.ShouldAllBe(form => !raw.Contains(form, StringComparison.Ordinal), $"{message.Type} carries a card");
        }

        // 4. No answer to a caller has it, error or not.
        answers.Count.ShouldBeGreaterThan(10);
        answers.ShouldAllBe(answer => AllForms.All(form => !answer.Contains(form, StringComparison.Ordinal)));
        answers.ShouldAllBe(answer => !answer.Contains(Cvc, StringComparison.Ordinal));

        // 5. The number went to the provider in token requests and in no other request. Cards.Visa and the others are
        // all sent: the count is the number of tokenise calls that carried a number we wrote down.
        IReadOnlyList<WireEntry> withNumber =
        [
            .. payments.Host.Wire.Entries.Where(e => Numbers.Any(n => e.Body.Contains(n, StringComparison.Ordinal))),
        ];
        withNumber.ShouldNotBeEmpty();
        withNumber.ShouldAllBe(static e => e.Method == HttpMethod.Post && e.Path == "/v1/tokens");
        payments.Host.Wire.Entries.Where(static e => e.Path != "/v1/tokens")
            .ShouldAllBe(static e => !e.Body.Contains(Cvc, StringComparison.Ordinal) && !e.Query.Contains(Cvc, StringComparison.Ordinal));
    }

    /// <summary>Every text, character, json or binary column of every table in the service schema that holds any of the needles.</summary>
    private async Task<IReadOnlyList<string>> ScanAsync(IEnumerable<string> needles)
    {
        List<string> hits = [];
        await using var connection = new NpgsqlConnection(payments.Host.PostgresConnection);
        await connection.OpenAsync();

        List<(string Table, string Column, string Type)> columns = [];
        await using (NpgsqlCommand list = connection.CreateCommand())
        {
            list.CommandText = """
                select table_name, column_name, data_type
                from information_schema.columns
                where table_schema = 'payments'
                  and data_type in ('text', 'character varying', 'character', 'json', 'jsonb', 'bytea')
                """;
            await using NpgsqlDataReader reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        columns.Count.ShouldBeGreaterThan(15, "the scan should be looking at the whole schema");

        foreach ((string table, string column, string type) in columns)
        {
            foreach (string needle in needles)
            {
                await using NpgsqlCommand find = connection.CreateCommand();
                // Names come from information_schema, never from input. A binary column is compared as the text it holds.
                string expression = type == "bytea" ? $"encode(\"{column}\", 'escape')" : $"\"{column}\"::text";
#pragma warning disable CA2100
                find.CommandText = $"select count(*) from payments.\"{table}\" where {expression} ilike '%' || @needle || '%'";
#pragma warning restore CA2100
                find.Parameters.AddWithValue("needle", needle);
                if ((long)(await find.ExecuteScalarAsync())! > 0)
                {
                    hits.Add($"{table}.{column}: {needle}");
                }
            }
        }

        return hits;
    }
}
