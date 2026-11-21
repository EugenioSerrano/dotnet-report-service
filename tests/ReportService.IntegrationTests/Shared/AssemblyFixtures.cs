using ReportService.IntegrationTests.Shared;

// The run-level shared dependencies, owned once per test assembly and reused across the
// suite. Only PostgreSQL is shared: its per-test own-database mechanism provably isolates
// every case. Temporal is NOT shared — the worker task-queue names are constants reused
// across the whole suite in the default namespace, so a shared engine's workers would
// drain one another's tasks; isolating that would require rewriting every worker host's
// namespace, which cannot be proven safe here (SPEC §6 B.3 STOP gate). Temporal keeps its
// per-class engine, and Valkey/filesystem storage are unchanged.
[assembly: AssemblyFixture(typeof(SharedPostgres))]

// The seed-driven whole-class/collection ordering for the order-randomized covering run
// (SPEC AC-4). The built-in xUnit v3 run seed does not reorder whole collections, so this
// orderer is the named fallback; it reads the seed from REPORTSERVICE_IT_SEED and the
// covering run records the value.
[assembly: Xunit.TestCollectionOrderer(typeof(SeedTestCollectionOrderer))]
