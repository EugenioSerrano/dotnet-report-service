# ReportService.Migrations

The one-shot migrator of the service's metadata database.

## Purpose

It brings a database to the schema version this build expects and exits. It is
the only component that changes the schema: the API, the orchestration worker and
the PDF worker never do. The local composition starts it before the API and the
orchestration worker, which wait for it to finish successfully.

## How it works

- The scripts live in `Migrations/` and are embedded in the assembly. A script is
  named `NNNN_<slug>.sql` — four digits, an underscore, a lower-case kebab-case
  slug — so the order of the names is the order of the scripts.
- One run takes a PostgreSQL advisory lock, creates `public.schema_migrations`
  when it does not exist, compares the journal with the embedded scripts, and
  applies the missing ones in order. Each script and its journal row are written
  in one transaction.
- A script that changed after it was applied, a journal entry the build does not
  know, or a row without a name or checksum is refused: nothing is applied.
- It exits `0` when the schema is current and `1` when it refused or failed.

## Running it

```bash
ConnectionStrings__reportservice='Host=…;Username=reportservice;Password=…;Database=reportservice' \
  dotnet ReportService.Migrations.dll
```

The connection string is read from `ConnectionStrings__reportservice`; it is
never printed. See [`docs/metadata-database.md`](../../docs/metadata-database.md)
for the role, the journal, how a change adds a migration and how an operator
handles an existing deployment.

## Layout

| File | Responsibility |
|------|----------------|
| `Program.cs` | Reads the connection string, runs the migration set and turns the result into an exit code |
| `MigrationScript.cs` | One script: its name, its SQL and the SHA-256 of its bytes |
| `MigrationScriptName.cs` | The name rule, with its number and its reason for refusing a name |
| `MigrationCatalog.cs` | The embedded set, validated and ordered, and the expected version |
| `MigrationPlanner.cs` | Decides what a run applies from the journal and the embedded set |
| `MigrationJournal.cs` | Creates, reads and writes the journal table |
| `MigrationRunner.cs` | One run: the lock, the transaction per script, the refusal and failure handling |
| `MigrationResult.cs` | What a run did, as the lines it writes |
| `Migrations/` | The embedded SQL scripts |
