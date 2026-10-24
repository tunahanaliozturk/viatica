using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Viatica.Kernel.Validation;

namespace Viatica.Kernel.UnitTests;

public sealed class ValidationBehaviorTests
{
    private static readonly CreateTrip Valid = new("Lisbon", 2, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 4));

    [Fact]
    public async Task With_no_validator_the_handler_runs()
    {
        var behavior = new ValidationBehavior<CreateTrip, string>([]);

        (await behavior.Handle(Valid, static _ => Task.FromResult("handled"), CancellationToken.None)).ShouldBe("handled");
    }

    [Fact]
    public async Task Every_validator_runs_and_the_handler_does_not()
    {
        bool handled = false;
        var behavior = new ValidationBehavior<CreateTrip, string>([new CreateTripValidator(), new Fixed("Name", "taken", "NameTaken")]);

        var error = await Should.ThrowAsync<ApiErrorException>(() => behavior.Handle(
            Valid with { Guests = 0 },
            _ =>
            {
                handled = true;
                return Task.FromResult("handled");
            },
            CancellationToken.None));

        handled.ShouldBeFalse();
        error.Category.ShouldBe(ErrorCategory.InvalidRequest);
        error.Error.Code.ShouldBe(ApiErrorCode.InvalidRequest);
        error.Error.Details!.Select(static d => d.Code).ShouldBe(["GuestsOutOfRange", "NameTaken"]);
    }

    [Fact]
    public async Task A_rule_without_a_code_gets_InvalidValue_and_targets_are_camel_cased()
    {
        var behavior = new ValidationBehavior<CreateTrip, string>([new Fixed("Items[2].PassengerID", "bad", "")]);

        var error = await Should.ThrowAsync<ApiErrorException>(() =>
            behavior.Handle(Valid, static _ => Task.FromResult("handled"), CancellationToken.None));

        ApiError detail = error.Error.Details!.Single();
        detail.Code.ShouldBe(ApiErrorCode.InvalidValue);
        detail.Target.ShouldBe("items[2].passengerID");
    }

    private sealed class Fixed(string parameter, string message, string code) : IValidator<CreateTrip>
    {
        public GuardResult Validate(CreateTrip value) => GuardResult.Failure(parameter, message, code);

        public GuardResult Validate(CreateTrip value, ValidationContext context) => Validate(value);

        public Task<GuardResult> ValidateAsync(CreateTrip value, CancellationToken cancellationToken = default) =>
            Task.FromResult(Validate(value));

        public Task<GuardResult> ValidateAsync(CreateTrip value, ValidationContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(Validate(value));
    }
}
