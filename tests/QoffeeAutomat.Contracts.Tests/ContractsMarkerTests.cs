namespace QoffeeAutomat.Contracts.Tests;

public class ContractsMarkerTests
{
    [Fact]
    public void Marker_lives_in_the_contracts_assembly()
    {
        Assert.Equal("QoffeeAutomat.Contracts", typeof(ContractsMarker).Assembly.GetName().Name);
    }
}
