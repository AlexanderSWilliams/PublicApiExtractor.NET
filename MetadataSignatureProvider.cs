using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace PublicApiExtractorV2;

internal sealed class MetadataSignatureProvider : ISignatureTypeProvider<SignatureTypeName, GenericContext>
{
    private readonly MetadataNames _names;

    public MetadataSignatureProvider(MetadataNames names)
    {
        _names = names;
    }

    public SignatureTypeName GetArrayType(SignatureTypeName elementType, ArrayShape shape)
        => SignatureTypeName.Array(elementType, shape);

    public SignatureTypeName GetByReferenceType(SignatureTypeName elementType)
        => SignatureTypeName.ByReference(elementType);

    public SignatureTypeName GetFunctionPointerType(MethodSignature<SignatureTypeName> signature)
        => SignatureTypeName.FunctionPointer(signature.ParameterTypes.ToArray(), signature.ReturnType);

    public SignatureTypeName GetGenericInstantiation(SignatureTypeName genericType, ImmutableArray<SignatureTypeName> typeArguments)
        => SignatureTypeName.GenericInstantiation(genericType, typeArguments.ToArray());

    public SignatureTypeName GetGenericMethodParameter(GenericContext genericContext, int index)
        => SignatureTypeName.GenericParameter(genericContext.MethodParameter(index));

    public SignatureTypeName GetGenericTypeParameter(GenericContext genericContext, int index)
        => SignatureTypeName.GenericParameter(genericContext.TypeParameter(index));

    public SignatureTypeName GetModifiedType(SignatureTypeName modifier, SignatureTypeName unmodifiedType, bool isRequired)
        => SignatureTypeName.Modified(modifier, unmodifiedType, isRequired);

    public SignatureTypeName GetPinnedType(SignatureTypeName elementType)
        => SignatureTypeName.Pinned(elementType);

    public SignatureTypeName GetPointerType(SignatureTypeName elementType)
        => SignatureTypeName.Pointer(elementType);

    public SignatureTypeName GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        string name = MetadataNames.PrimitiveName(typeCode);
        bool nullableReferenceSlot = typeCode == PrimitiveTypeCode.String || typeCode == PrimitiveTypeCode.Object;
        string fullName = typeCode == PrimitiveTypeCode.String ? "System.String" : typeCode == PrimitiveTypeCode.Object ? "System.Object" : "";
        return SignatureTypeName.Simple(name, fullName, nullableReferenceSlot);
    }

    public SignatureTypeName GetSZArrayType(SignatureTypeName elementType)
        => SignatureTypeName.SZArray(elementType);

    public SignatureTypeName GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        bool nullableReferenceSlot = rawTypeKind != (byte)SignatureTypeKind.ValueType;
        return SignatureTypeName.Simple(
            _names.TypeDefinitionDisplayName(handle, includeNamespace: true, includeGenericParameters: false),
            _names.EntityTypeFullName(handle),
            nullableReferenceSlot);
    }

    public SignatureTypeName GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        bool nullableReferenceSlot = rawTypeKind != (byte)SignatureTypeKind.ValueType;
        return SignatureTypeName.Simple(
            _names.TypeReferenceDisplayName(handle, includeNamespace: true),
            _names.EntityTypeFullName(handle),
            nullableReferenceSlot);
    }

    public SignatureTypeName GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        TypeSpecification spec = reader.GetTypeSpecification(handle);
        return spec.DecodeSignature(this, genericContext);
    }
}
