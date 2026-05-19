using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace PublicApiExtractorV2;

internal sealed class MetadataNames
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long",
        "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
    };

    private static readonly Dictionary<string, string> SemanticAttributeNames = new(StringComparer.Ordinal)
    {
        ["System.ObsoleteAttribute"] = "Obsolete",
        ["System.Diagnostics.CodeAnalysis.ExperimentalAttribute"] = "Experimental",
        ["System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"] = "RequiresUnreferencedCode",
        ["System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"] = "RequiresDynamicCode",
        ["System.Diagnostics.CodeAnalysis.RequiresAssemblyFilesAttribute"] = "RequiresAssemblyFiles",
        ["System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute"] = "DynamicallyAccessedMembers",
        ["System.Diagnostics.CodeAnalysis.AllowNullAttribute"] = "AllowNull",
        ["System.Diagnostics.CodeAnalysis.DisallowNullAttribute"] = "DisallowNull",
        ["System.Diagnostics.CodeAnalysis.MaybeNullAttribute"] = "MaybeNull",
        ["System.Diagnostics.CodeAnalysis.NotNullAttribute"] = "NotNull",
        ["System.Diagnostics.CodeAnalysis.MaybeNullWhenAttribute"] = "MaybeNullWhen",
        ["System.Diagnostics.CodeAnalysis.NotNullWhenAttribute"] = "NotNullWhen",
        ["System.Diagnostics.CodeAnalysis.NotNullIfNotNullAttribute"] = "NotNullIfNotNull",
        ["System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute"] = "DoesNotReturn",
        ["System.Diagnostics.CodeAnalysis.DoesNotReturnIfAttribute"] = "DoesNotReturnIf",
        ["System.Diagnostics.CodeAnalysis.MemberNotNullAttribute"] = "MemberNotNull",
        ["System.Diagnostics.CodeAnalysis.MemberNotNullWhenAttribute"] = "MemberNotNullWhen",
        ["System.Diagnostics.CodeAnalysis.StringSyntaxAttribute"] = "StringSyntax",
        ["System.Runtime.Versioning.SupportedOSPlatformAttribute"] = "SupportedOSPlatform",
        ["System.Runtime.Versioning.UnsupportedOSPlatformAttribute"] = "UnsupportedOSPlatform",
        ["System.Runtime.Versioning.ObsoletedOSPlatformAttribute"] = "ObsoletedOSPlatform",
        ["System.Runtime.Versioning.TargetPlatformAttribute"] = "TargetPlatform",
        ["System.Runtime.CompilerServices.RequiredMemberAttribute"] = "RequiredMember",
        ["System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"] = "SetsRequiredMembers"
    };

    public static bool IsEmittedSemanticAttributeFullName(string fullName)
        => SemanticAttributeNames.ContainsKey(fullName);

    private readonly MetadataReader _reader;
    private readonly MetadataNamePolicy _policy;
    private string _currentNamespace = "";

    public MetadataNames(MetadataReader reader, MetadataNamePolicy policy)
    {
        _reader = reader;
        _policy = policy;
    }

    public void SetCurrentNamespace(string ns) => _currentNamespace = ns ?? "";

    public string String(StringHandle handle) => handle.IsNil ? "" : _reader.GetString(handle);

    public string SimpleName(string metadataName)
    {
        int tick = metadataName.IndexOf('`');
        string simple = tick >= 0 ? metadataName.Substring(0, tick) : metadataName;
        return Identifier(simple);
    }

    public static string TypeNameWithSyntheticGenericParameters(string metadataName)
    {
        int tick = metadataName.IndexOf('`');
        if (tick < 0) return Identifier(metadataName);

        string simple = Identifier(metadataName.Substring(0, tick));
        string arityText = metadataName.Substring(tick + 1);
        int arity = 0;
        for (int i = 0; i < arityText.Length; i++)
        {
            char ch = arityText[i];
            if (ch < '0' || ch > '9') break;
            arity = arity * 10 + (ch - '0');
        }

        if (arity <= 0) return simple;

        var args = new string[arity];
        for (int i = 0; i < arity; i++)
            args[i] = arity == 1 ? "T" : "T" + i.ToString(CultureInfo.InvariantCulture);
        return simple + "<" + string.Join(",", args) + ">";
    }

    public string TypeDefinitionNamespace(TypeDefinitionHandle handle)
    {
        TypeDefinition td = _reader.GetTypeDefinition(handle);
        if (!td.GetDeclaringType().IsNil)
            return TypeDefinitionNamespace(td.GetDeclaringType());
        return String(td.Namespace);
    }

    public string TypeDefinitionDisplayName(TypeDefinitionHandle handle, bool includeNamespace, bool includeGenericParameters)
    {
        string ns = TypeDefinitionNamespace(handle);
        string nestedName = TypeDefinitionNestedName(handle, includeGenericParameters);
        string leafKey = String(_reader.GetTypeDefinition(handle).Name);
        return _policy.Format(ns, nestedName, _currentNamespace, includeNamespace, leafKey);
    }

    private string TypeDefinitionNestedName(TypeDefinitionHandle handle, bool includeGenericParameters)
    {
        TypeDefinition td = _reader.GetTypeDefinition(handle);
        string simple = SimpleName(String(td.Name));
        if (includeGenericParameters)
        {
            var gps = GenericParameterNames(td.GetGenericParameters());
            if (gps.Count > 0) simple += "<" + string.Join(",", gps) + ">";
        }

        TypeDefinitionHandle declaring = td.GetDeclaringType();
        if (!declaring.IsNil)
            return TypeDefinitionNestedName(declaring, includeGenericParameters) + "." + simple;
        return simple;
    }

    public string TypeReferenceDisplayName(TypeReferenceHandle handle, bool includeNamespace)
    {
        string ns = TypeReferenceNamespace(handle);
        string nestedName = TypeReferenceNestedName(handle);
        string leafKey = String(_reader.GetTypeReference(handle).Name);
        return _policy.Format(ns, nestedName, _currentNamespace, includeNamespace, leafKey);
    }

    private string TypeReferenceNamespace(TypeReferenceHandle handle)
    {
        TypeReference tr = _reader.GetTypeReference(handle);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeReferenceNamespace((TypeReferenceHandle)tr.ResolutionScope);
        return String(tr.Namespace);
    }

    private string TypeReferenceNestedName(TypeReferenceHandle handle)
    {
        TypeReference tr = _reader.GetTypeReference(handle);
        string simple = SimpleName(String(tr.Name));
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeReferenceNestedName((TypeReferenceHandle)tr.ResolutionScope) + "." + simple;
        return simple;
    }

    public string TypeReferenceIdentityName(TypeReferenceHandle handle)
    {
        string ns = TypeReferenceNamespace(handle);
        string nested = TypeReferenceIdentityNestedName(handle);
        return ns.Length == 0 ? nested : ns + "." + nested;
    }

    private string TypeReferenceIdentityNestedName(TypeReferenceHandle handle)
    {
        TypeReference tr = _reader.GetTypeReference(handle);
        string simple = String(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeReferenceIdentityNestedName((TypeReferenceHandle)tr.ResolutionScope) + "." + simple;
        return simple;
    }

    public string EntityTypeName(EntityHandle handle, GenericContext context, bool includeNamespace = true)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return TypeDefinitionDisplayName((TypeDefinitionHandle)handle, includeNamespace, includeGenericParameters: true);
            case HandleKind.TypeReference:
                return TypeReferenceDisplayName((TypeReferenceHandle)handle, includeNamespace);
            case HandleKind.TypeSpecification:
                {
                    var provider = new MetadataSignatureProvider(this);
                    TypeSpecification spec = _reader.GetTypeSpecification((TypeSpecificationHandle)handle);
                    return spec.DecodeSignature(provider, context).Render();
                }
            default:
                return "<" + handle.Kind.ToString() + ">";
        }
    }

    public List<string> GenericParameterNames(GenericParameterHandleCollection handles)
    {
        var list = new List<string>();
        foreach (GenericParameterHandle h in handles)
            list.Add(Identifier(String(_reader.GetGenericParameter(h).Name)));
        return list;
    }

    public string GenericParameterList(GenericParameterHandleCollection handles)
    {
        var parts = new List<string>();
        foreach (GenericParameterHandle h in handles)
        {
            GenericParameter gp = _reader.GetGenericParameter(h);
            string name = Identifier(String(gp.Name));
            string attributePrefix = SemanticAttributePrefix(gp.GetCustomAttributes(), includeNullableInfrastructure: false);
            var variance = gp.Attributes & GenericParameterAttributes.VarianceMask;
            if (variance == GenericParameterAttributes.Covariant) name = "out " + attributePrefix + name;
            else if (variance == GenericParameterAttributes.Contravariant) name = "in " + attributePrefix + name;
            else name = attributePrefix + name;
            parts.Add(name);
        }
        return parts.Count == 0 ? "" : "<" + string.Join(",", parts) + ">";
    }

    public string GenericWhereClauses(GenericParameterHandleCollection handles, GenericContext context)
    {
        var clauses = new List<string>();
        foreach (GenericParameterHandle h in handles)
        {
            GenericParameter gp = _reader.GetGenericParameter(h);
            var attrs = gp.Attributes & GenericParameterAttributes.SpecialConstraintMask;
            var parts = new List<string>();
            bool unmanaged = HasAttribute(gp.GetCustomAttributes(), "System.Runtime.CompilerServices.IsUnmanagedAttribute");
            if (unmanaged) parts.Add("unmanaged");
            else
            {
                if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0) parts.Add("class");
                if ((attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) parts.Add("struct");
            }

            foreach (GenericParameterConstraintHandle ch in gp.GetConstraints())
            {
                EntityHandle constraint = _reader.GetGenericParameterConstraint(ch).Type;
                string text = CleanGenericConstraintType(EntityTypeName(constraint, context, includeNamespace: true));
                if (IsValueTypeConstraint(text))
                    continue;
                parts.Add(text);
            }

            if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
                (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0 &&
                !unmanaged)
                parts.Add("new()");

            if (parts.Count > 0)
                clauses.Add("where " + Identifier(String(gp.Name)) + ":" + string.Join(",", parts.Distinct(StringComparer.Ordinal)));
        }
        return clauses.Count == 0 ? "" : " " + string.Join(" ", clauses);
    }


    private static string CleanGenericConstraintType(string text)
    {
        return text
            .Replace("modreq(System.Runtime.CompilerServices.IsUnmanagedAttribute) ", "", StringComparison.Ordinal)
            .Replace("modreq(System.Runtime.CompilerServices.UnmanagedType) ", "", StringComparison.Ordinal)
            .Replace("modreq(System.Runtime.InteropServices.UnmanagedType) ", "", StringComparison.Ordinal)
            .Replace("modreq(IsUnmanagedAttribute) ", "", StringComparison.Ordinal)
            .Replace("modreq(UnmanagedType) ", "", StringComparison.Ordinal);
    }

    private static bool IsValueTypeConstraint(string text)
        => text == "System.ValueType" || text == "ValueType";

    public string AttributeTypeName(CustomAttribute attribute)
    {
        EntityHandle parent = AttributeTypeHandle(attribute);
        string name = parent.Kind == HandleKind.TypeDefinition || parent.Kind == HandleKind.TypeReference || parent.Kind == HandleKind.TypeSpecification
            ? EntityTypeName(parent, new GenericContext(Array.Empty<string>(), Array.Empty<string>()), includeNamespace: true)
            : "<attribute>";

        return name;
    }

    public bool HasAttribute(CustomAttributeHandleCollection attrs, string fullName)
    {
        foreach (CustomAttributeHandle h in attrs)
        {
            if (AttributeTypeFullName(_reader.GetCustomAttribute(h)) == fullName) return true;
        }
        return false;
    }

    public string SemanticAttributeSuffix(CustomAttributeHandleCollection attrs, bool includeNullableInfrastructure = false)
    {
        var parts = new List<string>();
        foreach (CustomAttributeHandle h in attrs)
        {
            CustomAttribute attr = _reader.GetCustomAttribute(h);
            string fullName = AttributeTypeFullName(attr);
            if (SemanticAttributeNames.TryGetValue(fullName, out string? shortName))
            {
                string? rendered = FormatSemanticAttribute(attr, fullName, shortName);
                if (rendered != null) parts.Add(rendered);
            }
        }
        return parts.Count == 0 ? "" : " " + string.Join(" ", parts.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
    }

    public string SemanticAttributeSuffix(CustomAttributeHandleCollection? attrs, string targetPrefix)
    {
        if (!attrs.HasValue) return "";
        var parts = new List<string>();
        foreach (CustomAttributeHandle h in attrs.Value)
        {
            CustomAttribute attr = _reader.GetCustomAttribute(h);
            string fullName = AttributeTypeFullName(attr);
            if (SemanticAttributeNames.TryGetValue(fullName, out string? shortName))
            {
                string? rendered = FormatSemanticAttribute(attr, fullName, shortName);
                if (rendered != null)
                    parts.Add("[" + targetPrefix + ":" + rendered.Substring(1));
            }
        }
        return parts.Count == 0 ? "" : " " + string.Join(" ", parts.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
    }

    public string SemanticAttributePrefix(CustomAttributeHandleCollection attrs, bool includeNullableInfrastructure = false)
    {
        string suffix = SemanticAttributeSuffix(attrs, includeNullableInfrastructure);
        return suffix.Length == 0 ? "" : suffix.TrimStart() + " ";
    }

    public string SemanticAttributePrefix(CustomAttributeHandleCollection? attrs)
    {
        if (!attrs.HasValue) return "";
        return SemanticAttributePrefix(attrs.Value);
    }

    public byte? NullableContext(CustomAttributeHandleCollection attrs, byte? inheritedContext = null)
    {
        foreach (CustomAttributeHandle h in attrs)
        {
            CustomAttribute attr = _reader.GetCustomAttribute(h);
            if (AttributeTypeFullName(attr) == "System.Runtime.CompilerServices.NullableContextAttribute" && TryReadNullableContext(attr, out byte context))
                return context;
        }
        return inheritedContext;
    }

    public byte? NullableContext(CustomAttributeHandleCollection? attrs, byte? inheritedContext = null)
        => attrs.HasValue ? NullableContext(attrs.Value, inheritedContext) : inheritedContext;

    public string RenderNullableType(SignatureTypeName type, CustomAttributeHandleCollection attrs, byte? inheritedContext)
        => RenderNullableType(type, inheritedContext, attrs);

    public string RenderNullableType(SignatureTypeName type, CustomAttributeHandleCollection? attrs, byte? inheritedContext)
        => RenderNullableType(type, inheritedContext, attrs);

    public string RenderNullableType(SignatureTypeName type, byte? inheritedContext, params CustomAttributeHandleCollection?[] attributeLayers)
    {
        byte? context = inheritedContext;
        byte[]? flags = null;
        IReadOnlyList<string?>? tupleNames = null;

        foreach (CustomAttributeHandleCollection? attrs in attributeLayers)
        {
            if (!attrs.HasValue) continue;

            context = NullableContext(attrs.Value, context);
            byte[]? layerFlags = NullableFlags(attrs.Value);
            if (layerFlags != null)
                flags = layerFlags;

            IReadOnlyList<string?>? layerTupleNames = TupleElementNames(attrs.Value);
            if (layerTupleNames != null)
                tupleNames = layerTupleNames;
        }

        return type.Render(flags, context, tupleNames);
    }

    public byte[]? NullableFlags(CustomAttributeHandleCollection? attrs)
    {
        if (!attrs.HasValue) return null;
        return NullableFlags(attrs.Value);
    }

    public byte[]? NullableFlags(CustomAttributeHandleCollection attrs)
    {
        foreach (CustomAttributeHandle h in attrs)
        {
            CustomAttribute attr = _reader.GetCustomAttribute(h);
            if (AttributeTypeFullName(attr) == "System.Runtime.CompilerServices.NullableAttribute" && TryReadNullableFlags(attr, out byte[]? decodedFlags))
                return decodedFlags;
        }
        return null;
    }

    public IReadOnlyList<string?>? TupleElementNames(CustomAttributeHandleCollection? attrs)
    {
        if (!attrs.HasValue) return null;
        return TupleElementNames(attrs.Value);
    }

    public IReadOnlyList<string?>? TupleElementNames(CustomAttributeHandleCollection attrs)
    {
        foreach (CustomAttributeHandle h in attrs)
        {
            CustomAttribute attr = _reader.GetCustomAttribute(h);
            if (AttributeTypeFullName(attr) != "System.Runtime.CompilerServices.TupleElementNamesAttribute")
                continue;

            try
            {
                DecodedAttribute decoded = DecodeCustomAttribute(attr);
                if (decoded.FixedArguments.Count == 0 || decoded.FixedArguments[0] is not List<object?> values)
                    return null;

                var names = new string?[values.Count];
                for (int i = 0; i < values.Count; i++)
                    names[i] = values[i] as string;
                return names;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public string RenderNullableType(SignatureTypeName type, byte[]? nullableFlags, byte? nullableContext)
        => type.Render(nullableFlags, nullableContext);

    public string RenderNullableType(SignatureTypeName type, byte[]? nullableFlags, byte? nullableContext, IReadOnlyList<string?>? tupleElementNames)
        => type.Render(nullableFlags, nullableContext, tupleElementNames);

    private bool TryReadNullableContext(CustomAttribute attr, out byte context)
    {
        context = 0;
        try
        {
            BlobReader reader = _reader.GetBlobReader(attr.Value);
            if (reader.Length < 3 || reader.ReadUInt16() != 1) return false;
            context = reader.ReadByte();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadNullableFlags(CustomAttribute attr, out byte[]? flags)
    {
        flags = null;
        try
        {
            BlobReader reader = _reader.GetBlobReader(attr.Value);
            if (reader.Length < 3 || reader.ReadUInt16() != 1) return false;

            List<string> parameterTypes = AttributeConstructorParameterTypes(attr);
            string parameterType = parameterTypes.Count == 0 ? "" : parameterTypes[0];

            if (parameterType == "byte" || parameterType == "System.Byte")
            {
                if (reader.RemainingBytes < 1) return false;
                flags = new[] { reader.ReadByte() };
                return true;
            }

            if (parameterType == "byte[]" || parameterType == "System.Byte[]")
            {
                if (reader.RemainingBytes < 4) return false;
                int count = reader.ReadInt32();
                if (count < 0)
                {
                    flags = null;
                    return true;
                }
                if (count > reader.RemainingBytes) return false;
                flags = new byte[count];
                for (int i = 0; i < count; i++) flags[i] = reader.ReadByte();
                return true;
            }

            // Fallback for unusual metadata: old code assumed the payload had no trailing
            // named-argument count, but custom attribute blobs normally do. Treat a short
            // payload as the byte constructor and a longer payload as the byte[] constructor.
            if (reader.RemainingBytes >= 1 && reader.RemainingBytes < 4)
            {
                flags = new[] { reader.ReadByte() };
                return true;
            }
            if (reader.RemainingBytes >= 4)
            {
                int count = reader.ReadInt32();
                if (count < 0)
                {
                    flags = null;
                    return true;
                }
                if (count > reader.RemainingBytes) return false;
                flags = new byte[count];
                for (int i = 0; i < count; i++) flags[i] = reader.ReadByte();
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private string? FormatSemanticAttribute(CustomAttribute attr, string fullName, string shortName)
    {
        try
        {
            DecodedAttribute decoded = DecodeCustomAttribute(attr);
            if (IsPrivateTargetMemberNullabilityAttribute(shortName, decoded))
                return null;

            if (decoded.FixedArguments.Count == 0 && decoded.NamedArguments.Count == 0)
                return "[" + shortName + "]";

            var values = new List<string>();
            foreach (object? value in decoded.FixedArguments)
                values.Add(FormatAttributeValue(shortName, value));
            foreach (KeyValuePair<string, object?> named in decoded.NamedArguments.OrderBy(x => x.Key, StringComparer.Ordinal))
                values.Add(Identifier(named.Key) + "=" + FormatAttributeValue(shortName, named.Value));
            return "[" + shortName + "(" + string.Join(",", values) + ")]";
        }
        catch
        {
            return "[" + shortName + "]";
        }
    }

    public bool IsSuppressedSemanticAttribute(CustomAttribute attr)
    {
        string fullName = AttributeTypeFullName(attr);
        if (!SemanticAttributeNames.TryGetValue(fullName, out string? shortName))
            return false;

        try
        {
            return IsPrivateTargetMemberNullabilityAttribute(shortName, DecodeCustomAttribute(attr));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivateTargetMemberNullabilityAttribute(string shortName, DecodedAttribute decoded)
    {
        if (shortName != "MemberNotNull" && shortName != "MemberNotNullWhen")
            return false;

        foreach (object? target in decoded.FixedArguments)
        {
            if (ContainsPrivateMemberTarget(target))
                return true;
        }

        foreach (object? target in decoded.NamedArguments.Values)
        {
            if (ContainsPrivateMemberTarget(target))
                return true;
        }

        return false;
    }

    private static bool ContainsPrivateMemberTarget(object? value)
    {
        if (value is string s)
            return IsPrivateMemberTargetName(s);
        if (value is List<object?> list)
            return list.Any(ContainsPrivateMemberTarget);
        return false;
    }

    private static bool IsPrivateMemberTargetName(string name)
        => name.StartsWith("_", StringComparison.Ordinal)
           || name.StartsWith("m_", StringComparison.Ordinal)
           || name.Contains(".<", StringComparison.Ordinal);

    private DecodedAttribute DecodeCustomAttribute(CustomAttribute attr)
    {
        var fixedArgs = new List<object?>();
        var namedArgs = new Dictionary<string, object?>(StringComparer.Ordinal);
        BlobReader reader = _reader.GetBlobReader(attr.Value);
        if (reader.Length < 2 || reader.ReadUInt16() != 1)
            return new DecodedAttribute(fixedArgs, namedArgs);

        foreach (string parameterType in AttributeConstructorParameterTypes(attr))
            fixedArgs.Add(ReadCustomAttributeValue(ref reader, parameterType));

        if (reader.RemainingBytes >= 2)
        {
            int namedCount = reader.ReadUInt16();
            for (int i = 0; i < namedCount && reader.RemainingBytes > 0; i++)
            {
                reader.ReadByte(); // field/property selector
                SerializationTypeCode typeCode = reader.ReadSerializationTypeCode();
                string? enumTypeName = null;
                if (typeCode == SerializationTypeCode.Enum)
                    enumTypeName = reader.ReadSerializedString();
                string name = reader.ReadSerializedString();
                namedArgs[name] = ReadSerializedAttributeValue(ref reader, typeCode, enumTypeName);
            }
        }

        return new DecodedAttribute(fixedArgs, namedArgs);
    }

    private List<string> AttributeConstructorParameterTypes(CustomAttribute attr)
    {
        var provider = new MetadataSignatureProvider(this);
        var context = new GenericContext(Array.Empty<string>(), Array.Empty<string>());
        if (attr.Constructor.Kind == HandleKind.MemberReference)
        {
            MemberReference member = _reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            return member.DecodeMethodSignature(provider, context).ParameterTypes.Select(p => p.Render()).ToList();
        }
        if (attr.Constructor.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinition method = _reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
            return method.DecodeSignature(provider, context).ParameterTypes.Select(p => p.Render()).ToList();
        }
        return new List<string>();
    }

    private object? ReadCustomAttributeValue(ref BlobReader reader, string typeName)
    {
        if (typeName.EndsWith("[]", StringComparison.Ordinal))
        {
            string elementType = typeName.Substring(0, typeName.Length - 2);
            int count = reader.ReadInt32();
            if (count < 0) return null;
            var values = new List<object?>(count);
            for (int i = 0; i < count; i++)
                values.Add(ReadCustomAttributeValue(ref reader, elementType));
            return values;
        }

        switch (typeName)
        {
            case "bool": return reader.ReadBoolean();
            case "byte": return reader.ReadByte();
            case "sbyte": return reader.ReadSByte();
            case "short": return reader.ReadInt16();
            case "ushort": return reader.ReadUInt16();
            case "int": return reader.ReadInt32();
            case "uint": return reader.ReadUInt32();
            case "long": return reader.ReadInt64();
            case "ulong": return reader.ReadUInt64();
            case "float": return reader.ReadSingle();
            case "double": return reader.ReadDouble();
            case "char": return reader.ReadChar();
            case "string": return reader.ReadSerializedString();
            case "Type":
            case "System.Type": return reader.ReadSerializedString();
            default:
                return reader.ReadInt32();
        }
    }

    private object? ReadSerializedAttributeValue(ref BlobReader reader, SerializationTypeCode typeCode, string? enumTypeName)
    {
        switch (typeCode)
        {
            case SerializationTypeCode.Boolean: return reader.ReadBoolean();
            case SerializationTypeCode.Byte: return reader.ReadByte();
            case SerializationTypeCode.SByte: return reader.ReadSByte();
            case SerializationTypeCode.Int16: return reader.ReadInt16();
            case SerializationTypeCode.UInt16: return reader.ReadUInt16();
            case SerializationTypeCode.Int32: return reader.ReadInt32();
            case SerializationTypeCode.UInt32: return reader.ReadUInt32();
            case SerializationTypeCode.Int64: return reader.ReadInt64();
            case SerializationTypeCode.UInt64: return reader.ReadUInt64();
            case SerializationTypeCode.Single: return reader.ReadSingle();
            case SerializationTypeCode.Double: return reader.ReadDouble();
            case SerializationTypeCode.Char: return reader.ReadChar();
            case SerializationTypeCode.String: return reader.ReadSerializedString();
            case SerializationTypeCode.Type: return reader.ReadSerializedString();
            case SerializationTypeCode.Enum: return reader.ReadInt32();
            default: return null;
        }
    }

    private static string FormatAttributeValue(string shortName, object? value)
    {
        if (value is List<object?> list)
            return "{" + string.Join(",", list.Select(v => FormatAttributeValue(shortName, v))) + "}";
        if (shortName == "DynamicallyAccessedMembers" && value is int i)
            return FormatDynamicallyAccessedMemberTypes(i);
        if (shortName == "StringSyntax" && value is string syntax && IsSimpleAttributeIdentifier(syntax))
            return syntax;
        return Literal(value);
    }


    private static bool IsSimpleAttributeIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!(char.IsLetter(value[0]) || value[0] == '_')) return false;
        for (int i = 1; i < value.Length; i++)
        {
            char ch = value[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_')) return false;
        }
        return true;
    }

    private static string FormatDynamicallyAccessedMemberTypes(int value)
    {
        uint remaining = unchecked((uint)value);
        if (remaining == 0) return "None";
        if (remaining == 0xFFFFFFFFu) return "All";

        var flags = new List<string>();
        foreach (KeyValuePair<uint, string> known in DynamicallyAccessedMemberTypeFlags)
        {
            if ((remaining & known.Key) == known.Key)
            {
                flags.Add(known.Value);
                remaining &= ~known.Key;
            }
        }

        if (remaining != 0) flags.Add("0x" + remaining.ToString("X", CultureInfo.InvariantCulture));
        return string.Join("|", flags);
    }

    private static readonly KeyValuePair<uint, string>[] DynamicallyAccessedMemberTypeFlags =
    {
        new(2228608u, "AllNestedTypes"),
        new(2097280u, "PublicNestedTypesWithInherited"),
        new(1064967u, "AllConstructors"),
        new(1048579u, "PublicConstructorsWithInherited"),
        new(530432u, "AllEvents"),
        new(528384u, "NonPublicEventsWithInherited"),
        new(263680u, "AllProperties"),
        new(263168u, "NonPublicPropertiesWithInherited"),
        new(131328u, "NonPublicNestedTypesWithInherited"),
        new(65632u, "AllFields"),
        new(65600u, "NonPublicFieldsWithInherited"),
        new(32792u, "AllMethods"),
        new(32784u, "NonPublicMethodsWithInherited"),
        new(16388u, "NonPublicConstructorsWithInherited"),
        new(8192u, "Interfaces"),
        new(4096u, "NonPublicEvents"),
        new(2048u, "PublicEvents"),
        new(1024u, "NonPublicProperties"),
        new(512u, "PublicProperties"),
        new(256u, "NonPublicNestedTypes"),
        new(128u, "PublicNestedTypes"),
        new(64u, "NonPublicFields"),
        new(32u, "PublicFields"),
        new(16u, "NonPublicMethods"),
        new(8u, "PublicMethods"),
        new(4u, "NonPublicConstructors"),
        new(3u, "PublicConstructors"),
        new(1u, "PublicParameterlessConstructor"),
    };

    private readonly struct DecodedAttribute
    {
        public DecodedAttribute(List<object?> fixedArguments, Dictionary<string, object?> namedArguments)
        {
            FixedArguments = fixedArguments;
            NamedArguments = namedArguments;
        }

        public List<object?> FixedArguments { get; }
        public Dictionary<string, object?> NamedArguments { get; }
    }

    public string AttributeTypeFullName(CustomAttribute attribute)
    {
        EntityHandle parent = AttributeTypeHandle(attribute);
        if (parent.Kind == HandleKind.TypeDefinition)
            return RawTypeDefinitionFullName((TypeDefinitionHandle)parent);
        if (parent.Kind == HandleKind.TypeReference)
            return RawTypeReferenceFullName((TypeReferenceHandle)parent);
        return "<attribute>";
    }

    private EntityHandle AttributeTypeHandle(CustomAttribute attribute)
    {
        EntityHandle ctor = attribute.Constructor;
        if (ctor.Kind == HandleKind.MemberReference)
            return _reader.GetMemberReference((MemberReferenceHandle)ctor).Parent;
        if (ctor.Kind == HandleKind.MethodDefinition)
            return _reader.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType();
        return default;
    }

    public string EntityTypeFullName(EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeDefinition) return RawTypeDefinitionFullName((TypeDefinitionHandle)handle);
        if (handle.Kind == HandleKind.TypeReference) return RawTypeReferenceFullName((TypeReferenceHandle)handle);
        return EntityTypeName(handle, new GenericContext(Array.Empty<string>(), Array.Empty<string>()), includeNamespace: true);
    }

    private string RawTypeDefinitionFullName(TypeDefinitionHandle handle)
    {
        string ns = TypeDefinitionNamespace(handle);
        string nested = RawTypeDefinitionNestedName(handle);
        return ns.Length == 0 ? nested : ns + "." + nested;
    }

    private string RawTypeDefinitionNestedName(TypeDefinitionHandle handle)
    {
        TypeDefinition td = _reader.GetTypeDefinition(handle);
        string simple = SimpleRawName(String(td.Name));
        TypeDefinitionHandle declaring = td.GetDeclaringType();
        if (!declaring.IsNil)
            return RawTypeDefinitionNestedName(declaring) + "." + simple;
        return simple;
    }

    private string RawTypeReferenceFullName(TypeReferenceHandle handle)
    {
        string ns = TypeReferenceNamespace(handle);
        string nested = RawTypeReferenceNestedName(handle);
        return ns.Length == 0 ? nested : ns + "." + nested;
    }

    private string RawTypeReferenceNestedName(TypeReferenceHandle handle)
    {
        TypeReference tr = _reader.GetTypeReference(handle);
        string simple = SimpleRawName(String(tr.Name));
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return RawTypeReferenceNestedName((TypeReferenceHandle)tr.ResolutionScope) + "." + simple;
        return simple;
    }

    private static string SimpleRawName(string metadataName)
    {
        int tick = metadataName.IndexOf('`');
        return tick >= 0 ? metadataName.Substring(0, tick) : metadataName;
    }

    public static string PrimitiveName(PrimitiveTypeCode code)
    {
        switch (code)
        {
            case PrimitiveTypeCode.Void: return "void";
            case PrimitiveTypeCode.Boolean: return "bool";
            case PrimitiveTypeCode.Char: return "char";
            case PrimitiveTypeCode.SByte: return "sbyte";
            case PrimitiveTypeCode.Byte: return "byte";
            case PrimitiveTypeCode.Int16: return "short";
            case PrimitiveTypeCode.UInt16: return "ushort";
            case PrimitiveTypeCode.Int32: return "int";
            case PrimitiveTypeCode.UInt32: return "uint";
            case PrimitiveTypeCode.Int64: return "long";
            case PrimitiveTypeCode.UInt64: return "ulong";
            case PrimitiveTypeCode.Single: return "float";
            case PrimitiveTypeCode.Double: return "double";
            case PrimitiveTypeCode.String: return "string";
            case PrimitiveTypeCode.Object: return "object";
            case PrimitiveTypeCode.IntPtr: return "nint";
            case PrimitiveTypeCode.UIntPtr: return "nuint";
            case PrimitiveTypeCode.TypedReference: return "typedref";
            default: return code.ToString();
        }
    }

    public static string Identifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return "``";
        bool safe = IsIdentifierStart(name[0]);
        for (int i = 1; safe && i < name.Length; i++)
            safe = IsIdentifierPart(name[i]);
        if (safe)
            return CSharpKeywords.Contains(name) ? "@" + name : name;
        return "`" + EscapeBacktickQuoted(name) + "`";
    }

    private static bool IsIdentifierStart(char ch) => ch == '_' || char.IsLetter(ch);
    private static bool IsIdentifierPart(char ch) => ch == '_' || char.IsLetterOrDigit(ch);

    private static string EscapeBacktickQuoted(string value)
        => EscapeStringCore(value).Replace("`", "\\`");

    public static string Literal(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return "\"" + EscapeStringCore(s) + "\"";
        if (value is char ch) return "'" + EscapeCharCore(ch) + "'";
        if (value is bool b) return b ? "true" : "false";
        if (value is float f) return FloatLiteral(f);
        if (value is double d) return DoubleLiteral(d);
        if (value is decimal m) return m.ToString(CultureInfo.InvariantCulture) + "m";
        if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "null";
        return value.ToString() ?? "null";
    }


    private static string FloatLiteral(float value)
    {
        if (float.IsNaN(value)) return "float.NaN";
        if (float.IsPositiveInfinity(value)) return "float.PositiveInfinity";
        if (float.IsNegativeInfinity(value)) return "float.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "f";
    }

    private static string DoubleLiteral(double value)
    {
        if (double.IsNaN(value)) return "double.NaN";
        if (double.IsPositiveInfinity(value)) return "double.PositiveInfinity";
        if (double.IsNegativeInfinity(value)) return "double.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "d";
    }

    private static string EscapeStringCore(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\0': sb.Append("\\0"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\v': sb.Append("\\v"); break;
                default:
                    if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }


    private static bool ShouldEscapeCharLiteral(char ch)
    {
        return char.IsControl(ch)
            || char.IsSurrogate(ch)
            || char.IsWhiteSpace(ch) && ch != ' '
            || ch == '\uFFFF'
            || ch == '\uFFFE';
    }

    private static string EscapeCharCore(char ch)
    {
        switch (ch)
        {
            case '\\': return "\\\\";
            case '\'': return "\\'";
            case '\0': return "\\0";
            case '\a': return "\\a";
            case '\b': return "\\b";
            case '\f': return "\\f";
            case '\n': return "\\n";
            case '\r': return "\\r";
            case '\t': return "\\t";
            case '\v': return "\\v";
            default:
                if (ShouldEscapeCharLiteral(ch)) return "\\u" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture);
                return ch.ToString();
        }
    }
}
