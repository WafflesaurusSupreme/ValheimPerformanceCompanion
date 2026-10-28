# GitHub build and SignPath activation

Open **Actions → Build portable companion → Run workflow** on `main`.
Leave **Request SignPath signing** unchecked to build an unsigned portable ZIP.
The workflow is manual: pushes, pull requests, and tags do not start builds or
signing requests. The workflow token has only contents/actions read permission.

The unsigned hosted build was verified on 28 October 2026 in
[workflow run #1](https://github.com/WafflesaurusSupreme/ValheimPerformanceCompanion/actions/runs/40788054355).
The full release checks passed. Its downloaded 0.1.10 portable ZIP contained
458 files with no CI-only game inputs, and the native launcher's
`--check-startup` check returned exit 0. Archive SHA-256:
`A5B88F31F0E6F8D0267AF5039F97B25244CA330B20A3CB44B2C445ED4743EA1C`.
This run was unsigned; live SignPath signing awaits provider onboarding.

The build uses a standard GitHub-hosted `windows-2025` runner, pinned official
checkout/upload/SignPath action commits, .NET SDK 10.0.401, Zig 0.16.0, and
PerfView 3.2.6. Download hashes are committed in `.signpath/tool-sources.json`.
NuGet's locked dependencies and vulnerability checks remain enabled. No paid
runner, Azure subscription, paid signing service, or signing hardware is used.

## Build inputs

Compilation and the existing runtime regressions require Valheim/Unity reference
assemblies, the Windows game Mono runtime/configuration, and BepInEx core DLLs.
`.signpath/Export-BuildInputs.ps1` selects only those files and records their
individual sizes and SHA-256 values in `inputs-manifest.json` inside the ZIP.
It excludes the game executable, saves, profile configuration, plugins, logs,
recordings and credentials. These files are build/test inputs; they are not
copied into the portable output.

The input ZIP is a separate GitHub release asset labeled **CI build inputs —
not an application download**, under the `ci-inputs-20261028` pre-release tag.
Players do not need it. The source checkout and portable application ZIP do not
include it. The exact download URL and SHA-256 are committed in
`.signpath/build-inputs.json`. No encryption or build-input secret is required.

Initialization verifies the download hash, archive paths/limits and every
manifest entry before running build tools against the inputs. No cached tools
are accepted on the hosted runner. An empty
`valheim.exe` installation marker is created only in the isolated test tree for
filesystem discovery; it is never executed. Actual Mono tests run the verified
game runtime and real managed assemblies, without starting the game.

To refresh the input bundle, export to a new local directory and review the
manifest. Upload it as a separately named asset under a new CI-input pre-release
tag, then update the committed URL and SHA-256 and run an unsigned build. Retain
old snapshots for existing commits. Do not upload the input bundle as a normal
application release or add it to the portable package or source tree. The
optional encryption utility in `.signpath/` is not used by this workflow.

## Checks run for every build

The build runs the core and recovery suites; WPF automatic setup, close,
history, retry, late-attachment and reconnection tests; and the actual Valheim
Mono transport, recorder contract, no-Unity-Update and native GC clock tests.
The build also tests unsafe-archive, hash-mismatch and unsigned-file rejection.
GUI tests are enabled on the hosted runner. A failed check stops packaging.
The artifact is a self-contained Windows x64 ZIP, retained for 14 days. Download
and extract the complete ZIP. Hosted tests do not establish in-game behavior.

## After SignPath approves the application

1. Follow SignPath's onboarding instructions and install their GitHub App for
   this repository if requested. Link the public repository to the SignPath
   project and GitHub.com trusted build system. Confirm GitHub-hosted builds
   are required and keep release signing subject to maintainer approval.
2. Create artifact configurations with the exact slugs `native-gc-v1` and
   `portable-v1`, using the matching XML files in `.signpath/`.
3. Save the provider's submitter API token as the repository Actions secret
   `SIGNPATH_API_TOKEN`. Use a submitter identity limited to this project and
   policy; no signing private key is needed in GitHub.
4. Add these repository Actions variables using the actual approved values:

   | Variable | Value |
   | --- | --- |
   | `SIGNPATH_ORGANIZATION_ID` | Organization ID supplied by SignPath |
   | `SIGNPATH_PROJECT_SLUG` | Approved project slug |
   | `SIGNPATH_SIGNING_POLICY_SLUG` | Approved release-signing policy slug |

5. Run the workflow on `main` with **Request SignPath signing** checked. Approve
   the two signing requests in SignPath when prompted. Each request waits up to
   30 minutes; the whole workflow is limited to 90 minutes. A timeout fails the
   run. Inspect the existing SignPath request before dispatching another run;
   an abandoned provider request may still require a decision there.
6. Download `ValheimPerformanceCompanion-<version>-win-x64-signed.zip` and
   `SHA256SUMS.txt` from the successful run. Check the release before publishing
   it through the normal download channel. Signing never silently falls back
   to unsigned output and the workflow does not automatically publish releases.

The first request signs `VpcGcProbe.dll` before embedding. The second signs the
desktop executable, launcher, desktop/Core assemblies and recorder. Final
verification requires trusted, timestamped SignPath Foundation signatures,
the exact signed embedded helper, and unchanged third-party payload files.

Provider approval, the issued account values/token, GitHub App permissions and
the first live signing run cannot be verified before onboarding. No certificate
has been issued and the existing 0.1.10 portable download remains unsigned.
