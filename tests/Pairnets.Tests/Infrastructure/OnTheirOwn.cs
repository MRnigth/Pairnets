namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// Short tests of how long something waits (a pipe that opens late, a file that comes and goes). Next to the rest of
/// the suite on a small runner their threads can start seconds late, so they run on their own, after the others.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OnTheirOwn
{
    public const string Name = "Timing, on their own";
}
