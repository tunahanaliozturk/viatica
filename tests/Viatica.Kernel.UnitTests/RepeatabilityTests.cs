using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Viatica.Kernel.UnitTests;

public sealed class RepeatabilityTests(TestApi api) : IClassFixture<TestApi>
{
    private const string Body = """{"amount":{"amount":"40.00","currency":"EUR"}}""";

    private HttpClient Client => api.Client;

    private static string Now => DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture);

    private Task<HttpResponseMessage> Send(
        string path, string? id, string? firstSent = null, string body = Body, string user = "alice")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Http.V(path)) { Content = Http.Json(body) };
        request.Headers.Add(TestApi.UserHeader, user);
        if (id is not null)
        {
            request.Headers.Add("Repeatability-Request-ID", id);
            request.Headers.Add("Repeatability-First-Sent", firstSent ?? Now);
        }

        return Client.SendAsync(request);
    }

    [Fact]
    public async Task A_retry_gets_the_first_response_without_running_again()
    {
        string id = Guid.NewGuid().ToString();
        int before = api.Counters.Refunds;

        var first = await Send("/refunds", id);
        var retry = await Send("/refunds", id);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await retry.Content.ReadAsStringAsync()).ShouldBe(await first.Content.ReadAsStringAsync());
        retry.Headers.Location.ShouldBe(first.Headers.Location);
        first.Headers.GetValues("Repeatability-Result").Single().ShouldBe("accepted");
        retry.Headers.GetValues("Repeatability-Result").Single().ShouldBe("accepted");
        api.Counters.Refunds.ShouldBe(before + 1);
    }

    [Fact]
    public async Task Without_the_header_every_request_runs()
    {
        int before = api.Counters.Refunds;

        await Send("/refunds", null);
        await Send("/refunds", null);

        api.Counters.Refunds.ShouldBe(before + 2);
    }

    [Fact]
    public async Task The_same_id_from_another_caller_is_a_different_request()
    {
        string id = Guid.NewGuid().ToString();
        int before = api.Counters.Refunds;

        await Send("/refunds", id, user: "alice");
        var bob = await Send("/refunds", id, user: "bob");

        bob.StatusCode.ShouldBe(HttpStatusCode.Created);
        api.Counters.Refunds.ShouldBe(before + 2);
    }

    [Fact]
    public async Task The_same_id_with_a_different_body_is_refused()
    {
        string id = Guid.NewGuid().ToString();
        await Send("/refunds", id);

        var response = await Send("/refunds", id, body: """{"amount":{"amount":"4000.00","currency":"EUR"}}""");

        await response.ShouldBeError(HttpStatusCode.BadRequest, "InvalidRequest");
        response.Headers.GetValues("Repeatability-Result").Single().ShouldBe("rejected");
    }

    [Fact]
    public async Task A_first_sent_outside_the_window_is_refused_and_does_not_run()
    {
        int before = api.Counters.Refunds;
        string old = DateTimeOffset.UtcNow.AddDays(-2).ToString("r", CultureInfo.InvariantCulture);

        var response = await Send("/refunds", Guid.NewGuid().ToString(), old);

        JsonElement error = await response.ShouldBeError(HttpStatusCode.BadRequest, "InvalidRequest");
        error.Str("target").ShouldBe("Repeatability-First-Sent");
        response.Headers.GetValues("Repeatability-Result").Single().ShouldBe("rejected");
        api.Counters.Refunds.ShouldBe(before);
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("2026-12-01T14:00:00Z")]
    public async Task A_first_sent_that_is_not_an_http_date_is_refused(string firstSent) =>
        await (await Send("/refunds", Guid.NewGuid().ToString(), firstSent)).ShouldBeError(HttpStatusCode.BadRequest, "InvalidRequest");

    [Fact]
    public async Task A_domain_refusal_is_kept_and_replayed()
    {
        string id = Guid.NewGuid().ToString();
        int before = api.Counters.Declined;

        await (await Send("/refunds/declined", id)).ShouldBeError(HttpStatusCode.Conflict, "RefundWindowClosed");
        var retry = await Send("/refunds/declined", id);

        await retry.ShouldBeError(HttpStatusCode.Conflict, "RefundWindowClosed");
        retry.Headers.GetValues("Repeatability-Result").Single().ShouldBe("accepted");
        api.Counters.Declined.ShouldBe(before + 1);
    }

    [Fact]
    public async Task A_server_failure_is_not_kept_so_the_retry_runs()
    {
        string id = Guid.NewGuid().ToString();

        await (await Send("/refunds/flaky", id)).ShouldBeError(HttpStatusCode.InternalServerError, "InternalError");
        var retry = await Send("/refunds/flaky", id);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        api.Counters.Flaky.ShouldBe(2);
    }

    [Fact]
    public async Task A_retry_while_the_first_is_still_running_is_a_409()
    {
        string id = Guid.NewGuid().ToString();
        Task<HttpResponseMessage> first = Send("/refunds/slow", id);
        while (Volatile.Read(ref api.Counters.Slow) == 0)
        {
            await Task.Delay(10);
        }

        var retry = await Send("/refunds/slow", id);
        api.Counters.SlowGate.SetResult();

        await retry.ShouldBeError(HttpStatusCode.Conflict, "Conflict");
        retry.Headers.GetValues("Repeatability-Result").Single().ShouldBe("rejected");
        (await first).StatusCode.ShouldBe(HttpStatusCode.OK);
        api.Counters.Slow.ShouldBe(1);
    }

    [Fact]
    public async Task Of_many_identical_requests_arriving_together_exactly_one_runs()
    {
        const int copies = 32;
        string id = Guid.NewGuid().ToString();
        string firstSent = Now;
        using var start = new SemaphoreSlim(0);

        Task<HttpResponseMessage>[] requests = [.. Enumerable.Range(0, copies).Select(_ => Task.Run(async () =>
        {
            await start.WaitAsync();
            return await Send("/refunds/racy", id, firstSent);
        }))];
        start.Release(copies);
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        api.Counters.Racy.ShouldBe(1);
        responses.Count(static r => r.StatusCode == HttpStatusCode.Created).ShouldBeGreaterThanOrEqualTo(1);
        responses.ShouldAllBe(static r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        foreach (HttpResponseMessage conflict in responses.Where(static r => r.StatusCode == HttpStatusCode.Conflict))
        {
            await conflict.ShouldBeError(HttpStatusCode.Conflict, "Conflict");
        }

        // Once the first has finished, a straggler gets its response replayed rather than a 409.
        var late = await Send("/refunds/racy", id, firstSent);
        late.StatusCode.ShouldBe(HttpStatusCode.Created);
        late.Headers.Location!.ToString().ShouldBe("/refunds/1");
        api.Counters.Racy.ShouldBe(1);
    }
}
