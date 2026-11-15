namespace QoffeeAutomat.Core.Tests;

public class CoreMarkerTests
{
    [Fact]
    public void Marker_lives_in_the_core_assembly()
    {
        Assert.Equal("QoffeeAutomat.Core", typeof(CoreMarker).Assembly.GetName().Name);
    }
}
