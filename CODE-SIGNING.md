# Code signing

The SignPath Foundation application submitted on 28 October 2026 was declined
because the project lacks the required public visibility. No sponsored certificate
is pending. The existing 0.1.10 portable release is unsigned and has not been replaced.

0.1.11 adds a Windows collector service and Microsoft Store MSIX packaging. The
Store signs certified MSIX packages at no signing cost, but the recorder and
embedded native GC DLL are copied outside the installed package and require
their own public signatures. The paid-signing limit is less than USD 20 total;
no paid account or signing resource has been provisioned. Any Artifact Signing
use must remain within a single Basic billing period, including applicable tax,
and be cancelled after successful timestamped signing. Account verification and
Microsoft Store approval remain outstanding.

Production `Build-StorePackage.ps1` rejects unsigned first-party files and checks
the exact native resource embedded in the recorder. `-ValidationOnly` builds a
clearly labeled unsigned local-check package and does not assert public trust.

`Sign-AzureFiles.ps1` accepts explicitly supplied Microsoft-signed client tools,
an existing account's non-secret metadata and an expected verified publisher.
It does not create paid resources or subscribe. The native GC DLL must be signed
first, then embedded using `Build.ps1 -PrebuiltGcProbePath` with the actual
`-ExpectedSigningPublisher`. Sign the seven first-party published files last.
The signing script's real provider flow remains untested until account validation
and client-tool setup are available. Do not start billing while Store service
capability eligibility is unresolved; capability approval is not guaranteed.

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
second signing request, all seven first-party release files must have valid,
timestamped public signatures from SignPath Foundation. The final check confirms
that the recorder embeds the exact signed helper and all other packaged files
are unchanged. A failed or timed-out signing request cannot produce a final
artifact labeled signed. The workflow never publishes a GitHub Release itself.

Provider: https://signpath.org/
Trusted-build documentation: https://docs.signpath.io/trusted-build-systems/github
