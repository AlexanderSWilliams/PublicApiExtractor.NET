using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PublicApiExtractorV2;

public static class PublicApiExtractor
{
    public static string ExtractPublicApiText(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath)) throw new ArgumentException("Assembly path is required.", nameof(assemblyPath));

        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader peReader = new(stream);
        if (!peReader.HasMetadata) throw new InvalidOperationException("The file does not contain CLI metadata.");

        MetadataReader reader = peReader.GetMetadataReader();
        ApiAssembly model = MetadataApiReader.Read(reader);
        return CanonicalApiWriter.Write(model);
    }
}

internal sealed class MetadataApiReader
{
    private readonly MetadataReader _reader;
    private readonly MetadataNames _names;
    private readonly MetadataSignatureProvider _signatureProvider;
    private readonly MetadataNamePolicy _namePolicy;
    private readonly byte? _rootNullableContext;
    private readonly Dictionary<string, EnumInfo?> _enumInfoByFullName = new(StringComparer.Ordinal);

    private MetadataApiReader(MetadataReader reader)
    {
        _reader = reader;
        _namePolicy = MetadataNamePolicy.Build(reader);
        _names = new MetadataNames(reader, _namePolicy);
        _signatureProvider = new MetadataSignatureProvider(_names);
        _rootNullableContext = ComputeRootNullableContext();
    }

    public static ApiAssembly Read(MetadataReader reader)
        => new MetadataApiReader(reader).ReadAssembly();

    private byte? ComputeRootNullableContext()
    {
        byte? context = null;
        if (_reader.IsAssembly)
            context = _names.NullableContext(_reader.GetAssemblyDefinition().GetCustomAttributes(), context);
        context = _names.NullableContext(_reader.GetModuleDefinition().GetCustomAttributes(), context);
        return context;
    }

    private byte? NullableContextForType(TypeDefinitionHandle handle)
    {
        var chain = new Stack<TypeDefinitionHandle>();
        TypeDefinitionHandle current = handle;
        while (!current.IsNil)
        {
            chain.Push(current);
            current = _reader.GetTypeDefinition(current).GetDeclaringType();
        }

        byte? context = _rootNullableContext;
        while (chain.Count != 0)
            context = _names.NullableContext(_reader.GetTypeDefinition(chain.Pop()).GetCustomAttributes(), context);
        return context;
    }

    private ApiAssembly ReadAssembly()
    {
        string moduleName = _names.String(_reader.GetModuleDefinition().Name);
        var asm = new ApiAssembly
        {
            ModuleName = moduleName,
            MetadataTypeDefinitionCount = _reader.TypeDefinitions.Count,
            MetadataExportedTypeCount = _reader.ExportedTypes.Count
        };
        asm.NamespacesUsed.AddRange(_namePolicy.ImportedNamespaces);

        if (!_reader.IsAssembly)
        {
            asm.AssemblyName = moduleName;
        }
        else
        {
            AssemblyDefinition ad = _reader.GetAssemblyDefinition();
            asm.AssemblyName = ad.GetAssemblyName().FullName ?? _names.String(ad.Name);
        }

        PopulateReferenceTables(asm);
        List<string> typeForwards = TypeForwardLines().ToList();
        asm.TypeForwards.AddRange(typeForwards);
        asm.PublicExportedTypeCount = typeForwards.Count;

        foreach (TypeDefinitionHandle handle in _reader.TypeDefinitions)
        {
            if (!IsReachablePublicType(handle)) continue;
            TypeDefinition td = _reader.GetTypeDefinition(handle);
            if (IsCompilerGenerated(td.GetCustomAttributes())) continue;

            ApiType type = ReadType(handle, td);
            asm.Types.Add(type);
        }
        asm.PublicTypeDefinitionCount = asm.Types.Count;

        asm.Types.Sort((a, b) => string.CompareOrdinal(a.Namespace + "." + a.Name, b.Namespace + "." + b.Name));
        return asm;
    }

    private void PopulateReferenceTables(ApiAssembly asm)
    {
        var assemblyRefs = _namePolicy.UsedAssemblyReferences
            .Select(h => new { Handle = h, Identity = AssemblyReferenceIdentity(h) })
            .OrderBy(x => x.Identity, StringComparer.Ordinal)
            .ThenBy(x => x.Handle.GetHashCode())
            .ToList();

        var aliases = new Dictionary<AssemblyReferenceHandle, string>();
        for (int i = 0; i < assemblyRefs.Count; i++)
        {
            string alias = "A" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            aliases[assemblyRefs[i].Handle] = alias;
            asm.AssemblyReferenceLines.Add(alias + " " + assemblyRefs[i].Identity);
        }

        var typeRefs = _namePolicy.UsedTypeReferences
            .Select(h => new { Handle = h, Identity = _names.TypeReferenceIdentityName(h) })
            .Where(x => !IsNormalizedAwayTypeReference(x.Identity))
            .OrderBy(x => x.Identity, StringComparer.Ordinal)
            .ThenBy(x => x.Handle.GetHashCode())
            .ToList();

        for (int i = 0; i < typeRefs.Count; i++)
        {
            string alias = "R" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string scope = TypeReferenceScope(typeRefs[i].Handle, aliases);
            asm.TypeReferenceLines.Add(alias + " " + scope + ":" + typeRefs[i].Identity);
        }
    }

    private string AssemblyReferenceIdentity(AssemblyReferenceHandle handle)
    {
        AssemblyReference ar = _reader.GetAssemblyReference(handle);
        AssemblyName name = ar.GetAssemblyName();
        return name.FullName ?? _names.String(ar.Name);
    }

    private static bool IsNormalizedAwayTypeReference(string identity)
    {
        // These metadata-only helper types are normalized into public C# surface
        // syntax elsewhere: modreq(InAttribute) T& -> ref readonly T, and
        // modreq(UnmanagedType) ValueType -> unmanaged.  Do not keep them in
        // the # tref table when they no longer appear in the emitted records.
        return string.Equals(identity, "System.Runtime.InteropServices.InAttribute", StringComparison.Ordinal)
            || string.Equals(identity, "System.Runtime.CompilerServices.IsUnmanagedAttribute", StringComparison.Ordinal)
            || string.Equals(identity, "System.Runtime.CompilerServices.UnmanagedType", StringComparison.Ordinal)
            || string.Equals(identity, "System.Runtime.InteropServices.UnmanagedType", StringComparison.Ordinal);
    }

