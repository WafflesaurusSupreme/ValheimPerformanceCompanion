# Third-party components

## Native launcher toolchain and runtime

Built using official Zig 0.16.0 for Windows x64 (build tool only, not shipped). Source: https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip . Archive SHA-256: `68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e`.

The native launcher statically links toolchain support libraries. Upstream Zig, LLVM libc++, libc++abi, libunwind and MinGW license/copyright notices are retained verbatim under `Licenses/NativeLauncher`.

## Microsoft PerfView 3.2.6

Official binary: https://github.com/microsoft/perfview/releases/download/v3.2.6/PerfView.exe

SHA-256: `84B8523F7FB4783FD0BAAE6B080ADB1B5AAC388192145AD713E504C69954556D`

The GitHub release asset digest matched this hash and Windows reported a valid Authenticode signature at download time. Source and notices: https://github.com/microsoft/perfview/tree/v3.2.6

The bundled binary retains its embedded components and EULA. The app invokes PerfView as a separate executable and accepts its EULA when the user explicitly starts a PerfView capture. See the separately included upstream `PerfView-LICENSE.txt`.

## Microsoft .NET

Desktop: .NET 10 / Windows Desktop runtime. Source, license and notices: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf . Runtime notices are retained in the portable publish output.

## Microsoft TraceEvent 3.2.6

Package: https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/3.2.6

Source: https://github.com/microsoft/perfview . MIT licensed; the upstream MIT license is included as `PerfView-LICENSE.txt`. Direct and transitive versions and package content hashes are recorded in `src/Companion.Core/packages.lock.json`. The Microsoft Diagnostics.NETCore.Client, Extensions and Reflection.TypeExtensions dependencies are also Microsoft open-source components. Package licenses are included under `Licenses` in the release. No game code or assemblies are redistributed.

## BepInEx, Harmony and Unity/Valheim

Recorder build references are resolved against the player's existing installation; these game/runtime assemblies are not included in the release. The recorder's detailed probes and character tracing are adapted from this workspace's existing Riverheim diagnostic recorder, with a separate namespace and Harmony owner.
