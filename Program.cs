using PublicApiExtractorV2;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: PublicApiExtractorV2 <managed-pe-file>");
    return 2;
}

try
{
    Console.Write(PublicApiExtractor.ExtractPublicApiText(args[0]));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);
    return 1;
}
