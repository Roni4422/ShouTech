using Xunit;

// The integration suite shares process-wide AppState, AppHost permissions,
// and connection settings. Running these fixtures concurrently lets one test
// replace another test's active company/database and also prevents safe SQLite
// cleanup. Keep the suite deterministic until those globals are isolated.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
