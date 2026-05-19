using System.Reflection.Metadata;

namespace PublicApiExtractorV2;

internal sealed class SignatureTypeName
{
    private enum NodeKind
    {
        Simple,
        GenericParameter,
        GenericInstantiation,
        SZArray,
        Array,
        ByReference,
        Pointer,
        FunctionPointer,
        Modified,
        Pinned,
        NullableValue
    }

    private readonly NodeKind _kind;
    private readonly string _name;
    private readonly string _fullName;
    private readonly bool _nullableReferenceSlot;
    private readonly SignatureTypeName? _element;
    private readonly SignatureTypeName? _genericType;
    private readonly IReadOnlyList<SignatureTypeName> _arguments;
    private readonly ArrayShape _arrayShape;
    private readonly bool _requiredModifier;

    private SignatureTypeName(
        NodeKind kind,
        string name = "",
        string fullName = "",
        bool nullableReferenceSlot = false,
        SignatureTypeName? element = null,
        SignatureTypeName? genericType = null,
        IReadOnlyList<SignatureTypeName>? arguments = null,
        ArrayShape arrayShape = default,
        bool requiredModifier = false)
    {
        _kind = kind;
        _name = name;
        _fullName = fullName;
        _nullableReferenceSlot = nullableReferenceSlot;
        _element = element;
        _genericType = genericType;
        _arguments = arguments ?? System.Array.Empty<SignatureTypeName>();
        _arrayShape = arrayShape;
        _requiredModifier = requiredModifier;
    }

    public string FullName => _fullName;

    public bool IsGenericInstantiationOf(string fullName)
        => _kind == NodeKind.GenericInstantiation && string.Equals(_fullName, fullName, StringComparison.Ordinal);

    public bool CanUseReferenceNullDefault
    {
        get
        {
            switch (_kind)
            {
                case NodeKind.Simple:
                case NodeKind.GenericParameter:
                case NodeKind.GenericInstantiation:
                case NodeKind.SZArray:
                case NodeKind.Array:
                case NodeKind.FunctionPointer:
                    return _nullableReferenceSlot;
                case NodeKind.ByReference:
                case NodeKind.Modified:
                case NodeKind.Pinned:
                    return _element != null && _element.CanUseReferenceNullDefault;
                default:
                    return false;
            }
        }
    }

    public bool IsNullableValueType => _kind == NodeKind.NullableValue;

    public bool ShouldRenderNullDefaultAsDefault => !CanUseReferenceNullDefault && !IsNullableValueType;

    public int NullableSlotCount
    {
        get
        {
            switch (_kind)
            {
                case NodeKind.Simple:
                case NodeKind.GenericParameter:
                    return _nullableReferenceSlot ? 1 : 0;

                case NodeKind.GenericInstantiation:
                    {
                        int count = _nullableReferenceSlot ? 1 : 0;
                        foreach (SignatureTypeName argument in _arguments)
                            count += argument.NullableSlotCount;
                        return count;
                    }

                case NodeKind.SZArray:
                case NodeKind.Array:
                    return 1 + (_element?.NullableSlotCount ?? 0);

                case NodeKind.ByReference:
                case NodeKind.Pointer:
                case NodeKind.Modified:
                case NodeKind.Pinned:
                case NodeKind.NullableValue:
                    return _element?.NullableSlotCount ?? 0;

                case NodeKind.FunctionPointer:
                    {
                        int count = 0;
                        foreach (SignatureTypeName argument in _arguments)
                            count += argument.NullableSlotCount;
                        return count;
                    }

                default:
                    return 0;
            }
        }
    }

    public static SignatureTypeName Simple(string name, string fullName = "", bool nullableReferenceSlot = false)
        => new(NodeKind.Simple, name, fullName, nullableReferenceSlot);

    public static SignatureTypeName GenericParameter(string name)
        => new(NodeKind.GenericParameter, name, nullableReferenceSlot: true);

    public static SignatureTypeName GenericInstantiation(SignatureTypeName genericType, IReadOnlyList<SignatureTypeName> arguments)
    {
        if (genericType.FullName == "System.Nullable" && arguments.Count == 1)
            return new SignatureTypeName(NodeKind.NullableValue, element: arguments[0]);

        return new SignatureTypeName(
            NodeKind.GenericInstantiation,
            name: StripArity(genericType.UnannotatedName()),
            fullName: StripArity(genericType.FullName),
            nullableReferenceSlot: genericType._nullableReferenceSlot,
            genericType: genericType,
            arguments: arguments);
    }

