namespace Differ.App.Tests;

// Wall-clock UI budgets must not compete with the parallel high-DPI bitmap
// renderer or other WPF tests. Keep the production latency thresholds intact.
[CollectionDefinition("UI performance", DisableParallelization = true)]
public sealed class UiPerformanceDefinition;
