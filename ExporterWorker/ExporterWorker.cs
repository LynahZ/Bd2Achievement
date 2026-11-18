internal static class ExporterWorker
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        return args is ["--export", .. var options] ? TableExportEntry.Main(options) : 2;
    }
}
