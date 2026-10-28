# Code signing

The SignPath Foundation application was submitted on 28 October 2026. Approval
is pending. No sponsored signing certificate has
been issued for this project. The existing 0.1.10 portable release is unsigned.

The intended signing scope is this project's own Windows executables and
assemblies, built from the public source repository on GitHub-hosted runners.
Third-party executables and runtime assemblies retain their existing signatures.
The files selected for signing are listed explicitly in
`.signpath/portable-v1.xml` and `.signpath/native-gc-v1.xml`.

The native GC helper must be signed before it is embedded in the recorder. The
recorder, desktop assemblies and native launcher must be signed after their last
build step and before release packaging. Signed artifacts must pass Authenticode
publisher, trust-chain and timestamp verification before publication.

Signing approval will remain a maintainer-controlled release step. No signing API
token or private key belongs in this repository. The manual GitHub workflow is
`.github/workflows/portable.yml`; setup and activation instructions are in
[GITHUB_BUILD.md](GITHUB_BUILD.md). An unsigned build does not call SignPath.
Signing is restricted to `main` and fails if provider settings are missing.

The signed native helper is checked before building the recorder. After the
second signing request, all five first-party release files must have valid,
timestamped public signatures from SignPath Foundation. The final check confirms
that the recorder embeds the exact signed helper and all other packaged files
are unchanged. A failed or timed-out signing request cannot produce a final
artifact labeled signed. The workflow never publishes a GitHub Release itself.

Provider: https://signpath.org/
Trusted-build documentation: https://docs.signpath.io/trusted-build-systems/github
