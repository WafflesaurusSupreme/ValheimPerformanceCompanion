using Companion.Collector;

// Only SCM can run the collector. It has no interactive elevation or console mode.
return ServiceHost.Run(async cancellation =>
{
    ServiceSecurity.ValidateInstallation(AppContext.BaseDirectory);
    await using var server = new CollectorServer();
    await server.RunAsync(cancellation).ConfigureAwait(false);
});
