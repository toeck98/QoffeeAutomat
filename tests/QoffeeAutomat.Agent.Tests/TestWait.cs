namespace QoffeeAutomat.Agent.Tests;

/// <summary>Waits for a condition that another thread makes true.</summary>
internal static class TestWait
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Polls until the condition holds; fails the test with <paramref name="what"/> when it does not within the timeout.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Waits for a task and fails the test when it takes longer than the timeout.</summary>
    public static async Task<T> WithTimeoutAsync<T>(Task<T> task, string what, TimeSpan? timeout = null)
    {
        try
        {
            return await task.WaitAsync(timeout ?? DefaultTimeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Timed out waiting for: " + what);
            throw;
        }
    }

    /// <summary>Waits for a task and fails the test when it takes longer than the timeout.</summary>
    public static async Task WithTimeoutAsync(Task task, string what, TimeSpan? timeout = null)
    {
        try
        {
            await task.WaitAsync(timeout ?? DefaultTimeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Timed out waiting for: " + what);
        }
    }
}
