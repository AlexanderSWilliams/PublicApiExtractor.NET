using System.Reflection;

namespace PublicApiExtractorV2;

internal static class VisibilityPolicy
{
    public static bool IsVisibleType(TypeAttributes attrs)
    {
        TypeAttributes v = attrs & TypeAttributes.VisibilityMask;
        return v == TypeAttributes.Public
            || v == TypeAttributes.NestedPublic
            || v == TypeAttributes.NestedFamily
            || v == TypeAttributes.NestedFamORAssem;
    }

    public static bool IsTopLevelPublic(TypeAttributes attrs)
        => (attrs & TypeAttributes.VisibilityMask) == TypeAttributes.Public;

    public static bool IsNestedExternallyVisible(TypeAttributes attrs)
    {
        TypeAttributes v = attrs & TypeAttributes.VisibilityMask;
        return v == TypeAttributes.NestedPublic
            || v == TypeAttributes.NestedFamily
            || v == TypeAttributes.NestedFamORAssem;
    }

    public static bool IsVisibleMethod(MethodAttributes attrs)
    {
        MethodAttributes v = attrs & MethodAttributes.MemberAccessMask;
        return v == MethodAttributes.Public
            || v == MethodAttributes.Family
            || v == MethodAttributes.FamORAssem;
    }

    public static bool IsVisibleField(FieldAttributes attrs)
    {
        FieldAttributes v = attrs & FieldAttributes.FieldAccessMask;
        return v == FieldAttributes.Public
            || v == FieldAttributes.Family
            || v == FieldAttributes.FamORAssem;
    }

    public static string MethodVisibility(MethodAttributes attrs)
    {
        MethodAttributes v = attrs & MethodAttributes.MemberAccessMask;
        if (v == MethodAttributes.Family) return "protected ";
        if (v == MethodAttributes.FamORAssem) return "protected-internal ";
        return "";
    }

    public static string FieldVisibility(FieldAttributes attrs)
    {
        FieldAttributes v = attrs & FieldAttributes.FieldAccessMask;
        if (v == FieldAttributes.Family) return "protected ";
        if (v == FieldAttributes.FamORAssem) return "protected-internal ";
        return "";
    }

    public static string TypeVisibility(TypeAttributes attrs)
    {
        TypeAttributes v = attrs & TypeAttributes.VisibilityMask;
        if (v == TypeAttributes.NestedFamily) return "protected ";
        if (v == TypeAttributes.NestedFamORAssem) return "protected-internal ";
        return "";
    }
}
