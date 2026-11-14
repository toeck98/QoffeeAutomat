using QoffeeAutomat.Ui.Components;

namespace QoffeeAutomat.Ui.Tests;

public class UiComponentsAssemblyTests
{
    [Fact]
    public void App_component_lives_in_the_component_library()
    {
        Assert.Equal("QoffeeAutomat.Ui.Components", typeof(App).Assembly.GetName().Name);
    }
}
