using Xunit;

// WebApplicationFactory's deferred-host initialization races when test classes run
// in parallel (multiple TestServers starting concurrently in one process), which
// manifests as "The server has not been started or no web application was configured".
// Serializing test classes fixes it deterministically.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
