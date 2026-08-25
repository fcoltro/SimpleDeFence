using Xunit;

// LocTests mutates Loc's process-wide static culture state (SetCulture) mid-test. xUnit's
// default parallelism runs test classes in different collections concurrently, which would let
// FirewallModeInfoTests/ExceptionDescriptorTests observe a culture LocTests has switched away
// from English. The whole suite runs in single-digit milliseconds, so serializing it costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
