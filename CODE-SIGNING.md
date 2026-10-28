# Code signing

The SignPath Foundation application was submitted on 28 October 2026. Approval
and hosted-build integration are pending. No sponsored signing certificate has
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
token or private key belongs in this repository. Hosted build integration is not configured yet; the configuration files here are
preparation for enrollment, not evidence that any binary is signed.

Provider: https://signpath.org/
Trusted-build documentation: https://docs.signpath.io/trusted-build-systems/github
