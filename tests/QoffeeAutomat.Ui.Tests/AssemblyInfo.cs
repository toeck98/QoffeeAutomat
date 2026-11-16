// Run bUnit test collections sequentially so MudBlazor async continuations do not race on busy CI runners.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
