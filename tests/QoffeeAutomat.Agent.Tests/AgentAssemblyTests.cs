namespace QoffeeAutomat.Agent.Tests;

public class AgentAssemblyTests
{
    [Fact]
    public void Program_lives_in_the_agent_assembly()
    {
        Assert.Equal("QoffeeAutomat.Agent", typeof(Program).Assembly.GetName().Name);
    }
}
