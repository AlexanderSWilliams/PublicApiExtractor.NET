using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;

namespace PublicApiExtractorV2;

internal sealed class MetadataNamePolicy
{
    private readonly HashSet<string> _namespacesUsed;
    private readonly Dictionary<string, HashSet<string>> _fullNamesByLeafKey;
    private readonly HashSet<TypeReferenceHandle> _usedTypeReferences;
    private readonly HashSet<AssemblyReferenceHandle> _usedAssemblyReferences;

    private MetadataNamePolicy(
        HashSet<string> namespacesUsed,
        Dictionary<string, HashSet<string>> fullNamesByLeafKey,
        HashSet<TypeReferenceHandle> usedTypeReferences,
        HashSet<AssemblyReferenceHandle> usedAssemblyReferences)
    {
        _namespacesUsed = namespacesUsed;
        _fullNamesByLeafKey = fullNamesByLeafKey;
        _usedTypeReferences = usedTypeReferences;
        _usedAssemblyReferences = usedAssemblyReferences;
    }

    public IReadOnlyList<string> ImportedNamespaces => _namespacesUsed.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    public IReadOnlyList<TypeReferenceHandle> UsedTypeReferences => _usedTypeReferences.ToArray();
    public IReadOnlyList<AssemblyReferenceHandle> UsedAssemblyReferences => _usedAssemblyReferences.ToArray();

    public static MetadataNamePolicy Build(MetadataReader reader)
    {
        var builder = new PublicSignatureReferenceBuilder(reader);
        builder.Collect();
        return new MetadataNamePolicy(builder.NamespacesUsed, builder.FullNamesByLeafKey, builder.UsedTypeReferences, builder.UsedAssemblyReferences);
    }

    public string Format(string ns, string nestedName, string currentNamespace, bool includeNamespace, string leafAmbiguityKey)
    {
        if (!includeNamespace || ns.Length == 0 || string.Equals(ns, currentNamespace, StringComparison.Ordinal))
            return nestedName;

        string key = string.IsNullOrEmpty(leafAmbiguityKey) ? LastNestedNameKey(nestedName) : leafAmbiguityKey;
        bool ambiguous = _fullNamesByLeafKey.TryGetValue(key, out HashSet<string>? fullNames) && fullNames.Count > 1;
        if (!ambiguous && _namespacesUsed.Contains(ns))
            return nestedName;

        return ns + "." + nestedName;
    }

