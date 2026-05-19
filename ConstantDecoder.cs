using System.Globalization;
using System.Reflection.Metadata;
using System.Text;

namespace PublicApiExtractorV2;

internal static class ConstantDecoder
{
    public static object? Decode(MetadataReader reader, ConstantHandle handle)
    {
        if (handle.IsNil) return null;
        Constant c = reader.GetConstant(handle);
        byte[] bytes = reader.GetBlobBytes(c.Value);
        switch (c.TypeCode)
        {
            case ConstantTypeCode.NullReference: return null;
            case ConstantTypeCode.Boolean: return bytes.Length != 0 && bytes[0] != 0;
            case ConstantTypeCode.Char: return bytes.Length >= 2 ? (char)BitConverter.ToUInt16(bytes, 0) : '\0';
            case ConstantTypeCode.SByte: return bytes.Length == 0 ? (sbyte)0 : unchecked((sbyte)bytes[0]);
            case ConstantTypeCode.Byte: return bytes.Length == 0 ? (byte)0 : bytes[0];
            case ConstantTypeCode.Int16: return bytes.Length >= 2 ? BitConverter.ToInt16(bytes, 0) : (short)0;
            case ConstantTypeCode.UInt16: return bytes.Length >= 2 ? BitConverter.ToUInt16(bytes, 0) : (ushort)0;
            case ConstantTypeCode.Int32: return bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0;
            case ConstantTypeCode.UInt32: return bytes.Length >= 4 ? BitConverter.ToUInt32(bytes, 0) : 0u;
            case ConstantTypeCode.Int64: return bytes.Length >= 8 ? BitConverter.ToInt64(bytes, 0) : 0L;
            case ConstantTypeCode.UInt64: return bytes.Length >= 8 ? BitConverter.ToUInt64(bytes, 0) : 0UL;
            case ConstantTypeCode.Single: return bytes.Length >= 4 ? BitConverter.ToSingle(bytes, 0) : 0f;
            case ConstantTypeCode.Double: return bytes.Length >= 8 ? BitConverter.ToDouble(bytes, 0) : 0d;
            case ConstantTypeCode.String: return Encoding.Unicode.GetString(bytes);
            default: return "<" + c.TypeCode.ToString() + ":" + BitConverter.ToString(bytes) + ">";
        }
    }

    public static string DecodeLiteral(MetadataReader reader, ConstantHandle handle)
        => MetadataNames.Literal(Decode(reader, handle));

    public static string DecodeEnumIntegral(MetadataReader reader, ConstantHandle handle)
    {
        object? value = Decode(reader, handle);
        if (value == null) return "0";
        if (value is IConvertible c)
            return c.ToString(CultureInfo.InvariantCulture) ?? "0";
        return value.ToString() ?? "0";
    }
}
