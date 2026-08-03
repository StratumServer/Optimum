using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace Optimum.Patcher;

/// <summary>
/// Compares Cecil methods by their complete callable signature.
/// Name plus parameter count can select the wrong overload and leave a valid
/// looking patch that fails when the CLR resolves the transplanted call.
/// </summary>
public static class MethodSignature
{
    public static bool Matches(MethodDefinition candidate, MethodReference reference)
    {
        if (reference is GenericInstanceMethod genericInstance)
        {
            reference = genericInstance.ElementMethod;
        }

        if (GetDeclaringTypeName(candidate.DeclaringType) != GetDeclaringTypeName(reference.DeclaringType) ||
            candidate.Name != reference.Name ||
            candidate.HasThis != reference.HasThis ||
            candidate.ExplicitThis != reference.ExplicitThis ||
            candidate.CallingConvention != reference.CallingConvention ||
            candidate.GenericParameters.Count != reference.GenericParameters.Count ||
            candidate.ReturnType.FullName != reference.ReturnType.FullName ||
            candidate.Parameters.Count != reference.Parameters.Count)
        {
            return false;
        }

        for (int i = 0; i < candidate.Parameters.Count; i++)
        {
            if (candidate.Parameters[i].ParameterType.FullName !=
                reference.Parameters[i].ParameterType.FullName)
            {
                return false;
            }
        }

        return true;
    }

    private static string GetDeclaringTypeName(TypeReference type) =>
        type is GenericInstanceType genericType
            ? genericType.ElementType.FullName
            : type.FullName;

    public static bool Matches(MethodDefinition left, MethodDefinition right) =>
        Matches(left, (MethodReference)right);

    public static bool Matches(
        MethodReference candidate,
        string declaringType,
        string methodName,
        IReadOnlyList<string> parameterTypes,
        string returnType,
        bool hasThis,
        bool explicitThis,
        MethodCallingConvention callingConvention,
        int genericArity)
    {
        if (candidate is GenericInstanceMethod genericInstance)
            candidate = genericInstance.ElementMethod;

        string candidateDeclaringType = candidate.DeclaringType is GenericInstanceType genericType
            ? genericType.ElementType.FullName
            : candidate.DeclaringType.FullName;
        if (candidateDeclaringType != declaringType ||
            candidate.Name != methodName ||
            candidate.HasThis != hasThis ||
            candidate.ExplicitThis != explicitThis ||
            candidate.CallingConvention != callingConvention ||
            candidate.GenericParameters.Count != genericArity ||
            candidate.ReturnType.FullName != returnType ||
            candidate.Parameters.Count != parameterTypes.Count)
        {
            return false;
        }

        return candidate.Parameters
            .Select(parameter => parameter.ParameterType.FullName)
            .SequenceEqual(parameterTypes, StringComparer.Ordinal);
    }

    public static MethodDefinition? FindUnique(
        TypeDefinition type,
        string methodName,
        int? parameterCount = null)
    {
        var candidates = type.Methods
            .Where(method => method.Name == methodName &&
                (!parameterCount.HasValue || method.Parameters.Count == parameterCount.Value))
            .ToArray();

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Ambiguous method target {type.FullName}::{methodName}: " +
                string.Join(", ", candidates.Select(GetKey)));
        }

        return candidates.SingleOrDefault();
    }

    public static string GetKey(MethodReference method)
    {
        string parameters = string.Join(", ", method.Parameters.Select(parameter =>
            parameter.ParameterType.FullName));
        return $"{method.DeclaringType.FullName}::{method.Name}({parameters}) -> {method.ReturnType.FullName}";
    }
}
