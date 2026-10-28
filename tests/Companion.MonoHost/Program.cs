using System.Runtime.InteropServices;

if (args.Length < 4) throw new ArgumentException("Usage: MonoHost monoRuntimeDirectory managedDirectory testExe testArguments...");
IntPtr library = NativeLibrary.Load(Path.Combine(args[0], "mono-2.0-bdwgc.dll"));
T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
Export<SetDirs>("mono_set_dirs")(args[1], Path.GetFullPath(Path.Combine(args[0], "..", "etc")));
Export<SetPath>("mono_set_assemblies_path")(args[1]);
IntPtr domain = Export<Init>("mono_jit_init_version")("PackRegression", "v4.0.30319");
if (domain == IntPtr.Zero) throw new InvalidOperationException("Mono initialization failed");
IntPtr assembly = Export<Open>("mono_domain_assembly_open")(domain, Path.GetFullPath(args[2]));
if (assembly == IntPtr.Zero) throw new InvalidOperationException("Mono test assembly load failed");
IntPtr[] values = args.Skip(2).Select(Marshal.StringToCoTaskMemUTF8).ToArray();
IntPtr argv = Marshal.AllocHGlobal(IntPtr.Size * values.Length);
try
{
    Marshal.Copy(values, 0, argv, values.Length);
    return Export<Exec>("mono_jit_exec")(domain, assembly, values.Length, argv);
}
finally
{
    Marshal.FreeHGlobal(argv);
    foreach (IntPtr value in values) Marshal.FreeCoTaskMem(value);
    Export<Cleanup>("mono_jit_cleanup")(domain);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetDirs([MarshalAs(UnmanagedType.LPUTF8Str)] string assemblies, [MarshalAs(UnmanagedType.LPUTF8Str)] string config);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Init([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string version);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Open(IntPtr domain, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Exec(IntPtr domain, IntPtr assembly, int argc, IntPtr argv);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Cleanup(IntPtr domain);
