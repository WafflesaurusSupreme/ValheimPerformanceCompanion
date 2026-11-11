using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Companion.Core;

public sealed record RecorderCapabilities(Version Version, string SHA256, bool ParticleCensus, bool DisableCleanup, bool EarlyCommandValidation)
{
    public bool Preserves(RecorderCapabilities installed) => Version >= installed.Version
        && (!installed.ParticleCensus || ParticleCensus) && (!installed.DisableCleanup || DisableCleanup)
        && (!installed.EarlyCommandValidation || EarlyCommandValidation);

    public static RecorderCapabilities Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)); stream.Position = 0;
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var types = metadata.TypeDefinitions.ToDictionary(handle => Name(handle), handle => handle, StringComparer.Ordinal);
        const string prefix = "ValheimPerformanceCompanion.Recorder.";
        bool HasType(string name) => types.ContainsKey(prefix + name);
        bool Calls(string type, string method, string targetType, string targetMethod) => MethodCalls(type, method).Any(call => call.Type == prefix + targetType && call.Method == targetMethod);
        bool census = HasType("ParticleCensus") && HasType("ParticleCensusPlan")
            && types.TryGetValue(prefix + "Recording+Receipt", out var receipt)
            && metadata.GetTypeDefinition(receipt).GetProperties().Any(property => metadata.GetString(metadata.GetPropertyDefinition(property).Name) == "SupportsParticleCensus")
            && Calls("DetailedTrace", "Prepare", "ParticleCensus", ".ctor")
            && Calls("DetailedTrace", "Begin", "ParticleCensus", "Begin")
            && Calls("DetailedTrace", "SampleFrame", "ParticleCensus", "Sample")
            && Calls("DetailedTrace", "End", "ParticleCensus", "End")
            && Calls("DetailedTrace", "Save", "ParticleCensus", "Save");
        bool disable = Calls("RecorderPlugin", "OnDisable", "RecorderPlugin", "Stop");
        bool early = types.Where(type => type.Key.StartsWith(prefix + "RecorderPlugin+<ServerLoop>", StringComparison.Ordinal))
            .Any(type => MethodCalls(type.Key[prefix.Length..], "MoveNext").Any(call => call.Type == prefix + "RecorderPlugin" && call.Method == "IsSupportedRecorderOperation"));
        return new(metadata.GetAssemblyDefinition().Version, hash, census, disable, early);

        string Name(TypeDefinitionHandle handle)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var parent = definition.GetDeclaringType();
            return parent.IsNil ? metadata.GetString(definition.Namespace) + "." + metadata.GetString(definition.Name)
                : Name(parent) + "+" + metadata.GetString(definition.Name);
        }

        IEnumerable<(string Type, string Method)> MethodCalls(string type, string method)
        {
            if (!types.TryGetValue(prefix + type, out var handle)) yield break;
            foreach (var methodHandle in metadata.GetTypeDefinition(handle).GetMethods())
            {
                var definition = metadata.GetMethodDefinition(methodHandle);
                if (metadata.GetString(definition.Name) != method || definition.RelativeVirtualAddress == 0) continue;
                byte[] code = pe.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes() ?? [];
                foreach (int token in CallTokens(code))
                {
                    var target = MetadataTokens.EntityHandle(token);
                    if (target.Kind == HandleKind.MethodDefinition)
                    {
                        var called = metadata.GetMethodDefinition((MethodDefinitionHandle)target);
                        yield return (Name(called.GetDeclaringType()), metadata.GetString(called.Name));
                    }
                    else if (target.Kind == HandleKind.MemberReference)
                    {
                        var called = metadata.GetMemberReference((MemberReferenceHandle)target);
                        if (called.Parent.Kind == HandleKind.TypeDefinition)
                            yield return (Name((TypeDefinitionHandle)called.Parent), metadata.GetString(called.Name));
                    }
                }
            }
        }
    }

    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(opcode => opcode.Value);

    private static IEnumerable<int> CallTokens(byte[] code)
    {
        int offset = 0;
        while (offset < code.Length)
        {
            short value = code[offset++];
            if (value == 0xfe) { if (offset == code.Length) throw new BadImageFormatException("Truncated recorder IL."); value = (short)(0xfe00 | code[offset++]); }
            if (!Opcodes.TryGetValue(value, out var opcode)) throw new BadImageFormatException("Unknown recorder IL opcode.");
            int length = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => offset + 4 <= code.Length ? checked(4 + 4 * BitConverter.ToInt32(code, offset)) : throw new BadImageFormatException("Truncated recorder switch."),
                _ => 4
            };
            if (length < 0 || length > code.Length - offset) throw new BadImageFormatException("Truncated recorder IL operand.");
            if (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj) yield return BitConverter.ToInt32(code, offset);
            offset += length;
        }
    }
}
