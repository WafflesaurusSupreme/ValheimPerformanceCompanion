using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Companion.Shared;

// Exact IL bytes and metadata operands from the loaded assembly. This does not
// reconstruct source or claim to represent Harmony's generated replacement body.
internal static class IlText
{
    private static readonly Dictionary<ushort, OpCode> Codes = BuildCodes();
    private static Dictionary<ushort, OpCode> BuildCodes()
    {
        var result = new Dictionary<ushort, OpCode>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is OpCode code) result[unchecked((ushort)code.Value)] = code;
        return result;
    }
    internal static string Signature(MethodBase method) => (method.DeclaringType?.FullName ?? "<global>") + "." + method.Name + "(" + string.Join(",", Array.ConvertAll(method.GetParameters(), p => p.ParameterType.ToString())) + ")";
    internal static string Dump(MethodBase method, byte[] bytes)
    {
        var output = new StringBuilder(); int index = 0;
        while (index < bytes.Length)
        {
            int offset = index; ushort value = bytes[index++];
            if (value == 0xfe) { if (index >= bytes.Length) throw new BadImageFormatException("Truncated IL opcode"); value = (ushort)(0xfe00 | bytes[index++]); }
            if (!Codes.TryGetValue(value, out OpCode code)) throw new BadImageFormatException("Unknown IL opcode");
            int size = code.OperandType switch {
                OperandType.InlineNone => 0, OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
                OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => index + 4 <= bytes.Length ? checked(4 + 4 * BitConverter.ToInt32(bytes, index)) : -1, _ => 4 };
            if (size < 0 || index + (long)size > bytes.Length) throw new BadImageFormatException("Truncated IL operand");
            string operand = size == 0 ? "" : BitConverter.ToString(bytes, index, size);
            if (code.OperandType == OperandType.InlineBrTarget) operand = "IL_" + (index + size + BitConverter.ToInt32(bytes, index)).ToString("x4");
            else if (code.OperandType == OperandType.ShortInlineBrTarget) operand = "IL_" + (index + size + unchecked((sbyte)bytes[index])).ToString("x4");
            else if (code.OperandType == OperandType.InlineI) operand = BitConverter.ToInt32(bytes, index).ToString(CultureInfo.InvariantCulture);
            else if (code.OperandType == OperandType.ShortInlineI) operand = unchecked((sbyte)bytes[index]).ToString(CultureInfo.InvariantCulture);
            if (size == 4 && (code.OperandType == OperandType.InlineMethod || code.OperandType == OperandType.InlineField || code.OperandType == OperandType.InlineType || code.OperandType == OperandType.InlineTok))
            {
                int token = BitConverter.ToInt32(bytes, index);
                try {
                    MemberInfo? member = method.Module.ResolveMember(token, method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                    operand = "0x" + token.ToString("x8", CultureInfo.InvariantCulture) + " " + (member is MethodBase called ? Signature(called) : member is Type resolvedType ? resolvedType.FullName : member?.DeclaringType?.FullName + "." + member?.Name);
                } catch (Exception error) when (error is ArgumentException || error is BadImageFormatException || error is TypeLoadException || error is MissingMemberException) { operand = "0x" + token.ToString("x8") + " [unresolved metadata]"; }
            }
            // String contents may contain credentials or user data. Their metadata
            // tokens identify the exact IL operand without copying the value.
            output.Append("IL_").Append(offset.ToString("x4")).Append(": ").Append(code.Name).Append(' ').Append(operand).Append('\n');
            index += size;
        }
        return output.ToString();
    }
}
