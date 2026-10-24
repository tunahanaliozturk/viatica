namespace Viatica.PspSim.UnitTests;

public sealed class CardTests
{
    [Fact]
    public void Every_test_card_passes_the_Luhn_check_or_the_sim_would_refuse_its_own_cards()
    {
        Cards.All.ShouldAllBe(card => Cards.PassesLuhn(card.Number));
    }

    [Theory]
    [InlineData("4242424242424241")] // one digit off
    [InlineData("4242 4242 4242 4242")] // spaces are the caller's to strip
    [InlineData("424242")] // too short
    [InlineData("")]
    [InlineData("abcdabcdabcdabcd")]
    public void A_number_that_fails_the_check_is_refused(string number)
    {
        Cards.PassesLuhn(number).ShouldBeFalse();
    }

    [Theory]
    [InlineData("4242424242424242", "Approve")]
    [InlineData("5555555555554444", "Approve")]
    [InlineData("378282246310005", "Approve")]
    [InlineData("4000000000000002", "Decline")]
    [InlineData("4000000000009995", "InsufficientFunds")]
    [InlineData(Cards.TimeoutPerformedNumber, "TimeoutPerformed")]
    [InlineData(Cards.TimeoutNotPerformedNumber, "TimeoutNotPerformed")]
    public void The_number_decides_the_behaviour(string number, string expected)
    {
        Cards.Find(number)!.Behaviour.ToString().ShouldBe(expected);
    }

    [Fact]
    public void A_valid_looking_number_that_is_not_a_test_card_is_unknown()
    {
        // A control for the lookups above: 4111111111111111 passes Luhn and is still not one of ours.
        Cards.PassesLuhn("4111111111111111").ShouldBeTrue();
        Cards.Find("4111111111111111").ShouldBeNull();
    }
}
