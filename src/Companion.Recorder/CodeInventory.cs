using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Companion.Shared;

namespace ValheimPerformanceCompanion.Recorder;

internal static class CodeInventory
{
    internal static void Write(string directory, Assembly[] assemblies, IDictionary<Assembly, string> pluginOwners)
    {
        var warnings = new List<string>();
        using var modules = Writer(directory, "modules.csv");
        using var code = Writer(directory, "code-methods.csv");
        modules.WriteLine("Assembly,Version,Mvid,FileSha256,Owners,LocationKind");
        code.WriteLine("Assembly,Mvid,Token,Type,Method,Signature,IlSha256,IlBase64,Instructions,Locals,ExceptionClauses");
        long bytes = 0; int methods = 0;
        foreach (Assembly assembly in assemblies.OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            string name = assembly.GetName().Name ?? "";
            if (assembly.IsDynamic || name == "mscorlib" || name == "netstandard" || name == "System" || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("UnityEngine", StringComparison.Ordinal) || name.StartsWith("Unity.", StringComparison.Ordinal)) continue;
            try
            {
                string mvid = assembly.ManifestModule.ModuleVersionId.ToString(), location = assembly.Location;
                string owners = pluginOwners.TryGetValue(assembly, out string owner) ? owner : "";
                string hash = "";
                if (!string.IsNullOrEmpty(location) && File.Exists(location)) { using var stream = File.OpenRead(location); using var sha = SHA256.Create(); hash = Hex(sha.ComputeHash(stream)); }
                modules.WriteLine(string.Join(",", Q(name), Q(assembly.GetName().Version?.ToString() ?? ""), mvid, hash, Q(owners), Q(string.IsNullOrEmpty(location) ? "Embedded/in-memory; parent owner unresolved" : "File-backed; hash sampled after recording")));
                if (methods > 150000 || bytes > 64L * 1024 * 1024) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error) { types = error.Types.Where(t => t != null).Cast<Type>().ToArray(); warnings.Add(name + ": some types could not be loaded"); }
                foreach (Type type in types)
                foreach (MethodBase method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
                {
                    if (++methods > 150000 || bytes > 64L * 1024 * 1024) { warnings.Add("Code snapshot limit reached; methods outside the snapshot require inspection of the matching assembly."); goto NextAssembly; }
                    try
                    {
                        MethodBody? body = method.GetMethodBody(); byte[]? il = body?.GetILAsByteArray();
                        if (body == null || il == null || il.Length == 0) continue;
                        if (il.Length > 131072) { warnings.Add("Method IL exceeds snapshot limit: " + type.FullName + "." + method.Name); continue; }
                        using var sha = SHA256.Create(); string instructions = IlText.Dump(method, il);
                        string locals = string.Join(";", body.LocalVariables.Select(v => v.LocalIndex + ":" + v.LocalType + (v.IsPinned ? " pinned" : "")));
                        string clauses = string.Join(";", body.ExceptionHandlingClauses.Select(c => c.Flags + ":try=" + c.TryOffset + "+" + c.TryLength + ":handler=" + c.HandlerOffset + "+" + c.HandlerLength + (c.Flags == ExceptionHandlingClauseOptions.Clause ? ":catch=" + c.CatchType : c.Flags == ExceptionHandlingClauseOptions.Filter ? ":filter=" + c.FilterOffset : "")));
                        string row = string.Join(",", Q(name), mvid, "0x" + method.MetadataToken.ToString("x8"), Q(type.FullName ?? type.Name), Q(method.Name), Q(IlText.Signature(method)), Hex(sha.ComputeHash(il)), Q(Convert.ToBase64String(il)), Q(instructions), Q(locals), Q(clauses));
                        bytes += Encoding.UTF8.GetByteCount(row); code.WriteLine(row);
                    }
                    catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is NotSupportedException || error is BadImageFormatException || error is TypeLoadException || error is FileNotFoundException)
                    { warnings.Add(name + ": " + type.FullName + "." + method.Name + ": " + error.GetType().Name); }
                }
            }
            catch (Exception error) when (error is IOException || error is NotSupportedException || error is UnauthorizedAccessException || error is TypeLoadException)
            { warnings.Add(name + ": module snapshot unavailable: " + error.GetType().Name); }
            NextAssembly:;
        }
        warnings.Insert(0, "IL is the loaded assembly's original managed method body, not reconstructed C# or Harmony's generated replacement. File hashes were sampled after recording; MVID identifies the loaded module. Code collection occurs after measurement.");
        File.WriteAllLines(Path.Combine(directory, "code-quality.txt"), warnings.Take(1000), new UTF8Encoding(false));
    }
    private static StreamWriter Writer(string directory, string name) => new StreamWriter(new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "");
    private static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
