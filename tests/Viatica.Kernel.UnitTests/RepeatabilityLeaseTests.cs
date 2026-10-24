using System.Globalization;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Viatica.Kernel.Web;

namespace Viatica.Kernel.UnitTests;

public sealed class RepeatabilityLeaseTests(TestApi api) : IClassFixture<TestApi>
{
    private const string Body = """{"amount":{"amount":"40.00","currency":"EUR"}}""";

    private string FirstSent => api.Clock.GetUtcNow().ToString("r", CultureInfo.InvariantCulture);

    private Task<HttpResponseMessage> Send(string path, string id, string firstSent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Http.V(path)) { Content = Http.Json(Body) };
        request.Headers.Add(TestApi.UserHeader, "alice");
        request.Headers.Add("Repeatability-Request-ID", id);
        request.Headers.Add("Repeatability-First-Sent", firstSent);
        return api.Client.SendAsync(request);
    }

    /// <summary>
    /// The handler runs for two minutes of the clock, four times the lease. A copy sent meanwhile, with a
    /// <c>First-Sent</c> the window still accepts, must find the claim and get 409. Without renewal the claim lapses
    /// after one lease and the copy would run the handler a second time.
    /// </summary>
    [Fact]
    public async Task A_handler_that_runs_past_its_lease_keeps_its_claim_so_a_copy_is_not_run_beside_it()
    {
        string id = Guid.NewGuid().ToString();
        string firstSent = FirstSent;
        Task<HttpResponseMessage> first = Send("/refunds/slow", id, firstSent);
        while (Volatile.Read(ref api.Counters.Slow) == 0)
        {
            await Task.Delay(10);
        }

        for (int step = 0; step < 12; step++)
        {
            int renewals = Volatile.Read(ref api.Counters.Renewals);
            api.Clock.Advance(TimeSpan.FromSeconds(10));
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref api.Counters.Renewals) == renewals)
            {
                (DateTime.UtcNow < deadline).ShouldBeTrue("the running request did not renew its claim");
                await Task.Delay(10);
            }
        }

        var copy = await Send("/refunds/slow", id, firstSent);
        api.Counters.SlowGate.SetResult();

        await copy.ShouldBeError(HttpStatusCode.Conflict, "Conflict");
        (await first).StatusCode.ShouldBe(HttpStatusCode.OK);
        api.Counters.Slow.ShouldBe(1);
    }
}