    private string TypeReferenceScope(TypeReferenceHandle handle, IReadOnlyDictionary<AssemblyReferenceHandle, string> aliases)
    {
        TypeReference tr = _reader.GetTypeReference(handle);
        EntityHandle scope = tr.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
            return TypeReferenceScope((TypeReferenceHandle)scope, aliases);
        if (scope.Kind == HandleKind.AssemblyReference && aliases.TryGetValue((AssemblyReferenceHandle)scope, out string? alias))
            return alias;
        if (scope.Kind == HandleKind.ModuleReference)
            return "module:" + _names.String(_reader.GetModuleReference((ModuleReferenceHandle)scope).Name);
        if (scope.Kind == HandleKind.ModuleDefinition)
            return "module:" + _names.String(_reader.GetModuleDefinition().Name);
        return scope.Kind.ToString();
    }

    private IEnumerable<string> TypeForwardLines()
    {
        foreach (ExportedTypeHandle handle in _reader.ExportedTypes)
        {
            ExportedType exported = _reader.GetExportedType(handle);
            // Facade/reference assemblies often encode their entire public surface as
            // ExportedType forwarders whose visibility flags are not reliable enough to
            // use as an exclusion filter. Forwarders are themselves the public contract,
            // so emit all of them. For non-forwarded exported types, keep the visibility
            // filter to avoid reporting private implementation details from multi-module
            // assemblies.
            if (!exported.IsForwarder && !VisibilityPolicy.IsVisibleType(exported.Attributes)) continue;
            string kind = exported.IsForwarder ? "forward" : "export";
            string name = ExportedTypeFullName(handle);
            string target = ExportedTypeTarget(exported.Implementation);
            yield return "X " + kind + " " + name + " -> " + target;
        }
    }

