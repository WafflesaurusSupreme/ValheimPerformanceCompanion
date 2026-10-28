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
