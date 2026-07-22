using System.Runtime.Versioning;

// v1 credential storage is DPAPI-backed by design (docs/architecture.md §6) —
// intentionally Windows-only, not a portability gap to fix.
[assembly: SupportedOSPlatform("windows")]
