using Xunit.Abstractions;
using Xunit.Sdk;

namespace Pairnets.Browser.Tests;

/// <summary>The place of a test in <see cref="WebsiteTests"/>: the flows build on one server, and the guard comes last.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StepAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

/// <summary>Runs a class's tests in the order of their <see cref="StepAttribute"/>.</summary>
public sealed class StepOrderer : ITestCaseOrderer
{
    public const string Name = "Pairnets.Browser.Tests.StepOrderer";
    public const string Assembly = "Pairnets.Browser.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(t => t.TestMethod.Method.GetCustomAttributes(typeof(StepAttribute).AssemblyQualifiedName!)
            .Select(a => (int)a.GetConstructorArguments().First())
            .DefaultIfEmpty(int.MaxValue)
            .First());
}