    private static string LastNestedNameKey(string nestedName)
    {
        int dot = nestedName.LastIndexOf('.');
        string leaf = dot >= 0 ? nestedName.Substring(dot + 1) : nestedName;
        if (leaf.Length >= 2 && leaf[0] == '`' && leaf[leaf.Length - 1] == '`') return leaf;
        if (leaf.Length > 1 && leaf[0] == '@') leaf = leaf.Substring(1);
        int generic = leaf.IndexOf('<');
        if (generic < 0) return leaf;

        int arity = 1;
        for (int i = generic + 1; i < leaf.Length; i++)
            if (leaf[i] == ',') arity++;
        return leaf.Substring(0, generic) + "`" + arity.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class PublicSignatureReferenceBuilder
    {
        private readonly MetadataReader _reader;
        private readonly SignatureReferenceCollector _collector;

        public PublicSignatureReferenceBuilder(MetadataReader reader)
        {
            _reader = reader;
            _collector = new SignatureReferenceCollector(this);
        }

        public HashSet<string> NamespacesUsed { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> FullNamesByLeafKey { get; } = new(StringComparer.Ordinal);
        public HashSet<TypeReferenceHandle> UsedTypeReferences { get; } = new();
        public HashSet<AssemblyReferenceHandle> UsedAssemblyReferences { get; } = new();

        public void Collect()
        {
            foreach (TypeDefinitionHandle handle in _reader.TypeDefinitions)
            {
                if (!IsReachablePublicType(handle)) continue;
                TypeDefinition td = _reader.GetTypeDefinition(handle);
                if (HasAttribute(td.GetCustomAttributes(), "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) continue;
                AddTypeDefinitionName(handle);
                CollectType(td);
            }
        }

        private void CollectType(TypeDefinition td)
        {
            AddAttributes(td.GetCustomAttributes());
            if (!td.BaseType.IsNil) AddEntityType(td.BaseType);
            foreach (InterfaceImplementationHandle ih in td.GetInterfaceImplementations())
            {
                InterfaceImplementation impl = _reader.GetInterfaceImplementation(ih);
                AddEntityType(impl.Interface);
                AddAttributes(impl.GetCustomAttributes());
            }
            CollectGenericParameters(td.GetGenericParameters());

            foreach (FieldDefinitionHandle fh in td.GetFields())
            {
                FieldDefinition f = _reader.GetFieldDefinition(fh);
                string name = ReadString(f.Name);
                if (!VisibilityPolicy.IsVisibleField(f.Attributes)) continue;
                if ((f.Attributes & FieldAttributes.SpecialName) != 0 && name == "value__") continue;
                if (HasAttribute(f.GetCustomAttributes(), "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) continue;
                f.DecodeSignature(_collector, null);
                AddAttributes(f.GetCustomAttributes());
            }

            foreach (MethodDefinitionHandle mh in td.GetMethods())
            {
                MethodDefinition m = _reader.GetMethodDefinition(mh);
                string name = ReadString(m.Name);
                if (name == ".cctor") continue;
                if (!VisibilityPolicy.IsVisibleMethod(m.Attributes)) continue;
                if (HasAttribute(m.GetCustomAttributes(), "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) continue;
                if (IsAccessorSpecialName(m, name)) continue;
                m.DecodeSignature(_collector, null);
                AddAttributes(m.GetCustomAttributes());
                CollectGenericParameters(m.GetGenericParameters());
                AddParameterAttributes(m.GetParameters());
            }

            foreach (PropertyDefinitionHandle ph in td.GetProperties())
            {
                PropertyDefinition p = _reader.GetPropertyDefinition(ph);
                if (HasAttribute(p.GetCustomAttributes(), "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) continue;
                PropertyAccessors accessors = p.GetAccessors();
                MethodDefinition? getter = accessors.Getter.IsNil ? null : _reader.GetMethodDefinition(accessors.Getter);
                MethodDefinition? setter = accessors.Setter.IsNil ? null : _reader.GetMethodDefinition(accessors.Setter);
                if (!HasVisibleAccessor(getter, setter)) continue;
                p.DecodeSignature(_collector, null);
                AddAttributes(p.GetCustomAttributes());
                if (getter.HasValue) AddParameterAttributes(getter.Value.GetParameters());
                if (setter.HasValue) AddParameterAttributes(setter.Value.GetParameters());
            }

            foreach (EventDefinitionHandle eh in td.GetEvents())
            {
                EventDefinition e = _reader.GetEventDefinition(eh);
                if (HasAttribute(e.GetCustomAttributes(), "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) continue;
                EventAccessors accessors = e.GetAccessors();
                if (accessors.Adder.IsNil) continue;
                MethodDefinition add = _reader.GetMethodDefinition(accessors.Adder);
                if (!VisibilityPolicy.IsVisibleMethod(add.Attributes)) continue;
                AddEntityType(e.Type);
                AddAttributes(e.GetCustomAttributes());
            }
        }

        private static bool HasVisibleAccessor(MethodDefinition? getter, MethodDefinition? setter)
            => getter.HasValue && VisibilityPolicy.IsVisibleMethod(getter.Value.Attributes)
               || setter.HasValue && VisibilityPolicy.IsVisibleMethod(setter.Value.Attributes);

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

        private void CollectGenericParameters(GenericParameterHandleCollection handles)
        {
            foreach (GenericParameterHandle h in handles)
            {
                GenericParameter gp = _reader.GetGenericParameter(h);
                AddAttributes(gp.GetCustomAttributes());
                foreach (GenericParameterConstraintHandle ch in gp.GetConstraints())
                {
                    GenericParameterConstraint c = _reader.GetGenericParameterConstraint(ch);
                    AddEntityType(c.Type);
                    AddAttributes(c.GetCustomAttributes());
                }
            }
        }

        private void AddParameterAttributes(ParameterHandleCollection handles)
        {
            foreach (ParameterHandle ph in handles)
            {
                if (ph.IsNil) continue;
                AddAttributes(_reader.GetParameter(ph).GetCustomAttributes());
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

        public void AddEntityType(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    AddTypeDefinitionName((TypeDefinitionHandle)handle);
                    break;
                case HandleKind.TypeReference:
                    AddTypeReference((TypeReferenceHandle)handle);
                    break;
                case HandleKind.TypeSpecification:
                    _reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(_collector, null);
                    break;
            }
        }

        public void AddTypeDefinitionName(TypeDefinitionHandle handle)
        {
            string ns = TypeDefinitionNamespace(handle);
            string nested = TypeDefinitionNestedName(handle);
            string leaf = ReadString(_reader.GetTypeDefinition(handle).Name);
            AddName(ns, nested, leaf);
        }

        public void AddTypeReference(TypeReferenceHandle handle)
        {
            if (!UsedTypeReferences.Add(handle))
                return;

            TypeReference tr = _reader.GetTypeReference(handle);
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                AddTypeReference((TypeReferenceHandle)tr.ResolutionScope);
            else if (tr.ResolutionScope.Kind == HandleKind.AssemblyReference)
                UsedAssemblyReferences.Add((AssemblyReferenceHandle)tr.ResolutionScope);

            string ns = TypeReferenceNamespace(handle);
            string nested = TypeReferenceNestedName(handle);
            string leaf = ReadString(tr.Name);
            AddName(ns, nested, leaf);
        }

        private void AddAttributes(CustomAttributeHandleCollection attrs)
        {
            foreach (CustomAttributeHandle h in attrs)
            {
                CustomAttribute attr = _reader.GetCustomAttribute(h);
                string fullName = AttributeTypeFullName(attr);
                if (!MetadataNames.IsEmittedSemanticAttributeFullName(fullName))
                    continue;
                if (IsSuppressedPrivateTargetMemberNullabilityAttribute(fullName, attr))
                    continue;

                EntityHandle type = AttributeTypeHandle(attr);
                if (!type.IsNil) AddEntityType(type);
            }
        }

        private bool HasAttribute(CustomAttributeHandleCollection attrs, string fullName)
        {
            foreach (CustomAttributeHandle h in attrs)
                if (AttributeTypeFullName(_reader.GetCustomAttribute(h)) == fullName) return true;
            return false;
        }

        private bool IsSuppressedPrivateTargetMemberNullabilityAttribute(string fullName, CustomAttribute attr)
        {
            if (fullName != "System.Diagnostics.CodeAnalysis.MemberNotNullAttribute"
                && fullName != "System.Diagnostics.CodeAnalysis.MemberNotNullWhenAttribute")
                return false;

            try
            {
                BlobReader reader = _reader.GetBlobReader(attr.Value);
                if (reader.Length < 2 || reader.ReadUInt16() != 1) return false;
                if (fullName == "System.Diagnostics.CodeAnalysis.MemberNotNullWhenAttribute")
                    reader.ReadBoolean();

                BlobReader arrayReader = reader;
                if (TryReadPrivateTargetArray(ref arrayReader))
                    return true;

                BlobReader stringReader = reader;
                return TryReadPrivateTargetString(ref stringReader);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadPrivateTargetArray(ref BlobReader reader)
        {
            if (reader.RemainingBytes < 4) return false;
            int count = reader.ReadInt32();
            if (count < 0 || count > reader.RemainingBytes) return false;
            for (int i = 0; i < count; i++)
            {
                string? target = reader.ReadSerializedString();
                if (IsPrivateMemberTargetName(target)) return true;
            }
            return false;
        }

        private static bool TryReadPrivateTargetString(ref BlobReader reader)
        {
            if (reader.RemainingBytes <= 0) return false;
            string? target = reader.ReadSerializedString();
            return IsPrivateMemberTargetName(target);
        }

        private static bool IsPrivateMemberTargetName(string? name)
            => !string.IsNullOrEmpty(name)
               && (name.StartsWith("_", StringComparison.Ordinal)
                   || name.StartsWith("m_", StringComparison.Ordinal)
                   || name.Contains(".<", StringComparison.Ordinal));

        private EntityHandle AttributeTypeHandle(CustomAttribute attribute)
        {
            EntityHandle ctor = attribute.Constructor;
            if (ctor.Kind == HandleKind.MemberReference)
                return _reader.GetMemberReference((MemberReferenceHandle)ctor).Parent;
            if (ctor.Kind == HandleKind.MethodDefinition)
                return _reader.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType();
            return default;
        }

        private string AttributeTypeFullName(CustomAttribute attribute)
        {
            EntityHandle parent = AttributeTypeHandle(attribute);
            if (parent.Kind == HandleKind.TypeDefinition)
                return TypeDefinitionFullName((TypeDefinitionHandle)parent);
            if (parent.Kind == HandleKind.TypeReference)
                return TypeReferenceFullName((TypeReferenceHandle)parent);
            return "<attribute>";
        }

        private void AddName(string ns, string nestedName, string leafKey)
        {
            if (ns.Length != 0) NamespacesUsed.Add(ns);
            string fullKey = ns.Length == 0 ? nestedName : ns + "." + nestedName;
            if (leafKey.Length == 0 || fullKey.Length == 0) return;
            if (!FullNamesByLeafKey.TryGetValue(leafKey, out HashSet<string>? values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                FullNamesByLeafKey.Add(leafKey, values);
            }
            values.Add(fullKey);
        }

        private string TypeDefinitionNamespace(TypeDefinitionHandle handle)
        {
            TypeDefinition td = _reader.GetTypeDefinition(handle);
            if (!td.GetDeclaringType().IsNil)
                return TypeDefinitionNamespace(td.GetDeclaringType());
            return ReadString(td.Namespace);
        }

        private string TypeDefinitionNestedName(TypeDefinitionHandle handle)
        {
            TypeDefinition td = _reader.GetTypeDefinition(handle);
            string simple = ReadString(td.Name);
            TypeDefinitionHandle declaring = td.GetDeclaringType();
            if (!declaring.IsNil)
                return TypeDefinitionNestedName(declaring) + "." + simple;
            return simple;
        }

        private string TypeDefinitionFullName(TypeDefinitionHandle handle)
        {
            string ns = TypeDefinitionNamespace(handle);
            string nested = TypeDefinitionNestedName(handle);
            return ns.Length == 0 ? nested : ns + "." + nested;
        }

        private string TypeReferenceNamespace(TypeReferenceHandle handle)
        {
            TypeReference tr = _reader.GetTypeReference(handle);
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                return TypeReferenceNamespace((TypeReferenceHandle)tr.ResolutionScope);
            return ReadString(tr.Namespace);
        }

        private string TypeReferenceNestedName(TypeReferenceHandle handle)
        {
            TypeReference tr = _reader.GetTypeReference(handle);
            string simple = ReadString(tr.Name);
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                return TypeReferenceNestedName((TypeReferenceHandle)tr.ResolutionScope) + "." + simple;
            return simple;
        }

        private string TypeReferenceFullName(TypeReferenceHandle handle)
        {
            string ns = TypeReferenceNamespace(handle);
            string nested = TypeReferenceNestedName(handle);
            return ns.Length == 0 ? nested : ns + "." + nested;
        }

        private string ReadString(StringHandle handle) => handle.IsNil ? "" : _reader.GetString(handle);
    }

    private sealed class SignatureReferenceCollector : ISignatureTypeProvider<object?, object?>
    {
        private readonly PublicSignatureReferenceBuilder _builder;

        public SignatureReferenceCollector(PublicSignatureReferenceBuilder builder)
        {
            _builder = builder;
        }

        public object? GetArrayType(object? elementType, ArrayShape shape) => null;
        public object? GetByReferenceType(object? elementType) => null;
        public object? GetFunctionPointerType(MethodSignature<object?> signature) => null;
        public object? GetGenericInstantiation(object? genericType, ImmutableArray<object?> typeArguments) => null;
        public object? GetGenericMethodParameter(object? genericContext, int index) => null;
        public object? GetGenericTypeParameter(object? genericContext, int index) => null;
        public object? GetModifiedType(object? modifier, object? unmodifiedType, bool isRequired) => null;
        public object? GetPinnedType(object? elementType) => null;
        public object? GetPointerType(object? elementType) => null;
        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
        public object? GetSZArrayType(object? elementType) => null;

        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            _builder.AddTypeDefinitionName(handle);
            return null;
        }

        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            _builder.AddTypeReference(handle);
            return null;
        }

        public object? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
            return null;
        }
    }
}
