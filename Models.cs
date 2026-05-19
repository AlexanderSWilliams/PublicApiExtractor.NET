namespace PublicApiExtractorV2;

internal sealed class ApiAssembly
{
    public string AssemblyName { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public List<string> NamespacesUsed { get; } = new();
    public List<string> AssemblyReferenceLines { get; } = new();
    public List<string> TypeReferenceLines { get; } = new();
    public List<string> AttributeAliasLines { get; } = new();
    public List<string> TypeForwards { get; } = new();
    public List<ApiType> Types { get; } = new();
    public int MetadataTypeDefinitionCount { get; set; }
    public int MetadataExportedTypeCount { get; set; }
    public int PublicTypeDefinitionCount { get; set; }
    public int PublicExportedTypeCount { get; set; }
}

internal sealed class ApiType
{
    public string Namespace { get; set; } = "";
    public string Name { get; set; } = "";
    public string Declaration { get; set; } = "";
    public List<string> Constructors { get; } = new();
    public List<string> Fields { get; } = new();
    public List<string> Properties { get; } = new();
    public List<string> Methods { get; } = new();
    public List<string> Events { get; } = new();
    public List<string> EnumMembers { get; } = new();
}

internal readonly struct GenericContext
{
    public GenericContext(IReadOnlyList<string> typeParameters, IReadOnlyList<string> methodParameters)
    {
        TypeParameters = typeParameters;
        MethodParameters = methodParameters;
    }

    public IReadOnlyList<string> TypeParameters { get; }
    public IReadOnlyList<string> MethodParameters { get; }

    public string TypeParameter(int index) => index >= 0 && index < TypeParameters.Count ? TypeParameters[index] : "!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string MethodParameter(int index) => index >= 0 && index < MethodParameters.Count ? MethodParameters[index] : "!!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
