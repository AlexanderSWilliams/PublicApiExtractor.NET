using System.Text;

namespace PublicApiExtractorV2;

internal static class CanonicalApiWriter
{
    public static string Write(ApiAssembly assembly)
    {
        Dictionary<string, string> attributeAliases = BuildAttributeAliases(assembly);

        var sb = new StringBuilder();
        sb.AppendLine("# K:T=type C=ctor M=method P=prop F=field V=event E=enum-member X=type-forward");
        sb.AppendLine("# visibility omitted means public; @ sets namespace until next @");
        sb.AppendLine("# repeated long attributes may be emitted as [ATn] aliases declared by # attr lines");
        if (!string.IsNullOrEmpty(assembly.AssemblyName)) sb.AppendLine("# assembly " + assembly.AssemblyName);
        if (!string.IsNullOrEmpty(assembly.ModuleName)) sb.AppendLine("# module " + assembly.ModuleName);
        if (assembly.NamespacesUsed.Count != 0) sb.AppendLine("# namespaces-used " + string.Join(" ", assembly.NamespacesUsed.OrderBy(x => x, StringComparer.Ordinal)));
        foreach (string line in assembly.AssemblyReferenceLines)
            sb.AppendLine("# aref " + line);
        foreach (string line in assembly.TypeReferenceLines)
            sb.AppendLine("# tref " + line);
        foreach (KeyValuePair<string, string> alias in attributeAliases.OrderBy(x => AttributeAliasIndex(x.Value)).ThenBy(x => x.Value, StringComparer.Ordinal))
            sb.AppendLine("# attr " + alias.Value + " " + alias.Key);
        foreach (string line in assembly.AttributeAliasLines)
            sb.AppendLine("# attr " + line);
        if (assembly.Types.Count == 0 && assembly.TypeForwards.Count == 0)
        {
            sb.AppendLine("# metadata type-definitions=" + assembly.MetadataTypeDefinitionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " exported-types=" + assembly.MetadataExportedTypeCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " public-type-definitions=" + assembly.PublicTypeDefinitionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " public-exported-types=" + assembly.PublicExportedTypeCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (assembly.MetadataExportedTypeCount != 0)
                sb.AppendLine("# exported-types-present emitted-type-forwarders=0");
            else
                sb.AppendLine("# no-public-api");
        }

        AppendTopLevelLines(sb, assembly.TypeForwards
            .OrderBy(ForwarderBaseName, StringComparer.Ordinal)
            .ThenBy(ForwarderArity)
            .ThenBy(ForwarderTypeName, StringComparer.Ordinal)
            .ThenBy(x => x, StringComparer.Ordinal), attributeAliases);

        string? currentNamespace = null;
        foreach (ApiType type in assembly.Types.OrderBy(t => t.Namespace, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            string ns = type.Namespace.Length == 0 ? "<global>" : type.Namespace;
            if (!string.Equals(currentNamespace, ns, StringComparison.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine("@ " + ns);
                currentNamespace = ns;
            }

            sb.AppendLine();
            sb.AppendLine("T " + ApplyAttributeAliases(type.Declaration, attributeAliases));
            AppendLines(sb, type.Constructors, attributeAliases);
            AppendLines(sb, type.Fields, attributeAliases);
            AppendLines(sb, type.Properties, attributeAliases);
            AppendLines(sb, type.Events, attributeAliases);
            AppendLines(sb, type.Methods, attributeAliases);
            AppendLines(sb, type.EnumMembers, attributeAliases);
        }

        return sb.ToString();
    }


    private static string ForwarderTypeName(string line)
    {
        int firstSpace = line.IndexOf(' ');
        if (firstSpace < 0) return line;
        int secondSpace = line.IndexOf(' ', firstSpace + 1);
        if (secondSpace < 0) return line;
        int arrow = line.IndexOf(" -> ", secondSpace + 1, StringComparison.Ordinal);
        if (arrow < 0) return line.Substring(secondSpace + 1);
        return line.Substring(secondSpace + 1, arrow - secondSpace - 1);
    }

    private static string ForwarderBaseName(string line)
    {
        string typeName = ForwarderTypeName(line);
        int genericStart = typeName.IndexOf('<');
        return genericStart < 0 ? typeName : typeName.Substring(0, genericStart);
    }

    private static int ForwarderArity(string line)
    {
        string typeName = ForwarderTypeName(line);
        int start = typeName.IndexOf('<');
        if (start < 0) return 0;

        int depth = 0;
        int arity = 1;
        for (int i = start; i < typeName.Length; i++)
        {
            char ch = typeName[i];
            if (ch == '<') depth++;
            else if (ch == '>')
            {
                depth--;
                if (depth == 0) break;
            }
            else if (ch == ',' && depth == 1)
            {
                arity++;
            }
        }

        return arity;
    }


    private static void AppendTopLevelLines(StringBuilder sb, IEnumerable<string> lines, IReadOnlyDictionary<string, string> aliases)
    {
        foreach (string line in lines)
            sb.AppendLine(ApplyAttributeAliases(line, aliases));
    }

    private static void AppendLines(StringBuilder sb, IEnumerable<string> lines, IReadOnlyDictionary<string, string> aliases)
    {
        foreach (string line in lines)
            sb.AppendLine(" " + ApplyAttributeAliases(line, aliases));
    }

    private static Dictionary<string, string> BuildAttributeAliases(ApiAssembly assembly)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in AllBodyLines(assembly))
        {
            foreach (string token in AttributeTokens(line))
            {
                if (!ShouldAliasAttribute(token)) continue;
                counts.TryGetValue(token, out int count);
                counts[token] = count + 1;
            }
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        int index = 0;
        foreach (string token in counts.Where(x => x.Value >= 3).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal))
        {
            aliases[token] = "AT" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            index++;
        }
        return aliases;
    }

    private static IEnumerable<string> AllBodyLines(ApiAssembly assembly)
    {
        foreach (string line in assembly.TypeForwards) yield return line;
        foreach (ApiType type in assembly.Types)
        {
            yield return type.Declaration;
            foreach (string line in type.Constructors) yield return line;
            foreach (string line in type.Fields) yield return line;
            foreach (string line in type.Properties) yield return line;
            foreach (string line in type.Events) yield return line;
            foreach (string line in type.Methods) yield return line;
            foreach (string line in type.EnumMembers) yield return line;
        }
    }

    private static bool ShouldAliasAttribute(string token)
    {
        if (token.Length < 48) return false;
        if (token == "[Flags]" || token == "[Serializable]") return false;
        if (token.StartsWith("[get:", StringComparison.Ordinal) || token.StartsWith("[set:", StringComparison.Ordinal)) return false;
        return true;
    }


    private static int AttributeAliasIndex(string value)
    {
        if (!value.StartsWith("AT", StringComparison.Ordinal))
            return int.MaxValue;

        if (int.TryParse(value.Substring(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
            return index;

        return int.MaxValue;
    }

    private static string ApplyAttributeAliases(string line, IReadOnlyDictionary<string, string> aliases)
    {
        foreach (KeyValuePair<string, string> alias in aliases)
            line = line.Replace(alias.Key, "[" + alias.Value + "]", StringComparison.Ordinal);
        return line;
    }

    private static IEnumerable<string> AttributeTokens(string line)
    {
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] != '[')
            {
                i++;
                continue;
            }

            int start = i;
            i++;
            bool inString = false;
            bool escape = false;
            while (i < line.Length)
            {
                char ch = line[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (ch == '\\') escape = true;
                    else if (ch == '"') inString = false;
                }
                else
                {
                    if (ch == '"') inString = true;
                    else if (ch == ']')
                    {
                        yield return line.Substring(start, i - start + 1);
                        i++;
                        break;
                    }
                }
                i++;
            }
        }
    }
}
