#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Nodes;

// Embedded Dustweave TableExporter at 3584daf8bebd42cd5f3f4bfcad164d246dc0846e.
// https://cnb.cool/dustweave/Dustweave
/*
MIT License

Copyright (c) 2026 MadestSamurai

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public static class TableExportEntry
{
    private static readonly string[] DefaultTableNames =
    {
        "CostumeTable",
        "SkillTable",
        "BuffTable",
        "CostumeNodeTable",
    };

    private static readonly IReadOnlyDictionary<string, string> TableMessageTypeAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SkillTextTable_CN"] = "SkillTextTable",
            ["SkillTextTable_TW"] = "SkillTextTable",
            ["SkillTextTable_EN"] = "SkillTextTable",
            ["SkillTextTable_JP"] = "SkillTextTable",
        };

    public static int Main(string[] args)
    {
        try
        {
            var gameRoot = GetOption(args, "--game-root") ?? FindDefaultGameRoot();
            var protobufInput = GetOption(args, "--decode-protobuf");
            if (protobufInput is not null)
            {
                var messageType = GetOption(args, "--message-type")
                    ?? throw new ArgumentException(
                        "--decode-protobuf requires --message-type.");
                var outputPath = GetOption(args, "--output")
                    ?? throw new ArgumentException(
                        "--decode-protobuf requires --output.");
                DecodeProtobuf(
                    gameRoot,
                    protobufInput,
                    messageType,
                    outputPath);
                return 0;
            }

            var dataRoot = GetOption(args, "--data-root") ?? FindDefaultDataRoot();
            var outputRoot = GetOption(args, "--output") ?? Path.Combine(
                Directory.GetCurrentDirectory(),
                "bd2_design_tables");
            var tableNames = ParseTableNames(GetOption(args, "--tables"));
            var simulatorCompatible = HasFlag(
                args,
                "--simulator-compatible");
            var legacyRoot = GetOption(args, "--merge-legacy-from");
            if (legacyRoot is not null && !simulatorCompatible)
            {
                throw new ArgumentException(
                    "--merge-legacy-from requires --simulator-compatible.");
            }

            var managedRoot = Path.Combine(gameRoot, "BrownDust II_Data", "Managed");
            var assemblyPath = Path.Combine(managedRoot, "Assembly-CSharp.dll");
            var packOption = GetOption(args, "--pack");
            int? pack = null;
            if (packOption is not null)
            {
                if (!int.TryParse(packOption, out var packId) || packId <= 0 || GetOption(args, "--tables") is null)
                    throw new ArgumentException("--pack requires a positive cartridge ID and explicit --tables.");
                pack = packId;
            }
            var databasePath = FindCommonDatabase(dataRoot, pack);

            RequireFile(assemblyPath, "Assembly-CSharp.dll");
            RequireFile(databasePath, "encrypted common database");
            var legacyManifestPath = legacyRoot is null
                ? null
                : Path.Combine(legacyRoot, "manifest.json");
            if (legacyManifestPath is not null)
            {
                RequireFile(legacyManifestPath, "legacy snapshot manifest");
            }
            Directory.CreateDirectory(outputRoot);

            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                var dependencyPath = Path.Combine(managedRoot, name.Name + ".dll");
                return File.Exists(dependencyPath)
                    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependencyPath)
                    : null;
            };

            var gameAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            var databaseApi = GameDatabaseApi.Resolve(gameAssembly);
            var database = OpenDatabase(databaseApi, databasePath);
            var counts = new Dictionary<string, int>();
            var legacyRetainedCounts = new Dictionary<string, int>();

            foreach (var tableName in tableNames)
            {
                var count = ExportTable(
                    gameAssembly,
                    databaseApi,
                    database,
                    tableName,
                    outputRoot,
                    simulatorCompatible,
                    pack.HasValue ? "pack1" : "common");
                var legacyRetained = 0;
                if (legacyRoot is not null)
                {
                    var legacyPath = Path.Combine(
                        legacyRoot,
                        tableName + ".json");
                    RequireFile(legacyPath, $"legacy {tableName}");
                    legacyRetained = LegacyTableMerger.MergeTable(
                        Path.Combine(outputRoot, tableName + ".json"),
                        legacyPath,
                        tableName);
                }
                count += legacyRetained;
                counts[tableName] = count;
                legacyRetainedCounts[tableName] = legacyRetained;
                Console.WriteLine(
                    legacyRetained == 0
                        ? $"{tableName}: {count} rows"
                        : $"{tableName}: {count} rows " +
                            $"(+{legacyRetained} legacy retained)");
            }

            var manifest = BuildManifest(
                databasePath,
                assemblyPath,
                tableNames,
                counts,
                simulatorCompatible,
                legacyManifestPath,
                legacyRetainedCounts);
            File.WriteAllText(
                Path.Combine(outputRoot, "manifest.json"),
                manifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Console.WriteLine($"Exported to: {Path.GetFullPath(outputRoot)}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void DecodeProtobuf(
        string gameRoot,
        string inputPath,
        string messageTypeName,
        string outputPath)
    {
        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);
        var managedRoot = Path.Combine(
            Path.GetFullPath(gameRoot),
            "BrownDust II_Data",
            "Managed");
        var assemblyPath = Path.Combine(managedRoot, "Assembly-CSharp.dll");
        RequireFile(assemblyPath, "Assembly-CSharp.dll");
        RequireFile(inputPath, "protobuf payload");

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependencyPath = Path.Combine(
                managedRoot,
                name.Name + ".dll");
            return File.Exists(dependencyPath)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(
                    dependencyPath)
                : null;
        };

        var gameAssembly =
            AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var messageType = GetGameType(gameAssembly, messageTypeName);
        var parser = messageType.GetProperty(
                "Parser",
                BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null)
            ?? throw new InvalidOperationException(
                $"Could not get the {messageTypeName} protobuf parser.");
        var parseFrom = parser.GetType().GetMethod(
                "ParseFrom",
                new[] { typeof(byte[]) })
            ?? throw new MissingMethodException(
                parser.GetType().FullName,
                "ParseFrom(byte[])");
        var message = parseFrom.Invoke(
                parser,
                new object[] { File.ReadAllBytes(inputPath) })
            ?? throw new InvalidDataException(
                $"{messageTypeName} did not parse.");
        var formatterType = parser.GetType().Assembly.GetType(
                "Google.Protobuf.JsonFormatter",
                throwOnError: true)
            ?? throw new TypeLoadException("Google.Protobuf.JsonFormatter");
        var settingsType = formatterType.GetNestedType(
                "Settings",
                BindingFlags.Public)
            ?? throw new TypeLoadException(
                "Google.Protobuf.JsonFormatter.Settings");
        var settings = Activator.CreateInstance(
                settingsType,
                new object[] { true })
            ?? throw new InvalidOperationException(
                "Could not create protobuf JSON settings.");
        var formatter = Activator.CreateInstance(
                formatterType,
                new[] { settings })
            ?? throw new InvalidOperationException(
                "Could not create the protobuf JSON formatter.");
        var format = formatterType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance)
            .Single(method =>
                method.Name == "Format" &&
                method.GetParameters().Length == 1);
        var json = (string?)format.Invoke(formatter, new[] { message })
            ?? throw new InvalidDataException(
                $"{messageTypeName} did not format.");

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        File.WriteAllText(
            outputPath,
            json + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"Decoded to: {outputPath}");
    }

    private static string FindDefaultGameRoot()
    {
        var configured = Environment.GetEnvironmentVariable("BD2_GAME_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "references", "game-client");
                if (File.Exists(Path.Combine(candidate, "BrownDust II_Data", "Managed", "Assembly-CSharp.dll")))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "BrownDust II was not found. Pass --game-root or set BD2_GAME_ROOT.");
    }

    private static string FindDefaultDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("BD2_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profileData = Directory.GetParent(localAppData)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Windows profile data directory.");
        var candidate = Path.Combine(profileData, "LocalLow", "Gamfs", "BrownDust II", "Data");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        throw new DirectoryNotFoundException(
            "BrownDust II data was not found. Pass --data-root or set BD2_DATA_ROOT.");
    }

    private static object OpenDatabase(
        GameDatabaseApi databaseApi,
        string databasePath)
    {
        var database = Activator.CreateInstance(databaseApi.DatabaseType)
            ?? throw new InvalidOperationException("Could not create the game SQLite wrapper.");

        InvokeIgnoringPostOperationLogFailure(
            databaseApi.OpenMethod,
            database,
            databasePath,
            false);
        InvokeIgnoringPostOperationLogFailure(
            databaseApi.SetKeyMethod,
            database);

        return database;
    }

    private static int ExportTable(
        Assembly gameAssembly,
        GameDatabaseApi databaseApi,
        object database,
        string tableName,
        string outputRoot,
        bool simulatorCompatible,
        string messageNamespace)
    {
        var query = databaseApi.QueryConstructor.Invoke(
                new[] { database, $"SELECT * FROM {tableName}" })
            ?? throw new InvalidOperationException($"Could not query {tableName}.");

        var messageTableName = TableMessageTypeAliases.GetValueOrDefault(
            tableName,
            tableName);
        var messageType = GetGameType(
            gameAssembly,
            $"Proto.Design.{messageNamespace}.{messageTableName}");
        var parser = messageType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null)
            ?? throw new InvalidOperationException(
                $"Could not get the {messageTableName} protobuf parser for {tableName}.");
        var parseFrom = parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) })
            ?? throw new MissingMethodException(parser.GetType().FullName, "ParseFrom(byte[])");
        var formatterType = parser.GetType().Assembly.GetType(
            "Google.Protobuf.JsonFormatter",
            throwOnError: true)
            ?? throw new TypeLoadException("Google.Protobuf.JsonFormatter");
        var settingsType = formatterType.GetNestedType("Settings", BindingFlags.Public)
            ?? throw new TypeLoadException("Google.Protobuf.JsonFormatter.Settings");
        var formatterSettings = Activator.CreateInstance(settingsType, new object[] { true })
            ?? throw new InvalidOperationException("Could not create protobuf JSON settings.");
        var formatter = Activator.CreateInstance(formatterType, new[] { formatterSettings })
            ?? throw new InvalidOperationException("Could not create the protobuf JSON formatter.");
        var formatMessage = formatterType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method =>
                method.Name == "Format" &&
                method.GetParameters().Length == 1);

        var outputPath = Path.Combine(outputRoot, tableName + ".json");
        var count = 0;
        using var writer = new StreamWriter(
            outputPath,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("[");

        try
        {
            var hasRow = (bool)(databaseApi.StepMethod.Invoke(query, null) ?? false);
            if (hasRow)
            {
                var columnCount = (int)(databaseApi.ColumnCountMethod.Invoke(query, null)
                    ?? throw new InvalidOperationException("Could not get query column count."));
                var blobIndex = Enumerable.Range(0, columnCount)
                    .Single(index => string.Equals(
                        (string?)databaseApi.ColumnNameMethod.Invoke(
                            query,
                            new object[] { index }),
                        "ProtoBuf",
                        StringComparison.Ordinal));

                do
                {
                    var blob = (byte[]?)databaseApi.BlobMethod.Invoke(
                            query,
                            new object[] { blobIndex })
                        ?? throw new InvalidDataException(
                            $"{tableName} row {count} has no ProtoBuf payload.");
                    var message = parseFrom.Invoke(parser, new object[] { blob })
                        ?? throw new InvalidDataException($"{tableName} row {count} did not parse.");

                    if (count > 0)
                    {
                        writer.WriteLine(",");
                    }

                    writer.Write("  ");
                    var json = (string?)formatMessage.Invoke(
                            formatter,
                            new[] { message })
                        ?? throw new InvalidDataException(
                            $"{tableName} row {count} did not format.");
                    writer.Write(simulatorCompatible
                        ? NormalizeForSimulator(tableName, json)
                        : json);
                    count++;
                }
                while ((bool)(databaseApi.StepMethod.Invoke(query, null) ?? false));
            }
        }
        finally
        {
            (query as IDisposable)?.Dispose();
        }

        writer.WriteLine();
        writer.WriteLine("]");
        return count;
    }

    private static string FindCommonDatabase(string dataRoot, int? pack = null)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(pack.HasValue ? $"pack{pack.Value}_v1" : "common_v1")));
        var candidates = new[]
        {
            Path.Combine(dataRoot, "t", hash),
            Path.Combine(dataRoot, hash),
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string BuildManifest(
        string databasePath,
        string assemblyPath,
        IReadOnlyList<string> tableNames,
        IReadOnlyDictionary<string, int> counts,
        bool simulatorCompatible,
        string? legacyManifestPath,
        IReadOnlyDictionary<string, int> legacyRetainedCounts)
    {
        var lines = new List<string>
        {
            "{",
            $"  \"exportedAtUtc\": \"{DateTime.UtcNow:O}\",",
            $"  \"databaseFile\": \"{EscapeJson(Path.GetFileName(databasePath))}\",",
            $"  \"databaseSha256\": \"{HashFile(databasePath)}\",",
            $"  \"assemblyFile\": \"{EscapeJson(Path.GetFileName(assemblyPath))}\",",
            $"  \"assemblySha256\": \"{HashFile(assemblyPath)}\",",
        };
        if (simulatorCompatible)
        {
            lines.Add("  \"simulatorCompatible\": true,");
            lines.Add("  \"buffGroupHierarchyIndex\": 3,");
            lines.Add("  \"buffGroupHierarchyPreserved\": true,");
        }
        if (legacyManifestPath is not null)
        {
            lines.Add("  \"legacyCompatibility\": true,");
            lines.Add(
                $"  \"legacySourceManifestSha256\": " +
                $"\"{HashFile(legacyManifestPath)}\",");
            lines.Add("  \"legacyRetainedRows\": {");
            for (var index = 0; index < tableNames.Count; index++)
            {
                var tableName = tableNames[index];
                var suffix = index + 1 == tableNames.Count
                    ? string.Empty
                    : ",";
                lines.Add(
                    $"    \"{tableName}\": " +
                    $"{legacyRetainedCounts[tableName]}{suffix}");
            }
            lines.Add("  },");
        }
        lines.Add("  \"tables\": {");

        for (var index = 0; index < tableNames.Count; index++)
        {
            var tableName = tableNames[index];
            var suffix = index + 1 == tableNames.Count ? string.Empty : ",";
            lines.Add($"    \"{tableName}\": {counts[tableName]}{suffix}");
        }

        lines.Add("  }");
        lines.Add("}");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void InvokeIgnoringPostOperationLogFailure(
        MethodInfo? method,
        object target,
        params object[] arguments)
    {
        if (method is null)
        {
            throw new MissingMethodException(target.GetType().FullName, "game database operation");
        }

        try
        {
            method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (
            exception.InnerException is TypeInitializationException)
        {
            // The database operation completes before a Unity-only logger is initialized.
        }
    }

    private static Type GetGameType(Assembly assembly, params string[] names)
    {
        foreach (var name in names)
        {
            var type = assembly.GetType(name, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }
        throw new TypeLoadException(string.Join(" | ", names));
    }

    private static MethodInfo RequireMethod(Type type, params string[] names)
    {
        foreach (var name in names)
        {
            var method = type.GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Instance);
            if (method is not null)
            {
                return method;
            }
        }
        throw new MissingMethodException(type.FullName, string.Join(" | ", names));
    }

    private static void RequireFile(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Could not find {description}.", path);
        }
    }

    private static string? GetOption(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value after {option}.");
        }

        return args[index + 1];
    }

    private static bool HasFlag(string[] args, string option) =>
        Array.IndexOf(args, option) >= 0;

    private static IReadOnlyList<string> ParseTableNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultTableNames;
        }

        var tableNames = value
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(tableName => tableName.Trim())
            .Where(tableName => tableName.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (tableNames.Length == 0)
        {
            throw new ArgumentException("--tables must contain at least one table name.");
        }

        return tableNames;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string NormalizeForSimulator(
        string tableName,
        string json)
    {
        if (!string.Equals(
                tableName,
                "BuffTable",
                StringComparison.Ordinal))
        {
            return json;
        }

        var replacements = 0;
        var normalized = Regex.Replace(
            json,
            "\"buffGroup\"\\s*:\\s*\\[\\s*(?<values>[^\\]]*)\\s*\\]",
            match =>
            {
                replacements++;
                var values = match.Groups["values"].Value
                    .Split(',', StringSplitOptions.TrimEntries);
                if (values.Length <= 3 ||
                    !int.TryParse(values[3], out var group))
                {
                    throw new InvalidDataException(
                        "BuffTable buffGroup did not contain hierarchy index 3.");
                }
                var hierarchy = string.Join(", ", values);
                return
                    $"\"buffGroup\": {group}, " +
                    $"\"buffGroupHierarchy\": [ {hierarchy} ]";
            },
            RegexOptions.CultureInvariant);
        if (replacements != 1)
        {
            throw new InvalidDataException(
                $"BuffTable row contained {replacements} buffGroup arrays.");
        }
        return normalized;
    }
}




internal sealed class GameDatabaseApi
{
    private static readonly OpCode?[] SingleByteOpCodes = new OpCode?[0x100];
    private static readonly OpCode?[] MultiByteOpCodes = new OpCode?[0x100];

    static GameDatabaseApi()
    {
        foreach (var field in typeof(OpCodes).GetFields(
                     BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            var value = unchecked((ushort)opCode.Value);
            if (value < 0x100)
            {
                SingleByteOpCodes[value] = opCode;
            }
            else if ((value & 0xff00) == 0xfe00)
            {
                MultiByteOpCodes[value & 0xff] = opCode;
            }
        }
    }

    private GameDatabaseApi(
        Type databaseType,
        Type queryType,
        MethodInfo openMethod,
        MethodInfo setKeyMethod,
        ConstructorInfo queryConstructor,
        MethodInfo stepMethod,
        MethodInfo columnCountMethod,
        MethodInfo columnNameMethod,
        MethodInfo blobMethod)
    {
        DatabaseType = databaseType;
        QueryType = queryType;
        OpenMethod = openMethod;
        SetKeyMethod = setKeyMethod;
        QueryConstructor = queryConstructor;
        StepMethod = stepMethod;
        ColumnCountMethod = columnCountMethod;
        ColumnNameMethod = columnNameMethod;
        BlobMethod = blobMethod;
    }

    public Type DatabaseType { get; }
    public Type QueryType { get; }
    public MethodInfo OpenMethod { get; }
    public MethodInfo SetKeyMethod { get; }
    public ConstructorInfo QueryConstructor { get; }
    public MethodInfo StepMethod { get; }
    public MethodInfo ColumnCountMethod { get; }
    public MethodInfo ColumnNameMethod { get; }
    public MethodInfo BlobMethod { get; }

    public static GameDatabaseApi Resolve(Assembly assembly)
    {
        var candidates = GetLoadableTypes(assembly)
            .Where(type =>
                type.IsClass &&
                !type.IsAbstract &&
                typeof(IDisposable).IsAssignableFrom(type))
            .SelectMany(BuildCandidates)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.QueryType.MetadataToken)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new TypeLoadException(
                "Could not identify the game SQLite wrapper by its API shape.");
        }

        var best = candidates[0];
        if (best.Score < 60 ||
            candidates.Skip(1).Any(candidate => candidate.Score == best.Score))
        {
            throw new TypeLoadException(
                "The game SQLite wrapper shape was ambiguous. " +
                string.Join(
                    " | ",
                    candidates.Take(4).Select(candidate =>
                        $"{candidate.DatabaseType.FullName}/{candidate.QueryType.FullName} " +
                        $"score={candidate.Score}")));
        }

        return new GameDatabaseApi(
            best.DatabaseType,
            best.QueryType,
            best.OpenMethod,
            best.SetKeyMethod,
            best.QueryConstructor,
            best.StepMethod,
            best.ColumnCountMethod,
            best.ColumnNameMethod,
            best.BlobMethod);
    }

    private static IEnumerable<Candidate> BuildCandidates(Type queryType)
    {
        var methods = queryType.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var constructors = queryType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance);

        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length < 2 ||
                parameters[1].ParameterType != typeof(string))
            {
                continue;
            }

            var databaseType = parameters[0].ParameterType;
            var databaseMethods = databaseType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var openMethod = databaseMethods
                .Where(method =>
                    method.ReturnType == typeof(void) &&
                    ParametersAre(method, typeof(string), typeof(bool)))
                .OrderByDescending(method =>
                    ContainsString(method, "DB Open") ? 1 : 0)
                .ThenBy(method => method.MetadataToken)
                .FirstOrDefault();
            var setKeyMethod = databaseMethods
                .Where(method =>
                    method.ReturnType == typeof(void) &&
                    method.GetParameters().Length == 0 &&
                    ContainsString(method, "SQLite success set"))
                .OrderBy(method => method.MetadataToken)
                .FirstOrDefault();
            var stepMethod = methods
                .Where(method =>
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 0)
                .OrderByDescending(method =>
                    ContainsString(method, "SQLite step fail") ? 1 : 0)
                .ThenBy(method => method.MetadataToken)
                .FirstOrDefault();
            var columnCountMethod = methods
                .Where(method =>
                    method.ReturnType == typeof(int) &&
                    method.GetParameters().Length == 0)
                .OrderByDescending(method =>
                    ContainsString(method, "read column fail") ? 1 : 0)
                .ThenBy(method => method.MetadataToken)
                .FirstOrDefault();
            var columnNameMethod = methods
                .Where(method =>
                    method.ReturnType == typeof(string) &&
                    ParametersAre(method, typeof(int)))
                .OrderByDescending(method =>
                    ContainsString(method, "unknown field") ? 1 : 0)
                .ThenBy(method => method.MetadataToken)
                .FirstOrDefault();
            var blobMethod = methods
                .Where(method =>
                    method.ReturnType == typeof(byte[]) &&
                    ParametersAre(method, typeof(int)))
                .OrderBy(method => method.MetadataToken)
                .FirstOrDefault();

            if (openMethod is null ||
                setKeyMethod is null ||
                stepMethod is null ||
                columnCountMethod is null ||
                columnNameMethod is null ||
                blobMethod is null)
            {
                continue;
            }

            var score = 0;
            score += typeof(IDisposable).IsAssignableFrom(queryType) ? 10 : 0;
            score += parameters.Length == 2 ? 8 : 0;
            score += databaseType.GetConstructor(Type.EmptyTypes) is not null ? 8 : 0;
            score += ContainsString(openMethod, "DB Open") ? 12 : 0;
            score += ContainsString(setKeyMethod, "SQLite success set") ? 12 : 0;
            score += ContainsString(stepMethod, "SQLite step fail") ? 12 : 0;
            score += ContainsString(columnCountMethod, "read column fail") ? 12 : 0;
            score += ContainsString(columnNameMethod, "unknown field") ? 8 : 0;
            score += 8;

            yield return new Candidate(
                databaseType,
                queryType,
                openMethod,
                setKeyMethod,
                constructor,
                stepMethod,
                columnCountMethod,
                columnNameMethod,
                blobMethod,
                score);
        }
    }

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types
                .Where(type => type is not null)
                .Cast<Type>()
                .ToArray();
        }
    }

    private static bool ParametersAre(MethodInfo method, params Type[] types)
    {
        var parameters = method.GetParameters();
        return parameters.Length == types.Length &&
            parameters.Select(parameter => parameter.ParameterType)
                .SequenceEqual(types);
    }

    private static bool ContainsString(MethodBase method, string fragment)
    {
        return GetLoadedStrings(method).Any(value =>
            value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static IEnumerable<string> GetLoadedStrings(MethodBase method)
    {
        var body = method.GetMethodBody();
        var bytes = body?.GetILAsByteArray();
        if (bytes is null)
        {
            yield break;
        }

        var offset = 0;
        while (offset < bytes.Length)
        {
            var maybeOpCode = ReadOpCode(bytes, ref offset);
            if (maybeOpCode is null)
            {
                yield break;
            }
            var opCode = maybeOpCode.Value;

            if (opCode.OperandType == OperandType.InlineString)
            {
                if (offset + 4 > bytes.Length)
                {
                    yield break;
                }
                var token = BitConverter.ToInt32(bytes, offset);
                string? value = null;
                try
                {
                    value = method.Module.ResolveString(token);
                }
                catch (ArgumentException)
                {
                }
                if (value is not null)
                {
                    yield return value;
                }
            }

            offset += OperandSize(opCode.OperandType, bytes, offset);
        }
    }

    private static OpCode? ReadOpCode(byte[] bytes, ref int offset)
    {
        if (offset >= bytes.Length)
        {
            return null;
        }

        var first = bytes[offset++];
        if (first != 0xfe)
        {
            return SingleByteOpCodes[first];
        }
        if (offset >= bytes.Length)
        {
            return null;
        }
        return MultiByteOpCodes[bytes[offset++]];
    }

    private static int OperandSize(
        OperandType operandType,
        byte[] bytes,
        int offset)
    {
        return operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI => 4,
            OperandType.InlineBrTarget => 4,
            OperandType.InlineField => 4,
            OperandType.InlineMethod => 4,
            OperandType.InlineSig => 4,
            OperandType.InlineString => 4,
            OperandType.InlineTok => 4,
            OperandType.InlineType => 4,
            OperandType.ShortInlineR => 4,
            OperandType.InlineI8 => 8,
            OperandType.InlineR => 8,
            OperandType.InlineSwitch => SwitchOperandSize(bytes, offset),
            _ => throw new InvalidDataException(
                $"Unsupported IL operand type: {operandType}"),
        };
    }

    private static int SwitchOperandSize(byte[] bytes, int offset)
    {
        if (offset + 4 > bytes.Length)
        {
            return Math.Max(0, bytes.Length - offset);
        }
        var count = BitConverter.ToInt32(bytes, offset);
        return 4 + Math.Max(0, count) * 4;
    }

    private sealed record Candidate(
        Type DatabaseType,
        Type QueryType,
        MethodInfo OpenMethod,
        MethodInfo SetKeyMethod,
        ConstructorInfo QueryConstructor,
        MethodInfo StepMethod,
        MethodInfo ColumnCountMethod,
        MethodInfo ColumnNameMethod,
        MethodInfo BlobMethod,
        int Score);
}




internal static class LegacyTableMerger
{
    private static readonly IReadOnlyDictionary<string, string[]> PrimaryKeys =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["BattleDefaultTable"] = ["id"],
            ["BattleFieldBuffTable"] = ["id"],
            ["BuffConditionTable"] = ["id"],
            ["BuffTable"] = ["id"],
            ["CharAwakeGrowthTable"] = ["id"],
            ["CharAwakeTable"] = ["id"],
            ["CharGrowthTable"] = ["id"],
            ["CharImprintTable"] = ["id"],
            ["CharLevelTable"] = ["groupId", "id"],
            ["CharTable"] = ["id"],
            ["CostumeBurstTable"] = ["groupId", "id"],
            ["CostumeGrowthTable"] = ["groupId", "id"],
            ["CostumeNodeGroupTable"] = ["id"],
            ["CostumeNodeTable"] = ["groupId", "id"],
            ["CostumeTable"] = ["id"],
            ["EquipmentGradeTable"] = ["id"],
            ["EquipmentGrowthTable"] = ["groupId", "id"],
            ["EquipmentOptionTable"] = ["groupId", "id"],
            ["EquipmentRankTable"] = ["groupId", "id"],
            ["EquipmentTable"] = ["id"],
            ["PvpDefaultTable"] = ["id"],
            ["SkillTable"] = ["groupId", "id"],
        };

    public static int MergeTable(
        string currentPath,
        string legacyPath,
        string tableName)
    {
        if (!PrimaryKeys.TryGetValue(tableName, out var primaryKey))
        {
            throw new InvalidOperationException(
                $"No legacy compatibility key is defined for {tableName}.");
        }

        using var current = JsonDocument.Parse(File.ReadAllText(currentPath));
        using var legacy = JsonDocument.Parse(File.ReadAllText(legacyPath));
        RequireArray(current.RootElement, currentPath);
        RequireArray(legacy.RootElement, legacyPath);

        var currentKeys = ReadUniqueKeys(
            current.RootElement,
            primaryKey,
            tableName,
            "current");
        var legacyKeys = new HashSet<string>(StringComparer.Ordinal);
        var retained = new List<string>();
        foreach (var row in legacy.RootElement.EnumerateArray())
        {
            var key = BuildKey(row, primaryKey, tableName);
            if (!legacyKeys.Add(key))
            {
                throw new InvalidDataException(
                    $"{tableName} legacy snapshot contains duplicate key {key}.");
            }
            if (!currentKeys.Contains(key))
            {
                retained.Add(row.GetRawText());
            }
        }

        if (retained.Count == 0)
        {
            return 0;
        }

        var currentText = File.ReadAllText(currentPath);
        var closingBracket = currentText.LastIndexOf(']');
        if (closingBracket < 0 ||
            currentText[(closingBracket + 1)..].Any(character =>
                !char.IsWhiteSpace(character)))
        {
            throw new InvalidDataException(
                $"{currentPath} is not a standalone JSON array.");
        }

        var prefix = currentText[..closingBracket].TrimEnd();
        var builder = new StringBuilder(prefix);
        if (current.RootElement.GetArrayLength() > 0)
        {
            builder.AppendLine(",");
        }
        else
        {
            builder.AppendLine();
        }

        for (var index = 0; index < retained.Count; index++)
        {
            builder.Append("  ");
            builder.Append(retained[index]);
            builder.AppendLine(index + 1 == retained.Count ? string.Empty : ",");
        }
        builder.AppendLine("]");
        File.WriteAllText(
            currentPath,
            builder.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return retained.Count;
    }

    private static HashSet<string> ReadUniqueKeys(
        JsonElement rows,
        IReadOnlyList<string> primaryKey,
        string tableName,
        string snapshot)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            var key = BuildKey(row, primaryKey, tableName);
            if (!keys.Add(key))
            {
                throw new InvalidDataException(
                    $"{tableName} {snapshot} snapshot contains duplicate key {key}.");
            }
        }
        return keys;
    }

    private static string BuildKey(
        JsonElement row,
        IReadOnlyList<string> primaryKey,
        string tableName)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"{tableName} contains a non-object row.");
        }

        return string.Join(
            ":",
            primaryKey.Select(key =>
            {
                if (!row.TryGetProperty(key, out var value))
                {
                    throw new InvalidDataException(
                        $"{tableName} row is missing primary-key field {key}.");
                }
                return value.GetRawText();
            }));
    }

    private static void RequireArray(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path} is not a JSON array.");
        }
    }
}