    public static SignatureTypeName SZArray(SignatureTypeName element)
        => new(NodeKind.SZArray, element: element, nullableReferenceSlot: true);

    public static SignatureTypeName Array(SignatureTypeName element, ArrayShape shape)
        => new(NodeKind.Array, element: element, arrayShape: shape, nullableReferenceSlot: true);

    public static SignatureTypeName ByReference(SignatureTypeName element)
        => new(NodeKind.ByReference, element: element);

    public static SignatureTypeName Pointer(SignatureTypeName element)
        => new(NodeKind.Pointer, element: element);

    public static SignatureTypeName FunctionPointer(IReadOnlyList<SignatureTypeName> parameters, SignatureTypeName returnType)
    {
        var args = new List<SignatureTypeName>(parameters.Count + 1);
        args.AddRange(parameters);
        args.Add(returnType);
        return new SignatureTypeName(NodeKind.FunctionPointer, arguments: args);
    }

    public static SignatureTypeName Modified(SignatureTypeName modifier, SignatureTypeName unmodifiedType, bool required)
        => new(NodeKind.Modified, element: unmodifiedType, genericType: modifier, requiredModifier: required);

    public static SignatureTypeName Pinned(SignatureTypeName element)
        => new(NodeKind.Pinned, element: element);

    public string Render(byte[]? nullableFlags = null, byte? nullableContext = null, IReadOnlyList<string?>? tupleElementNames = null)
    {
        var nullableState = new NullableRenderState(nullableFlags, nullableContext);
        TupleNameRenderState? tupleState = tupleElementNames == null ? null : new TupleNameRenderState(tupleElementNames);
        return Render(nullableState, tupleState);
    }

    public override string ToString() => Render();

    private string Render(NullableRenderState nullableState, TupleNameRenderState? tupleState)
    {
        switch (_kind)
        {
            case NodeKind.Simple:
            case NodeKind.GenericParameter:
                return ApplyNullable(UnannotatedName(), nullableState);

            case NodeKind.NullableValue:
                return _element!.Render(nullableState, tupleState) + "?";

            case NodeKind.GenericInstantiation:
                {
                    if (IsValueTupleSyntax())
                    {
                        byte? tupleAnnotation = _nullableReferenceSlot ? nullableState.Next() : null;
                        var parts = new List<string>();
                        AppendTupleElementTexts(parts, nullableState, tupleState);
                        string tuple = "(" + string.Join(",", parts) + ")";
                        return tupleAnnotation == 2 ? tuple + "?" : tuple;
                    }

                    ConsumeTupleContainerPlaceholderIfPresent(tupleState);
                    byte? annotation = _nullableReferenceSlot ? nullableState.Next() : null;
                    var renderedArgs = new List<string>(_arguments.Count);
                    foreach (SignatureTypeName arg in _arguments)
                        renderedArgs.Add(arg.Render(nullableState, tupleState));
                    string text = StripArity(_name) + "<" + string.Join(",", renderedArgs) + ">";
                    return annotation == 2 ? text + "?" : text;
                }

            case NodeKind.SZArray:
                {
                    ConsumeTupleContainerPlaceholderIfPresent(tupleState);
                    byte? annotation = nullableState.Next();
                    string text = _element!.Render(nullableState, tupleState) + "[]";
                    return annotation == 2 ? text + "?" : text;
                }

            case NodeKind.Array:
                {
                    ConsumeTupleContainerPlaceholderIfPresent(tupleState);
                    byte? annotation = nullableState.Next();
                    string text = _element!.Render(nullableState, tupleState) + ArraySuffix(_arrayShape);
                    return annotation == 2 ? text + "?" : text;
                }

            case NodeKind.ByReference:
                return _element!.Render(nullableState, tupleState) + "&";

            case NodeKind.Pointer:
                return _element!.Render(nullableState, tupleState) + "*";

            case NodeKind.FunctionPointer:
                {
                    ConsumeTupleContainerPlaceholderIfPresent(tupleState);
                    if (_arguments.Count == 0) return "delegate*<void>";
                    var parts = new List<string>(_arguments.Count);
                    for (int i = 0; i < _arguments.Count - 1; i++)
                        parts.Add(_arguments[i].Render(nullableState, tupleState));
                    parts.Add(_arguments[_arguments.Count - 1].Render(nullableState, tupleState));
                    return "delegate*<" + string.Join(",", parts) + ">";
                }

            case NodeKind.Modified:
                return (_requiredModifier ? "modreq(" : "modopt(") + _genericType!.UnannotatedName() + ") " + _element!.Render(nullableState, tupleState);

            case NodeKind.Pinned:
                return "pinned " + _element!.Render(nullableState, tupleState);

            default:
                return _name;
        }
    }

