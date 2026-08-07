@echo off
setlocal

rem Prints the live SQLite schema (tables, columns, keys, indexes) exactly as
rem it exists on disk today -- the authoritative source for
rem docs/database-schema.md's per-table sections and appendix. Re-run this
rem and diff against the doc's appendix whenever a new EF Core migration
rem lands, to catch drift between what the doc describes and what's real.
rem
rem Usage: DumpSchema.cmd

set DB=%LOCALAPPDATA%\Meterist\meterist.db

sqlite3 "%DB%" ".schema"

endlocal