    private string ExportedTypeFullName(ExportedTypeHandle handle)
    {
        ExportedType exported = _reader.GetExportedType(handle);
        string name = MetadataNames.TypeNameWithSyntheticGenericParameters(_names.String(exported.Name));
        if (exported.Implementation.Kind == HandleKind.ExportedType)
            return ExportedTypeFullName((ExportedTypeHandle)exported.Implementation) + "." + name;
        string ns = _names.String(exported.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private string ExportedTypeTarget(EntityHandle implementation)
    {
        switch (implementation.Kind)
        {
            case HandleKind.AssemblyReference:
                return AssemblyReferenceIdentity((AssemblyReferenceHandle)implementation);
            case HandleKind.AssemblyFile:
                return _names.String(_reader.GetAssemblyFile((AssemblyFileHandle)implementation).Name);
            case HandleKind.ExportedType:
                return ExportedTypeFullName((ExportedTypeHandle)implementation);
            default:
                return implementation.Kind.ToString();
        }
    }

    private bool IsReachablePublicType(TypeDefinitionHandle handle)
    {
        TypeDefinition td = _reader.GetTypeDefinition(handle);
        TypeAttributes attrs = td.Attributes;
        TypeDefinitionHandle declaring = td.GetDeclaringType();
        if (declaring.IsNil) return VisibilityPolicy.IsTopLevelPublic(attrs);
        return VisibilityPolicy.IsNestedExternallyVisible(attrs) && IsReachablePublicType(declaring);
    }

    private ApiType ReadType(TypeDefinitionHandle handle, TypeDefinition td)
    {
        _names.SetCurrentNamespace(_names.TypeDefinitionNamespace(handle));
        var typeParamNames = _names.GenericParameterNames(td.GetGenericParameters());
        var context = new GenericContext(typeParamNames, Array.Empty<string>());
        byte? nullableContext = NullableContextForType(handle);
        var model = new ApiType
        {
            Namespace = _names.TypeDefinitionNamespace(handle),
            Name = _names.TypeDefinitionDisplayName(handle, includeNamespace: false, includeGenericParameters: true),
            Declaration = TypeDeclaration(handle, td, context, nullableContext)
        };

        if (IsEnum(td))
        {
            model.EnumMembers.AddRange(EnumMembers(td));
        }
        else if (IsDelegate(td))
        {
            // The delegate invocation signature is represented on the T line.
        }
        else
        {
            model.Constructors.AddRange(Constructors(td, model.Name, context, nullableContext));
            model.Fields.AddRange(Fields(td, context, nullableContext));
            model.Properties.AddRange(Properties(td, context, nullableContext));
            model.Events.AddRange(Events(td, context, nullableContext));
            model.Methods.AddRange(Methods(td, context, nullableContext));
        }

        model.Constructors.Sort(StringComparer.Ordinal);
        model.Fields.Sort(StringComparer.Ordinal);
        model.Properties.Sort(StringComparer.Ordinal);
        model.Events.Sort(StringComparer.Ordinal);
        model.Methods.Sort(StringComparer.Ordinal);
        model.EnumMembers.Sort(StringComparer.Ordinal);
        return model;
    }

    private string TypeDeclaration(TypeDefinitionHandle handle, TypeDefinition td, GenericContext context, byte? typeNullableContext)
    {
        string visibility = VisibilityPolicy.TypeVisibility(td.Attributes);
        string name = _names.TypeDefinitionDisplayName(handle, includeNamespace: false, includeGenericParameters: false);
        string gp = _names.GenericParameterList(td.GetGenericParameters());
        string where = _names.GenericWhereClauses(td.GetGenericParameters(), context);

        if (IsDelegate(td))
            return DelegateDeclaration(td, visibility, name, gp, context, typeNullableContext);

        string kind = TypeKind(td);
        string line = visibility + kind + " " + name + gp;
        if (IsEnum(td))
        {
            string underlying = EnumUnderlyingType(td, context);
            if (underlying != "int") line += " : " + underlying;
        }
        else
        {
            string? baseType = null;
            if (!td.BaseType.IsNil)
            {
                string b = _names.EntityTypeName(td.BaseType, context);
                string baseFull = _names.EntityTypeFullName(td.BaseType);
                if (baseFull != "System.Object" && baseFull != "System.ValueType" && baseFull != "System.Enum" && baseFull != "System.MulticastDelegate" && baseFull != "System.Delegate")
                    baseType = b;
            }

            var interfaces = new SortedSet<string>(StringComparer.Ordinal);
            foreach (InterfaceImplementationHandle ih in td.GetInterfaceImplementations())
            {
                string iface = _names.EntityTypeName(_reader.GetInterfaceImplementation(ih).Interface, context);
                interfaces.Add(iface);
            }

            var bases = new List<string>();
            if (baseType != null) bases.Add(baseType);
            bases.AddRange(interfaces.Where(i => baseType == null || !string.Equals(i, baseType, StringComparison.Ordinal)));
            if (bases.Count > 0) line += " : " + string.Join(",", bases);
        }

        if (where.Length != 0) line += where;

        var suffix = new List<string>();
        if (IsFlagsEnum(td)) suffix.Add("[Flags]");
        if ((td.Attributes & TypeAttributes.Serializable) != 0) suffix.Add("[Serializable]");
        if (suffix.Count > 0) line += " " + string.Join(" ", suffix.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
        line += _names.SemanticAttributeSuffix(td.GetCustomAttributes());

        return line;
    }

    private string TypeKind(TypeDefinition td)
    {
        if (IsEnum(td)) return "enum";
        if (IsDelegate(td)) return "delegate";
        if ((td.Attributes & TypeAttributes.Interface) != 0) return "interface";
        if (IsValueType(td))
        {
            string prefix = "";
            if (IsReadOnly(td)) prefix += "readonly ";
            if (IsByRefLike(td)) prefix += "ref ";
            return prefix + "struct";
        }

        if (IsException(td)) return "exception";
        bool isAbstract = (td.Attributes & TypeAttributes.Abstract) != 0;
        bool isSealed = (td.Attributes & TypeAttributes.Sealed) != 0;
        if (isAbstract && isSealed) return "static class";
        if (isAbstract) return "abstract class";
        if (isSealed) return "sealed class";
        return "class";
    }

    private string DelegateDeclaration(TypeDefinition td, string visibility, string name, string genericParameterList, GenericContext typeContext, byte? typeNullableContext)
    {
        foreach (MethodDefinitionHandle mh in td.GetMethods())
        {
            MethodDefinition m = _reader.GetMethodDefinition(mh);
            if (_names.String(m.Name) != "Invoke") continue;
            byte? methodNullableContext = _names.NullableContext(m.GetCustomAttributes(), typeNullableContext);
            MethodSignature<SignatureTypeName> sig = m.DecodeSignature(_signatureProvider, typeContext);
            string parameters = FormatSignatureParameters(sig, m.GetParameters(), typeContext, methodNullableContext, startIndex: 0, firstParameterIsExtensionThis: false, signatureAttributes: m.GetCustomAttributes());
            string where = _names.GenericWhereClauses(td.GetGenericParameters(), typeContext);
            CustomAttributeHandleCollection? returnAttrs = ReturnParameterAttributes(m.GetParameters());
            string returnAttrPrefix = _names.SemanticAttributePrefix(returnAttrs);
            string returnType = CleanReturnType(RenderMethodReturnType(sig, m, typeNullableContext, methodNullableContext, returnAttrs));
            string semantic = _names.SemanticAttributeSuffix(td.GetCustomAttributes());
            return visibility + "delegate " + returnAttrPrefix + returnType + " " + name + genericParameterList + "(" + parameters + ")" + where + semantic;
        }
        return visibility + "delegate " + name + genericParameterList + _names.SemanticAttributeSuffix(td.GetCustomAttributes());
    }

    private string EnumUnderlyingType(TypeDefinition td, GenericContext context)
    {
        foreach (FieldDefinitionHandle fh in td.GetFields())
        {
            FieldDefinition f = _reader.GetFieldDefinition(fh);
            if (_names.String(f.Name) == "value__")
                return CleanReturnType(f.DecodeSignature(_signatureProvider, context).Render());
        }
        return "int";
    }

    private IEnumerable<string> Constructors(TypeDefinition td, string typeName, GenericContext context, byte? typeNullableContext)
    {
        foreach (MethodDefinitionHandle mh in td.GetMethods())
        {
            MethodDefinition m = _reader.GetMethodDefinition(mh);
            string n = _names.String(m.Name);
            if (n != ".ctor" && n != ".cctor") continue;
            if (!VisibilityPolicy.IsVisibleMethod(m.Attributes)) continue;
            if (IsCompilerGenerated(m.GetCustomAttributes())) continue;
            string sig = MethodSignature(m, context, typeNullableContext, isConstructor: true, constructorName: typeName);
            if (sig.Length != 0) yield return "C " + sig;
        }
    }

    private IEnumerable<string> Fields(TypeDefinition td, GenericContext context, byte? typeNullableContext)
    {
        foreach (FieldDefinitionHandle fh in td.GetFields())
        {
            FieldDefinition f = _reader.GetFieldDefinition(fh);
            string rawName = _names.String(f.Name);
            if (!VisibilityPolicy.IsVisibleField(f.Attributes)) continue;
            if (IsCompilerGenerated(f.GetCustomAttributes())) continue;
            if ((f.Attributes & FieldAttributes.SpecialName) != 0 && rawName == "value__") continue;

            SignatureTypeName type = f.DecodeSignature(_signatureProvider, context);
            string renderedType = CleanReturnType(_names.RenderNullableType(type, f.GetCustomAttributes(), typeNullableContext));
            string mods = VisibilityPolicy.FieldVisibility(f.Attributes);
            if ((f.Attributes & FieldAttributes.Literal) != 0) mods += "const ";
            else
            {
                if ((f.Attributes & FieldAttributes.Static) != 0) mods += "static ";
                if ((f.Attributes & FieldAttributes.InitOnly) != 0) mods += "readonly ";
            }

            string line = "F " + mods + renderedType + " " + MetadataNames.Identifier(rawName);
            if ((f.Attributes & FieldAttributes.Literal) != 0)
                line += "=" + ConstantDecoder.DecodeLiteral(_reader, f.GetDefaultValue());
            line += _names.SemanticAttributeSuffix(f.GetCustomAttributes());
            yield return line;
        }
    }

    private IEnumerable<string> Properties(TypeDefinition td, GenericContext context, byte? typeNullableContext)
    {
        foreach (PropertyDefinitionHandle ph in td.GetProperties())
        {
            PropertyDefinition p = _reader.GetPropertyDefinition(ph);
            if (IsCompilerGenerated(p.GetCustomAttributes())) continue;
            PropertyAccessors accessors = p.GetAccessors();
            MethodDefinitionHandle getterH = accessors.Getter;
            MethodDefinitionHandle setterH = accessors.Setter;
            MethodDefinition? getter = getterH.IsNil ? null : _reader.GetMethodDefinition(getterH);
            MethodDefinition? setter = setterH.IsNil ? null : _reader.GetMethodDefinition(setterH);
            MethodDefinition? dominant = DominantVisible(getter, setter);
            if (dominant == null) continue;

            byte? propertyNullableContext = _names.NullableContext(p.GetCustomAttributes(), typeNullableContext);
            MethodSignature<SignatureTypeName> sig = p.DecodeSignature(_signatureProvider, context);

            bool useGetterForPropertyType = getter.HasValue && VisibilityPolicy.IsVisibleMethod(getter.Value.Attributes);
            MethodDefinition? typeAccessor = useGetterForPropertyType
                ? getter
                : setter.HasValue && VisibilityPolicy.IsVisibleMethod(setter.Value.Attributes) ? setter : dominant;
            byte? accessorNullableContext = typeAccessor.HasValue
                ? _names.NullableContext(typeAccessor.Value.GetCustomAttributes(), propertyNullableContext)
                : propertyNullableContext;
            CustomAttributeHandleCollection? accessorAttributes = typeAccessor.HasValue
                ? typeAccessor.Value.GetCustomAttributes()
                : null;
            CustomAttributeHandleCollection? accessorTypeAttributes = typeAccessor.HasValue
                ? PropertyAccessorTypeAttributes(typeAccessor.Value, useGetterForPropertyType)
                : null;

            string propertyType = CleanReturnType(_names.RenderNullableType(sig.ReturnType, typeNullableContext, p.GetCustomAttributes(), accessorAttributes, accessorTypeAttributes));
            propertyType = ForceNullableReferenceAnnotationIfNeeded(sig.ReturnType, propertyType, accessorTypeAttributes, defaultValueIsNull: false);
            string name = MetadataNames.Identifier(_names.String(p.Name));
            ParameterHandleCollection? indexerParams = null;
            if (sig.ParameterTypes.Length > 0)
            {
                if (getter.HasValue) indexerParams = getter.Value.GetParameters();
                else if (setter.HasValue) indexerParams = setter.Value.GetParameters();
            }
            string indexArgs = FormatSignatureParameters(sig, indexerParams, context, accessorNullableContext, startIndex: 0, firstParameterIsExtensionThis: false, signatureAttributes: p.GetCustomAttributes());
            string displayName = sig.ParameterTypes.Length > 0 ? "this[" + indexArgs + "]" : name;

            string prefix = "P " + VisibilityPolicy.MethodVisibility(dominant.Value.Attributes) + MethodModifiers(dominant.Value, includeStatic: true, includeAbstractVirtualOverride: true) + propertyType + " " + displayName + " ";
            var access = new List<string>();
            if (getter != null && VisibilityPolicy.IsVisibleMethod(getter.Value.Attributes)) access.Add(AccessorText("get", getter.Value, dominant.Value));
            if (setter != null && VisibilityPolicy.IsVisibleMethod(setter.Value.Attributes)) access.Add(AccessorText(IsInitSetter(setter.Value, context) ? "init" : "set", setter.Value, dominant.Value));
            string semanticSuffix = _names.SemanticAttributeSuffix(p.GetCustomAttributes());
            if (getter != null && VisibilityPolicy.IsVisibleMethod(getter.Value.Attributes))
                semanticSuffix += _names.SemanticAttributeSuffix(ReturnParameterAttributes(getter.Value.GetParameters()), "get");
            if (setter != null && VisibilityPolicy.IsVisibleMethod(setter.Value.Attributes))
                semanticSuffix += _names.SemanticAttributeSuffix(LastParameterAttributes(setter.Value.GetParameters()), "set");
            yield return prefix + string.Join(" ", access) + semanticSuffix;
        }
    }

    private IEnumerable<string> Events(TypeDefinition td, GenericContext context, byte? typeNullableContext)
    {
        foreach (EventDefinitionHandle eh in td.GetEvents())
        {
            EventDefinition e = _reader.GetEventDefinition(eh);
            if (IsCompilerGenerated(e.GetCustomAttributes())) continue;
            EventAccessors accessors = e.GetAccessors();
            if (accessors.Adder.IsNil) continue;
            MethodDefinition add = _reader.GetMethodDefinition(accessors.Adder);
            if (!VisibilityPolicy.IsVisibleMethod(add.Attributes)) continue;
            string type = _names.EntityTypeName(e.Type, context);
            string mods = VisibilityPolicy.MethodVisibility(add.Attributes) + MethodModifiers(add, includeStatic: true, includeAbstractVirtualOverride: true);
            yield return "V " + mods + type + " " + MetadataNames.Identifier(_names.String(e.Name)) + _names.SemanticAttributeSuffix(e.GetCustomAttributes());
        }
    }

    private IEnumerable<string> Methods(TypeDefinition td, GenericContext context, byte? typeNullableContext)
    {
        foreach (MethodDefinitionHandle mh in td.GetMethods())
        {
            MethodDefinition m = _reader.GetMethodDefinition(mh);
            string name = _names.String(m.Name);
            if (name == ".ctor" || name == ".cctor") continue;
            if (!VisibilityPolicy.IsVisibleMethod(m.Attributes)) continue;
            if (IsCompilerGenerated(m.GetCustomAttributes())) continue;
            if (IsAccessorSpecialName(m, name)) continue;
            yield return "M " + MethodSignature(m, context, typeNullableContext, isConstructor: false, constructorName: "");
        }
    }

    private IEnumerable<string> EnumMembers(TypeDefinition td)
    {
        foreach (FieldDefinitionHandle fh in td.GetFields())
        {
            FieldDefinition f = _reader.GetFieldDefinition(fh);
            string rawName = _names.String(f.Name);
            if (rawName == "value__") continue;
            if ((f.Attributes & FieldAttributes.Literal) == 0) continue;
            yield return "E " + MetadataNames.Identifier(rawName) + "=" + ConstantDecoder.DecodeEnumIntegral(_reader, f.GetDefaultValue());
        }
    }

    private string MethodSignature(MethodDefinition m, GenericContext typeContext, byte? typeNullableContext, bool isConstructor, string constructorName)
    {
        var methodParams = _names.GenericParameterNames(m.GetGenericParameters());
        var context = new GenericContext(typeContext.TypeParameters, methodParams);
        byte? methodNullableContext = _names.NullableContext(m.GetCustomAttributes(), typeNullableContext);
        MethodSignature<SignatureTypeName> sig = m.DecodeSignature(_signatureProvider, context);
        string rawName = _names.String(m.Name);
        string name = isConstructor ? constructorName : MetadataNames.Identifier(rawName);
        string gp = _names.GenericParameterList(m.GetGenericParameters());
        string where = _names.GenericWhereClauses(m.GetGenericParameters(), context);
        string parameters = FormatSignatureParameters(sig, m.GetParameters(), context, methodNullableContext, startIndex: 0, firstParameterIsExtensionThis: IsExtensionMethod(m), signatureAttributes: m.GetCustomAttributes());
        string semantic = _names.SemanticAttributeSuffix(m.GetCustomAttributes());

        string mods = VisibilityPolicy.MethodVisibility(m.Attributes) + MethodModifiers(m, includeStatic: true, includeAbstractVirtualOverride: !isConstructor);
        if (isConstructor)
            return mods + name + "(" + parameters + ")" + semantic;

        CustomAttributeHandleCollection? returnAttrs = ReturnParameterAttributes(m.GetParameters());
        string returnAttrPrefix = _names.SemanticAttributePrefix(returnAttrs);
        string returnType = CleanReturnType(RenderMethodReturnType(sig, m, typeNullableContext, methodNullableContext, returnAttrs));
        returnType = ForceNullableReferenceAnnotationIfNeeded(sig.ReturnType, returnType, returnAttrs, defaultValueIsNull: false);
        string? operatorName = OperatorDisplayName(rawName);
        if (operatorName == "implicit" || operatorName == "explicit")
            return mods + operatorName + " operator " + returnType + "(" + parameters + ")" + semantic;
        if (operatorName != null)
            return mods + returnAttrPrefix + returnType + " operator " + operatorName + "(" + parameters + ")" + semantic;

        return mods + returnAttrPrefix + returnType + " " + name + gp + "(" + parameters + ")" + where + semantic;
    }

    private string RenderMethodReturnType(MethodSignature<SignatureTypeName> sig, MethodDefinition method, byte? typeNullableContext, byte? methodNullableContext, CustomAttributeHandleCollection? returnAttrs)
    {
        CustomAttributeHandleCollection methodAttrs = method.GetCustomAttributes();
        IReadOnlyList<string?>? tupleNames = _names.TupleElementNames(returnAttrs) ?? _names.TupleElementNames(methodAttrs);

        byte[]? returnFlags = _names.NullableFlags(returnAttrs);
        if (returnFlags != null)
            return _names.RenderNullableType(sig.ReturnType, returnFlags, _names.NullableContext(returnAttrs, methodNullableContext), tupleNames);

        byte[]? methodFlags = _names.NullableFlags(methodAttrs);
        if (methodFlags != null)
        {
            byte[]? returnSlice = SliceNullableFlags(methodFlags, 0, sig.ReturnType.NullableSlotCount);
            if (returnSlice != null)
                return _names.RenderNullableType(sig.ReturnType, returnSlice, methodNullableContext, tupleNames);
        }

        return _names.RenderNullableType(sig.ReturnType, (byte[]?)null, methodNullableContext, tupleNames);
    }

    private string FormatSignatureParameters(MethodSignature<SignatureTypeName> sig, ParameterHandleCollection? handles, GenericContext context, byte? nullableContext, int startIndex, bool firstParameterIsExtensionThis, CustomAttributeHandleCollection? signatureAttributes = null)
    {
        var parametersBySequence = new Dictionary<int, ParameterMetadata>();
        if (handles.HasValue)
        {
            foreach (ParameterHandle ph in handles.Value)
            {
                if (ph.IsNil) continue;

                Parameter p = _reader.GetParameter(ph);
                string name = p.Name.IsNil ? "" : _names.String(p.Name);
                CustomAttributeHandleCollection attrs = p.GetCustomAttributes();
                parametersBySequence[p.SequenceNumber] = new ParameterMetadata(
                    name,
                    p.Attributes,
                    IsParamArray(attrs),
                    p.GetDefaultValue(),
                    attrs);
            }
        }

        byte[]? signatureNullableFlags = _names.NullableFlags(signatureAttributes);
        int signatureNullableFlagOffset = sig.ReturnType.NullableSlotCount;
        for (int i = 0; i < startIndex && i < sig.ParameterTypes.Length; i++)
            signatureNullableFlagOffset += sig.ParameterTypes[i].NullableSlotCount;

        var parts = new List<string>();
        for (int i = startIndex; i < sig.ParameterTypes.Length; i++)
        {
            SignatureTypeName typeName = sig.ParameterTypes[i];
            int sequence = i + 1;
            bool hasParameterMetadata = parametersBySequence.TryGetValue(sequence, out ParameterMetadata parameter);

            string name = hasParameterMetadata && parameter.Name.Length != 0
                ? MetadataNames.Identifier(parameter.Name)
                : "arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

            bool defaultValueIsNull = hasParameterMetadata && !parameter.DefaultValue.IsNil && ConstantDecoder.Decode(_reader, parameter.DefaultValue) == null;
            CustomAttributeHandleCollection? parameterAttributes = hasParameterMetadata ? parameter.AttributesCollection : null;
            byte[]? parameterNullableFlags = _names.NullableFlags(parameterAttributes);
            if (parameterNullableFlags == null && signatureNullableFlags != null)
                parameterNullableFlags = SliceNullableFlags(signatureNullableFlags, signatureNullableFlagOffset, typeName.NullableSlotCount);
            signatureNullableFlagOffset += typeName.NullableSlotCount;

            IReadOnlyList<string?>? parameterTupleNames = _names.TupleElementNames(parameterAttributes);
            string type = parameterNullableFlags != null
                ? _names.RenderNullableType(typeName, parameterNullableFlags, nullableContext, parameterTupleNames)
                : _names.RenderNullableType(typeName, parameterAttributes, nullableContext);
            type = ForceNullableReferenceAnnotationIfNeeded(typeName, type, parameterAttributes, defaultValueIsNull);
            string modifier = "";
            bool byref = type.EndsWith("&", StringComparison.Ordinal);
            if (byref) type = type.Substring(0, type.Length - 1);

            if (hasParameterMetadata)
            {
                if ((parameter.ParameterAttributes & ParameterAttributes.Out) != 0) modifier = "out ";
                else if ((parameter.ParameterAttributes & ParameterAttributes.In) != 0 && byref) modifier = "in ";
                else if (byref) modifier = "ref ";
                if (parameter.IsParamArray) modifier = "params ";
            }
            else if (byref)
            {
                modifier = "ref ";
            }

            if (firstParameterIsExtensionThis && i == 0)
                modifier = "this " + modifier;

            string attrPrefix = hasParameterMetadata ? _names.SemanticAttributePrefix(parameter.AttributesCollection) : "";
            string text = attrPrefix + modifier + CleanParameterType(type) + " " + name;
            if (hasParameterMetadata && !parameter.DefaultValue.IsNil)
                text += "=" + FormatDefaultValue(typeName, parameter.DefaultValue);
            parts.Add(text);
        }
        return string.Join(",", parts);
    }

    private static byte[]? SliceNullableFlags(byte[] flags, int offset, int count)
    {
        if (count <= 0 || offset < 0 || offset >= flags.Length || offset + count > flags.Length)
            return null;

        var slice = new byte[count];
        Array.Copy(flags, offset, slice, 0, count);
        return slice;
    }

    private string FormatDefaultValue(SignatureTypeName typeName, ConstantHandle handle)
    {
        object? value = ConstantDecoder.Decode(_reader, handle);
        if (value == null)
            return typeName.ShouldRenderNullDefaultAsDefault ? "default" : "null";

        if (TryFormatEnumDefault(typeName, value, out string? enumDefault))
            return enumDefault;

        return MetadataNames.Literal(value);
    }

    private string ForceNullableReferenceAnnotationIfNeeded(SignatureTypeName typeName, string renderedType, CustomAttributeHandleCollection? attrs, bool defaultValueIsNull)
    {
        bool byref = renderedType.EndsWith("&", StringComparison.Ordinal);
        string coreType = byref ? renderedType.Substring(0, renderedType.Length - 1) : renderedType;
        if (!typeName.CanUseReferenceNullDefault || RenderedTypeAllowsNull(coreType))
            return renderedType;

        if (defaultValueIsNull || HasNullabilityFlowAttribute(attrs))
            return byref ? coreType + "?&" : coreType + "?";

        return renderedType;
    }

    private bool HasNullabilityFlowAttribute(CustomAttributeHandleCollection? attrs)
    {
        if (!attrs.HasValue) return false;
        return _names.HasAttribute(attrs.Value, "System.Diagnostics.CodeAnalysis.MaybeNullAttribute")
            || _names.HasAttribute(attrs.Value, "System.Diagnostics.CodeAnalysis.MaybeNullWhenAttribute")
            || _names.HasAttribute(attrs.Value, "System.Diagnostics.CodeAnalysis.NotNullWhenAttribute");
    }

    private static bool RenderedTypeAllowsNull(string renderedType)
        => renderedType.EndsWith("?", StringComparison.Ordinal);

    private bool TryFormatEnumDefault(SignatureTypeName typeName, object value, out string? rendered)
    {
        rendered = null;
        string fullName = typeName.FullName;
        if (string.IsNullOrEmpty(fullName)) return false;

        EnumInfo? info = GetEnumInfo(fullName);
        if (info == null) return false;

        ulong raw = IntegralBits(value);
        if (info.ByValue.TryGetValue(raw, out string? exactName))
        {
            rendered = exactName;
            return true;
        }

        if (!info.IsFlags || raw == 0)
            return false;

        ulong remaining = raw;
        var parts = new List<string>();
        var candidates = info.Members
            .Where(m => m.Value != 0 && (raw & m.Value) == m.Value)
            .OrderByDescending(m => PopCount(m.Value))
            .ThenByDescending(m => m.Value)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        foreach (EnumMember member in candidates)
        {
            if ((remaining & member.Value) != member.Value)
                continue;

            parts.Add(member.Name);
            remaining &= ~member.Value;
            if (remaining == 0) break;
        }

        if (parts.Count == 0)
            return false;

        if (remaining != 0)
            parts.Add(MetadataNames.Literal(remaining));

        rendered = string.Join("|", parts);
        return true;
    }

    private EnumInfo? GetEnumInfo(string fullName)
    {
        if (_enumInfoByFullName.TryGetValue(fullName, out EnumInfo? cached))
            return cached;

        foreach (TypeDefinitionHandle handle in _reader.TypeDefinitions)
        {
            TypeDefinition td = _reader.GetTypeDefinition(handle);
            if (!IsEnum(td)) continue;
            if (!string.Equals(_names.EntityTypeFullName(handle), fullName, StringComparison.Ordinal)) continue;

            var members = new List<EnumMember>();
            var byValue = new Dictionary<ulong, string>();
            foreach (FieldDefinitionHandle fieldHandle in td.GetFields())
            {
                FieldDefinition field = _reader.GetFieldDefinition(fieldHandle);
                string name = _names.String(field.Name);
                if (name == "value__" || (field.Attributes & FieldAttributes.Literal) == 0)
                    continue;

                object? constant = ConstantDecoder.Decode(_reader, field.GetDefaultValue());
                if (constant == null) continue;
                ulong raw = IntegralBits(constant);
                string renderedName = MetadataNames.Identifier(name);
                members.Add(new EnumMember(renderedName, raw));
                if (!byValue.ContainsKey(raw))
                    byValue.Add(raw, renderedName);
            }

            var info = new EnumInfo(IsFlagsEnum(td), members, byValue);
            _enumInfoByFullName[fullName] = info;
            return info;
        }

        _enumInfoByFullName[fullName] = null;
        return null;
    }

    private static ulong IntegralBits(object value)
    {
        switch (value)
        {
            case sbyte v: return unchecked((ulong)v);
            case short v: return unchecked((ulong)v);
            case int v: return unchecked((ulong)v);
            case long v: return unchecked((ulong)v);
            case byte v: return v;
            case ushort v: return v;
            case uint v: return v;
            case ulong v: return v;
            case char v: return v;
            case bool v: return v ? 1UL : 0UL;
            default: return Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static int PopCount(ulong value)
    {
        int count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }


    private CustomAttributeHandleCollection? PropertyAccessorTypeAttributes(MethodDefinition accessor, bool isGetter)
    {
        if (isGetter)
            return ReturnParameterAttributes(accessor.GetParameters());

        return LastParameterAttributes(accessor.GetParameters());
    }

    private CustomAttributeHandleCollection? LastParameterAttributes(ParameterHandleCollection handles)
    {
        ParameterHandle best = default;
        int bestSequence = -1;
        foreach (ParameterHandle ph in handles)
        {
            if (ph.IsNil) continue;
            Parameter p = _reader.GetParameter(ph);
            if (p.SequenceNumber > bestSequence)
            {
                best = ph;
                bestSequence = p.SequenceNumber;
            }
        }

        return best.IsNil ? null : _reader.GetParameter(best).GetCustomAttributes();
    }

    private CustomAttributeHandleCollection? ReturnParameterAttributes(ParameterHandleCollection handles)
    {
        foreach (ParameterHandle ph in handles)
        {
            if (ph.IsNil) continue;
            Parameter p = _reader.GetParameter(ph);
            if (p.SequenceNumber == 0)
                return p.GetCustomAttributes();
        }
        return null;
    }

    private readonly struct ParameterMetadata
    {
        public ParameterMetadata(string name, ParameterAttributes parameterAttributes, bool isParamArray, ConstantHandle defaultValue, CustomAttributeHandleCollection attributesCollection)
        {
            Name = name ?? "";
            ParameterAttributes = parameterAttributes;
            IsParamArray = isParamArray;
            DefaultValue = defaultValue;
            AttributesCollection = attributesCollection;
        }

        public string Name { get; }
        public ParameterAttributes ParameterAttributes { get; }
        public bool IsParamArray { get; }
        public ConstantHandle DefaultValue { get; }
        public CustomAttributeHandleCollection AttributesCollection { get; }
    }

    private bool IsParamArray(CustomAttributeHandleCollection attrs)
        => _names.HasAttribute(attrs, "System.ParamArrayAttribute");

    private sealed class EnumInfo
    {
        public EnumInfo(bool isFlags, List<EnumMember> members, Dictionary<ulong, string> byValue)
        {
            IsFlags = isFlags;
            Members = members;
            ByValue = byValue;
        }

        public bool IsFlags { get; }
        public List<EnumMember> Members { get; }
        public Dictionary<ulong, string> ByValue { get; }
    }

    private readonly struct EnumMember
    {
        public EnumMember(string name, ulong value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }
        public ulong Value { get; }
    }

    private static string? OperatorDisplayName(string rawName)
    {
        switch (rawName)
        {
            case "op_Implicit": return "implicit";
            case "op_Explicit": return "explicit";
            case "op_UnaryPlus": return "+";
            case "op_UnaryNegation": return "-";
            case "op_LogicalNot": return "!";
            case "op_OnesComplement": return "~";
            case "op_Increment": return "++";
            case "op_Decrement": return "--";
            case "op_True": return "true";
            case "op_False": return "false";
            case "op_Addition": return "+";
            case "op_Subtraction": return "-";
            case "op_Multiply": return "*";
            case "op_Division": return "/";
            case "op_Modulus": return "%";
            case "op_BitwiseAnd": return "&";
            case "op_BitwiseOr": return "|";
            case "op_ExclusiveOr": return "^";
            case "op_LeftShift": return "<<";
            case "op_RightShift": return ">>";
            case "op_UnsignedRightShift": return ">>>";
            case "op_Equality": return "==";
            case "op_Inequality": return "!=";
            case "op_GreaterThan": return ">";
            case "op_LessThan": return "<";
            case "op_GreaterThanOrEqual": return ">=";
            case "op_LessThanOrEqual": return "<=";
            default: return rawName.StartsWith("op_", StringComparison.Ordinal) ? rawName.Substring(3) : null;
        }
    }

    private static string CleanReturnType(string type)
    {
        return RemoveIsExternalInitModifier(ConvertByRefReturn(ConvertReadonlyByRefReturn(type)));
    }

    private static string CleanParameterType(string type)
    {
        return RemoveKnownParameterModifiers(type);
    }

    private static string RemoveIsExternalInitModifier(string type)
    {
        return type
            .Replace("modreq(System.Runtime.CompilerServices.IsExternalInit) ", "", StringComparison.Ordinal)
            .Replace("modreq(IsExternalInit) ", "", StringComparison.Ordinal);
    }

    private static string RemoveKnownParameterModifiers(string type)
    {
        return RemoveIsExternalInitModifier(type)
            .Replace("modreq(System.Runtime.CompilerServices.InAttribute) ", "", StringComparison.Ordinal)
            .Replace("modreq(InAttribute) ", "", StringComparison.Ordinal);
    }

    private static string ConvertReadonlyByRefReturn(string type)
    {
        const string markerFull = "modreq(System.Runtime.CompilerServices.InAttribute) ";
        const string markerShort = "modreq(InAttribute) ";
        string? inner = null;
        if (type.StartsWith(markerFull, StringComparison.Ordinal))
            inner = type.Substring(markerFull.Length);
        else if (type.StartsWith(markerShort, StringComparison.Ordinal))
            inner = type.Substring(markerShort.Length);

        if (inner == null || !inner.EndsWith("&", StringComparison.Ordinal))
            return type;

        return "ref readonly " + inner.Substring(0, inner.Length - 1);
    }

    private static string ConvertByRefReturn(string type)
    {
        if (type.StartsWith("ref readonly ", StringComparison.Ordinal))
            return type;

        if (!type.EndsWith("&", StringComparison.Ordinal))
            return type;

        return "ref " + type.Substring(0, type.Length - 1);
    }

    private bool IsInitSetter(MethodDefinition setter, GenericContext context)
    {
        MethodSignature<SignatureTypeName> sig = setter.DecodeSignature(_signatureProvider, context);
        return sig.ReturnType.Render().Contains("IsExternalInit", StringComparison.Ordinal);
    }

    private static string AccessorText(string keyword, MethodDefinition accessor, MethodDefinition dominant)
    {
        string vis = VisibilityPolicy.MethodVisibility(accessor.Attributes);
        string dominantVis = VisibilityPolicy.MethodVisibility(dominant.Attributes);
        return vis == dominantVis ? keyword : vis + keyword;
    }

    private static MethodDefinition? DominantVisible(MethodDefinition? a, MethodDefinition? b)
    {
        bool av = a.HasValue && VisibilityPolicy.IsVisibleMethod(a.Value.Attributes);
        bool bv = b.HasValue && VisibilityPolicy.IsVisibleMethod(b.Value.Attributes);
        if (!av && !bv) return null;
        if (av && !bv) return a;
        if (!av && bv) return b;
        return AccessRank(b!.Value.Attributes) > AccessRank(a!.Value.Attributes) ? b : a;
    }

    private static int AccessRank(MethodAttributes attrs)
    {
        MethodAttributes v = attrs & MethodAttributes.MemberAccessMask;
        if (v == MethodAttributes.Public) return 3;
        if (v == MethodAttributes.FamORAssem) return 2;
        if (v == MethodAttributes.Family) return 1;
        return 0;
    }

    private bool IsCompilerGenerated(CustomAttributeHandleCollection attrs)
        => _names.HasAttribute(attrs, "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    private bool IsExtensionMethod(MethodDefinition m)
        => (m.Attributes & MethodAttributes.Static) != 0
           && _names.HasAttribute(m.GetCustomAttributes(), "System.Runtime.CompilerServices.ExtensionAttribute");

    private bool IsFlagsEnum(TypeDefinition td)
        => _names.HasAttribute(td.GetCustomAttributes(), "System.FlagsAttribute");

    private bool IsReadOnly(TypeDefinition td)
        => _names.HasAttribute(td.GetCustomAttributes(), "System.Runtime.CompilerServices.IsReadOnlyAttribute");

    private bool IsByRefLike(TypeDefinition td)
        => _names.HasAttribute(td.GetCustomAttributes(), "System.Runtime.CompilerServices.IsByRefLikeAttribute");

    private bool IsException(TypeDefinition td)
    {
        string b = td.BaseType.IsNil ? "" : _names.EntityTypeFullName(td.BaseType);
        return b == "System.Exception" || b.EndsWith("Exception", StringComparison.Ordinal) && b.StartsWith("System.", StringComparison.Ordinal);
    }

    private bool IsEnum(TypeDefinition td)
        => !td.BaseType.IsNil && _names.EntityTypeFullName(td.BaseType) == "System.Enum";

    private bool IsDelegate(TypeDefinition td)
    {
        if (td.BaseType.IsNil) return false;
        string b = _names.EntityTypeFullName(td.BaseType);
        return b == "System.MulticastDelegate" || b == "System.Delegate";
    }

    private bool IsValueType(TypeDefinition td)
        => !td.BaseType.IsNil && _names.EntityTypeFullName(td.BaseType) == "System.ValueType";

    private static bool IsAccessorSpecialName(MethodDefinition method, string name)
    {
        if ((method.Attributes & MethodAttributes.SpecialName) == 0) return false;
        if (name.StartsWith("op_", StringComparison.Ordinal)) return false;
        return name.StartsWith("get_", StringComparison.Ordinal)
            || name.StartsWith("set_", StringComparison.Ordinal)
            || name.StartsWith("add_", StringComparison.Ordinal)
            || name.StartsWith("remove_", StringComparison.Ordinal)
            || name.StartsWith("raise_", StringComparison.Ordinal);
    }

    private static string MethodModifiers(MethodDefinition m, bool includeStatic, bool includeAbstractVirtualOverride)
    {
        var parts = new List<string>();
        if (includeStatic && (m.Attributes & MethodAttributes.Static) != 0) parts.Add("static");
        if (includeAbstractVirtualOverride)
        {
            if ((m.Attributes & MethodAttributes.Abstract) != 0) parts.Add("abstract");
            else if ((m.Attributes & MethodAttributes.Virtual) != 0 && (m.Attributes & MethodAttributes.NewSlot) == 0) parts.Add("override");
            else if ((m.Attributes & MethodAttributes.Virtual) != 0 && (m.Attributes & MethodAttributes.Final) == 0) parts.Add("virtual");
        }
        return parts.Count == 0 ? "" : string.Join(" ", parts) + " ";
    }
}
