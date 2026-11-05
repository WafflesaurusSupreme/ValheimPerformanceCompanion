# Privacy

Valheim Performance Companion records and analyzes performance measurements on
the user's computer. It stores local settings such as selected game/profile
locations and recent captures, and writes application diagnostic logs locally.

Depending on capture mode, recordings can include frame timing, process and
system information, game logs, CPU traces, loaded plugin identities, configuration
fingerprints and managed-code diagnostic snapshots. Raw system traces can contain
information about processes other than Valheim.

The companion does not automatically upload recordings or reports. The user can
export a report and share it through a service of their choice. Redacted exports
remove common identifiers, but automatic redaction is not a guarantee that every
private string has been removed. Full diagnostics and raw traces require the
user's review before sharing.

Build and signing tools may contact package servers, tool download sites,
timestamp services and the explicitly configured signing provider when those
tools are invoked. These operations are separate from recording game performance.
Any service the user chooses to share files with has its own privacy practices.
# Installed collector candidate (0.1.11)

The protected Windows collector service performs the same machine-wide ETW collection used by earlier Standard and Deep modes. CPU analysis selects the Valheim PID; raw ETL can contain other processes' activity and file/process metadata. Installing the profiling service authorizes that capability; package identity and caller/game ownership are checked on every request. The BepInEx recorder continues to collect inside Valheim. Raw ETL remains excluded from normal evidence exports unless explicitly selected.

Service traces are staged under `%PROGRAMDATA%/ValheimPerformanceCompanion/Collector/<Windows SID>/<session ID>` with System/Administrators write access and the requesting user's read access. The desktop copies completed traces with the user's own permissions. This installation candidate currently retains protected staging copies; they are not uploaded automatically. Store and code-signing account identity verification is performed directly with the providers, not through the application.
