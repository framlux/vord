// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using System.Reflection.Emit;

namespace Framlux.FleetManagement.UnitTest.Architecture;

/// <summary>
/// Finds which types in a compiled assembly reference types from a given namespace.
/// </summary>
/// <remarks>
/// Signature-only reflection is not enough for an architecture rule: a class can construct, call
/// and discard a foreign type entirely inside a method body without any of it showing up on a
/// field, property or parameter. This scanner therefore inspects declared member signatures
/// <em>and</em> decodes each method body's IL, resolving every type, field and method token it
/// touches. That is what a dedicated architecture-test package would do; doing it here keeps the
/// rule enforceable without adding a third-party dependency to the build.
/// </remarks>
internal static class AssemblyNamespaceReferenceScanner
{
    /// <summary>
    /// Single-byte IL opcodes indexed by their opcode value.
    /// </summary>
    private static readonly OpCode?[] _singleByteOpCodes = BuildOpCodeTable(twoByte: false);

    /// <summary>
    /// Two-byte (0xFE-prefixed) IL opcodes indexed by their second byte.
    /// </summary>
    private static readonly OpCode?[] _twoByteOpCodes = BuildOpCodeTable(twoByte: true);

    /// <summary>
    /// Every binding-flag combination needed to see a type's own members, including private and
    /// compiler-generated ones.
    /// </summary>
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Maps each type in <paramref name="assembly"/> that reaches into
    /// <paramref name="targetNamespace"/> to the set of namespace member names it touches.
    /// </summary>
    /// <param name="assembly">The compiled assembly to inspect.</param>
    /// <param name="targetNamespace">The namespace whose use is being tracked.</param>
    /// <returns>
    /// A dictionary keyed by the full name of the outermost declaring type, so that a violation
    /// hidden in a lambda closure or async state machine is reported against the type that wrote
    /// it rather than against a generated name nobody can find in the source.
    /// </returns>
    public static IReadOnlyDictionary<string, SortedSet<string>> FindReferencingTypes(Assembly assembly, string targetNamespace)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNamespace);

        Dictionary<string, SortedSet<string>> violations = new(StringComparer.Ordinal);

        foreach (Type type in GetLoadableTypes(assembly))
        {
            HashSet<Type> referenced = [];

            CollectFromSignatures(type, referenced);
            CollectFromMethodBodies(type, referenced);

            foreach (Type candidate in referenced)
            {
                foreach (Type leaf in Flatten(candidate))
                {
                    if (string.Equals(leaf.Namespace, targetNamespace, StringComparison.Ordinal) == false)
                    {
                        continue;
                    }

                    string owner = OutermostDeclaringType(type).FullName ?? type.Name;
                    if (violations.TryGetValue(owner, out SortedSet<string>? names) == false)
                    {
                        names = new SortedSet<string>(StringComparer.Ordinal);
                        violations[owner] = names;
                    }

                    names.Add(leaf.Name);
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Collects every type that appears in the declared signatures of <paramref name="type"/>.
    /// </summary>
    /// <param name="type">The type being inspected.</param>
    /// <param name="referenced">The accumulator to add discovered types to.</param>
    private static void CollectFromSignatures(Type type, HashSet<Type> referenced)
    {
        if (type.BaseType is not null)
        {
            referenced.Add(type.BaseType);
        }

        foreach (Type contract in type.GetInterfaces())
        {
            referenced.Add(contract);
        }

        foreach (FieldInfo field in type.GetFields(DeclaredMembers))
        {
            referenced.Add(field.FieldType);
        }

        foreach (PropertyInfo property in type.GetProperties(DeclaredMembers))
        {
            referenced.Add(property.PropertyType);
            foreach (ParameterInfo parameter in property.GetIndexParameters())
            {
                referenced.Add(parameter.ParameterType);
            }
        }

        foreach (EventInfo declaredEvent in type.GetEvents(DeclaredMembers))
        {
            if (declaredEvent.EventHandlerType is not null)
            {
                referenced.Add(declaredEvent.EventHandlerType);
            }
        }

        foreach (MethodBase method in EnumerateMethods(type))
        {
            if (method is MethodInfo info)
            {
                referenced.Add(info.ReturnType);
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                referenced.Add(parameter.ParameterType);
            }
        }
    }

    /// <summary>
    /// Decodes every method body declared on <paramref name="type"/> and collects the types behind
    /// the metadata tokens the IL touches.
    /// </summary>
    /// <param name="type">The type being inspected.</param>
    /// <param name="referenced">The accumulator to add discovered types to.</param>
    private static void CollectFromMethodBodies(Type type, HashSet<Type> referenced)
    {
        Type[]? typeArguments = type.IsGenericType ? type.GetGenericArguments() : null;

        foreach (MethodBase method in EnumerateMethods(type))
        {
            MethodBody? body;
            try
            {
                body = method.GetMethodBody();
            }
            catch (NotSupportedException)
            {
                continue;
            }

            if (body is null)
            {
                continue;
            }

            foreach (LocalVariableInfo local in body.LocalVariables)
            {
                referenced.Add(local.LocalType);
            }

            byte[]? il = body.GetILAsByteArray();
            if (il is null)
            {
                continue;
            }

            Type[]? methodArguments = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;
            CollectFromIl(method.Module, il, typeArguments, methodArguments, referenced);
        }
    }

    /// <summary>
    /// Walks an IL byte stream and resolves each type, field and method token it contains.
    /// </summary>
    /// <param name="module">The module the tokens are scoped to.</param>
    /// <param name="il">The raw IL of a single method body.</param>
    /// <param name="typeArguments">Generic arguments of the declaring type, or null.</param>
    /// <param name="methodArguments">Generic arguments of the method, or null.</param>
    /// <param name="referenced">The accumulator to add discovered types to.</param>
    private static void CollectFromIl(
        Module module,
        byte[] il,
        Type[]? typeArguments,
        Type[]? methodArguments,
        HashSet<Type> referenced)
    {
        int offset = 0;

        while (offset < il.Length)
        {
            OpCode? opCode;

            byte first = il[offset];
            offset++;

            if (first == 0xFE)
            {
                if (offset >= il.Length)
                {
                    return;
                }

                opCode = _twoByteOpCodes[il[offset]];
                offset++;
            }
            else
            {
                opCode = _singleByteOpCodes[first];
            }

            // An unknown opcode means the stream is no longer being read at an instruction
            // boundary; continuing would resolve garbage tokens, so stop reading this body.
            if (opCode is null)
            {
                return;
            }

            OperandType operand = opCode.Value.OperandType;

            if (operand == OperandType.InlineSwitch)
            {
                if ((offset + 4) > il.Length)
                {
                    return;
                }

                int cases = BitConverter.ToInt32(il, offset);
                offset += 4 + (4 * cases);

                continue;
            }

            int operandSize = OperandSize(operand);
            if ((offset + operandSize) > il.Length)
            {
                return;
            }

            if ((operand == OperandType.InlineField) ||
                (operand == OperandType.InlineMethod) ||
                (operand == OperandType.InlineTok) ||
                (operand == OperandType.InlineType))
            {
                ResolveToken(module, BitConverter.ToInt32(il, offset), typeArguments, methodArguments, referenced);
            }

            offset += operandSize;
        }
    }

    /// <summary>
    /// Resolves a metadata token and records the types it exposes.
    /// </summary>
    /// <param name="module">The module the token is scoped to.</param>
    /// <param name="token">The metadata token from the IL stream.</param>
    /// <param name="typeArguments">Generic arguments of the declaring type, or null.</param>
    /// <param name="methodArguments">Generic arguments of the method, or null.</param>
    /// <param name="referenced">The accumulator to add discovered types to.</param>
    private static void ResolveToken(
        Module module,
        int token,
        Type[]? typeArguments,
        Type[]? methodArguments,
        HashSet<Type> referenced)
    {
        MemberInfo? member;

        try
        {
            member = module.ResolveMember(token, typeArguments, methodArguments);
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (BadImageFormatException)
        {
            return;
        }
        catch (MissingMemberException)
        {
            return;
        }

        switch (member)
        {
            case Type resolvedType:
                referenced.Add(resolvedType);
                break;

            case FieldInfo field:
                referenced.Add(field.FieldType);
                if (field.DeclaringType is not null)
                {
                    referenced.Add(field.DeclaringType);
                }

                break;

            case MethodBase method:
                if (method.DeclaringType is not null)
                {
                    referenced.Add(method.DeclaringType);
                }

                if (method is MethodInfo info)
                {
                    referenced.Add(info.ReturnType);

                    if (info.IsGenericMethod)
                    {
                        foreach (Type argument in info.GetGenericArguments())
                        {
                            referenced.Add(argument);
                        }
                    }
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    referenced.Add(parameter.ParameterType);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Enumerates the constructors and methods declared directly on a type.
    /// </summary>
    /// <param name="type">The type being inspected.</param>
    /// <returns>Every declared constructor and method, including private ones.</returns>
    private static IEnumerable<MethodBase> EnumerateMethods(Type type)
    {
        foreach (ConstructorInfo constructor in type.GetConstructors(DeclaredMembers))
        {
            yield return constructor;
        }

        foreach (MethodInfo method in type.GetMethods(DeclaredMembers))
        {
            yield return method;
        }
    }

    /// <summary>
    /// Expands a type into itself plus every type it is built out of — element types of arrays,
    /// by-ref and pointer types, and generic arguments.
    /// </summary>
    /// <param name="type">The type to expand.</param>
    /// <returns>The type and all of its constituent types.</returns>
    private static IEnumerable<Type> Flatten(Type type)
    {
        Stack<Type> pending = new();
        HashSet<Type> seen = [];

        pending.Push(type);

        while (pending.Count > 0)
        {
            Type current = pending.Pop();
            if (seen.Add(current) == false)
            {
                continue;
            }

            yield return current;

            if (current.HasElementType)
            {
                Type? element = current.GetElementType();
                if (element is not null)
                {
                    pending.Push(element);
                }
            }

            if (current.IsGenericType)
            {
                foreach (Type argument in current.GetGenericArguments())
                {
                    pending.Push(argument);
                }
            }
        }
    }

    /// <summary>
    /// Walks up the nesting chain so a violation inside a generated closure or state machine is
    /// attributed to the source-visible type that produced it.
    /// </summary>
    /// <param name="type">The type to resolve.</param>
    /// <returns>The outermost declaring type.</returns>
    private static Type OutermostDeclaringType(Type type)
    {
        Type current = type;
        while (current.DeclaringType is not null)
        {
            current = current.DeclaringType;
        }

        return current;
    }

    /// <summary>
    /// Returns every type an assembly can load, ignoring the ones whose dependencies are absent.
    /// </summary>
    /// <param name="assembly">The assembly to enumerate.</param>
    /// <returns>The loadable types.</returns>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!);
        }
    }

    /// <summary>
    /// Returns the number of operand bytes that follow an opcode with the given operand type.
    /// </summary>
    /// <param name="operand">The operand type declared by the opcode.</param>
    /// <returns>The operand width in bytes.</returns>
    private static int OperandSize(OperandType operand)
    {
        return operand switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 => 8,
            OperandType.InlineR => 8,
            _ => 4,
        };
    }

    /// <summary>
    /// Builds an opcode lookup table from the runtime's own opcode definitions, so no hand-written
    /// instruction table can drift from the IL specification.
    /// </summary>
    /// <param name="twoByte">True for the 0xFE-prefixed table, false for the single-byte table.</param>
    /// <returns>A 256-entry table indexed by the opcode's low byte.</returns>
    private static OpCode?[] BuildOpCodeTable(bool twoByte)
    {
        OpCode?[] table = new OpCode?[256];

        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            ushort value = unchecked((ushort)opCode.Value);
            bool isTwoByte = (value & 0xFF00) == 0xFE00;

            if (isTwoByte != twoByte)
            {
                continue;
            }

            table[value & 0xFF] = opCode;
        }

        return table;
    }
}