    private void AppendTupleElementTexts(List<string> parts, NullableRenderState nullableState, TupleNameRenderState? tupleState)
    {
        int count = _arguments.Count;
        int ordinaryCount = count == 8 && _arguments[7].IsValueTupleType() ? 7 : count;
        for (int i = 0; i < ordinaryCount; i++)
        {
            string? elementName = tupleState?.Next();
            string text = _arguments[i].Render(nullableState, tupleState);
            if (!string.IsNullOrEmpty(elementName))
                text += " " + MetadataNames.Identifier(elementName!);
            parts.Add(text);
        }

        if (count == 8 && _arguments[7].IsValueTupleType())
            _arguments[7].AppendTupleElementTexts(parts, nullableState, tupleState);
    }

    private void ConsumeTupleContainerPlaceholderIfPresent(TupleNameRenderState? tupleState)
    {
        if (tupleState != null && ContainsValueTuple() && tupleState.HasRemaining && tupleState.Peek() == null)
            tupleState.Next();
    }

    private bool ContainsValueTuple()
    {
        if (IsValueTupleType()) return true;
        if (_element != null && _element.ContainsValueTuple()) return true;
        if (_genericType != null && _genericType.ContainsValueTuple()) return true;
        foreach (SignatureTypeName arg in _arguments)
            if (arg.ContainsValueTuple()) return true;
        return false;
    }

    private bool IsValueTupleSyntax()
        => _kind == NodeKind.GenericInstantiation
           && _fullName == "System.ValueTuple"
           && (_arguments.Count >= 2 && _arguments.Count <= 7
               || _arguments.Count == 8 && _arguments[7].IsValueTupleType());

    private bool IsValueTupleType()
        => _kind == NodeKind.GenericInstantiation && _fullName == "System.ValueTuple" && _arguments.Count >= 1 && _arguments.Count <= 8;

    private string ApplyNullable(string text, NullableRenderState state)
    {
        if (!_nullableReferenceSlot)
            return text;

        byte? annotation = state.Next();
        return annotation == 2 ? text + "?" : text;
    }

    private string UnannotatedName() => _kind == NodeKind.GenericInstantiation ? StripArity(_name) : _name;

    private static string ArraySuffix(ArrayShape shape)
    {
        if (shape.Rank <= 1) return "[*]";
        return "[" + new string(',', shape.Rank - 1) + "]";
    }

    private static string StripArity(string name)
    {
        int tick = name.IndexOf('`');
        return tick >= 0 ? name.Substring(0, tick) : name;
    }

    private sealed class NullableRenderState
    {
        private readonly byte[]? _flags;
        private readonly byte? _context;
        private int _index;

        public NullableRenderState(byte[]? flags, byte? context)
        {
            _flags = flags;
            _context = context;
        }

        public byte? Next()
        {
            if (_flags != null)
            {
                // NullableAttribute(byte) is a subtree-wide default, not just the
                // first/top-level nullable slot. This matters for signatures like
                // [return: Nullable(1)] Task<HttpResponseMessage> on a method whose
                // parameters inherit a nullable method context.
                if (_flags.Length == 1)
                    return _flags[0];

                if (_index < _flags.Length)
                    return _flags[_index++];
            }

            return _context;
        }
    }

    private sealed class TupleNameRenderState
    {
        private readonly IReadOnlyList<string?> _names;
        private int _index;

        public TupleNameRenderState(IReadOnlyList<string?> names)
        {
            _names = names;
        }

        public bool HasRemaining => _index < _names.Count;

        public string? Peek() => _index < _names.Count ? _names[_index] : null;

        public string? Next()
        {
            if (_index >= _names.Count) return null;
            return _names[_index++];
        }
    }
}
