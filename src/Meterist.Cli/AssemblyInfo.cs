using System.Runtime.Versioning;

// Meterist.Cli calls into Meterist.Secrets' DPAPI-backed registration, which
// is Windows-only by design (docs/architecture.md §6/§9 — local deployment
// for v1, on the operator's own Windows machine).
[assembly: SupportedOSPlatform("windows")]
