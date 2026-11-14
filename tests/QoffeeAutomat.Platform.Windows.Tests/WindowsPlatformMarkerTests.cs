namespace QoffeeAutomat.Platform.Windows.Tests;

public class WindowsPlatformMarkerTests
{
    [Fact]
    public void Marker_lives_in_the_windows_platform_assembly()
    {
        Assert.Equal("QoffeeAutomat.Platform.Windows", typeof(WindowsPlatformMarker).Assembly.GetName().Name);
    }
}
