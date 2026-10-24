namespace Viatica.Contracts;

/// <summary>Applies one integration event to the consuming service's state.</summary>
/// <remarks>
/// <para>
/// It lives here rather than in Viatica.Kernel.Messaging so that a service's Application layer, which may not
/// reference the Kafka library, can implement it. Kernel.Messaging resolves it per message, in a scope of its own.
/// </para>
/// <para>
/// Change the service's DbContext (through <c>I&lt;Service&gt;Db</c>) and do not commit: the consumer saves the
/// changes together with the inbox row, in one transaction, after this returns. Throwing means "try again", up to
/// the subscription's attempt limit; then the message goes to the dead letter topic.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It handles integration events; it is not a .NET event delegate, and the name is the one the design uses.")]
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent message, CancellationToken cancellationToken);
}
