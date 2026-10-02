using Tether.Core;

namespace Tether.Tests.Unit;

public class SmokeTests
{
    [Fact]
    public void ProductNameIsTether() => Assert.Equal("Tether", TetherInfo.ProductName);
}
