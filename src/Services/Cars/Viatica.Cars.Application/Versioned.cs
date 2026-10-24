namespace Viatica.Cars.Application;

/// <summary>
/// A resource and the row version it was read at. The Api turns the version into the <c>ETag</c>; a version of
/// zero means "no row yet" (partner settings before the first save) and gets no ETag.
/// </summary>
public sealed record Versioned<T>(T Value, uint Version, bool Created = false);
