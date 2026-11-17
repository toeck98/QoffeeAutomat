// WebApplicationFactory / HostFactoryResolver uses a process-wide DiagnosticListener; running test collections
// sequentially avoids "The entry point exited without ever building an IHost" races on busy CI runners.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
