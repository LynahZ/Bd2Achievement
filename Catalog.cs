using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bd2Achievement;

internal sealed record Tier(int Id, string Name, decimal Target, string Description);
internal sealed record Series(int GroupId, Tier[] Tiers)
{
    public decimal FinalTarget => Tiers.Max(t => t.Target);
}

internal sealed class Catalog(string folder)
{
    private sealed record Row(int Id, int GroupId, int ContentsGroup, int EventId, int UseType,
        int TitleLocalTextId, int DescLocalTextId, decimal ConditionValue);
    private sealed record TextRow(int Id, string TextCn, string TextTw, string TextEn, string TextJp, string Text);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string AssemblyPath = Path.Combine("BrownDust II_Data", "Managed", "Assembly-CSharp.dll");
    private static readonly string DatabaseName = Convert.ToHexString(SHA1.HashData("common_v1"u8));
    private readonly Row[] rows = Read<Row>(folder, "AchievementTable");
    private readonly Dictionary<int, TextRow> texts = Read<TextRow>(folder, "LocalTextTable").ToDictionary(row => row.Id);
    public int AchievementCount => rows.Length;
    public int TextCount => texts.Count;

    private static T[] Read<T>(string path, string table)
    {
        using var stream = File.OpenRead(Path.Combine(path, table + ".json"));
        return JsonSerializer.Deserialize<T[]>(stream, Json) ?? throw new InvalidDataException($"无法读取 {table}。");
    }

    public static Catalog Load()
    {
        var client = FindClient();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = Environment.GetEnvironmentVariable("BD2_DATA_ROOT")
            ?? Path.Combine(profile, "AppData", "LocalLow", "Gamfs", "BrownDust II", "Data");
        var database = Path.Combine(data, "t", DatabaseName);
        if (!File.Exists(database)) database = Path.Combine(data, DatabaseName);
        if (!File.Exists(database)) throw new FileNotFoundException($"找不到游戏数据。请检查 {data}，或设置 BD2_DATA_ROOT。");

        var assembly = Path.Combine(client, AssemblyPath);
        var databaseHash = Hash(database);
        var assemblyHash = Hash(assembly);
        var cache = Environment.GetEnvironmentVariable("BD2_ACHIEVEMENT_CACHE") ?? Path.Combine(local, "Bd2Achievement");
        Directory.CreateDirectory(cache);
        var target = Path.Combine(cache, $"{databaseHash}-{assemblyHash}");
        if (!File.Exists(Path.Combine(target, "manifest.json")))
            Export(client, assembly, database, databaseHash, assemblyHash, cache, target);
        return new Catalog(target);
    }

    private static string FindClient()
    {
        static bool Valid(string path) => File.Exists(Path.Combine(path, AssemblyPath));
        var configured = Environment.GetEnvironmentVariable("BD2_GAME_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Valid(configured)) return Path.GetFullPath(configured);
            throw new DirectoryNotFoundException($"BD2_GAME_ROOT 指向的目录里没有游戏程序集。{configured}");
        }
        var installs = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var parent = Path.Combine(drive.RootDirectory.FullName, "Neowiz", "Browndust2");
            if (!Directory.Exists(parent)) continue;
            if (Valid(parent)) installs.Add(parent);
            installs.AddRange(Directory.EnumerateDirectories(parent).Where(Valid));
        }
        return installs.OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, AssemblyPath)))
            .FirstOrDefault() ?? throw new DirectoryNotFoundException("找不到游戏客户端。自定义安装路径可通过 BD2_GAME_ROOT 指定。");
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void Export(string client, string assembly, string database, string databaseHash,
        string assemblyHash, string cache, string target)
    {
        var stage = Directory.CreateTempSubdirectory("bd2-export-");
        var output = Path.Combine(cache, ".export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = Path.Combine(stage.FullName, DatabaseName);
            File.Copy(database, snapshot);
            if (Hash(snapshot) != databaseHash) throw GameChanged();

            var worker = Environment.ProcessPath!;
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("exporter-x64.exe")
                    ?? throw new InvalidDataException("ARM64 包缺少 x64 导出组件。");
                worker = Path.Combine(stage.FullName, "Bd2Exporter-x64.exe");
                using var file = File.Create(worker);
                resource.CopyTo(file);
            }
            var start = new ProcessStartInfo(worker)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = client
            };
            foreach (var arg in new[] { "--export", "--game-root", client, "--data-root", stage.FullName,
                         "--output", output, "--tables", "AchievementTable,LocalTextTable" })
                start.ArgumentList.Add(arg);
            start.Environment["PATH"] = string.Join(';', client,
                Path.Combine(client, "BrownDust II_Data", "Plugins", "x86_64"), Environment.GetEnvironmentVariable("PATH"));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动导出组件。");
            var errors = process.StandardError.ReadToEndAsync();
            var log = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(errors, log);
            if (process.ExitCode != 0) throw new InvalidOperationException($"导出失败（{process.ExitCode}）。{Environment.NewLine}{errors.Result}");
            if (Hash(database) != databaseHash || Hash(assembly) != assemblyHash) throw GameChanged();
            var catalog = new Catalog(output);
            if (catalog.AchievementCount == 0 || catalog.TextCount == 0)
                throw new InvalidDataException("游戏导出的表为空。");
            try { Directory.Move(output, target); }
            catch (IOException) when (File.Exists(Path.Combine(target, "manifest.json"))) { }
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            stage.Delete(recursive: true);
        }
    }

    private static IOException GameChanged() => new("游戏文件在导出期间发生变化。请等待更新结束后重试。");

    public Series[] Search(string query)
    {
        var needle = Normalize(query);
        if (needle.Length == 0) return [];
        var ranks = new Dictionary<int, int>();
        int TextRank(int id)
        {
            if (ranks.TryGetValue(id, out var rank)) return rank;
            rank = texts.TryGetValue(id, out var text)
                ? new[] { text.TextCn, text.TextTw, text.TextEn, text.TextJp, text.Text }.Max(value => Match(value, needle))
                : 0;
            ranks[id] = rank;
            return rank;
        }
        var groups = rows.GroupBy(row => (row.GroupId, row.ContentsGroup, row.EventId, row.UseType))
            .Select(group => (Rows: group, Rank: group.Max(row => Math.Max(TextRank(row.TitleLocalTextId), TextRank(row.DescLocalTextId)))))
            .Where(group => group.Rank > 0).ToArray();
        if (groups.Length == 0) return [];
        var best = groups.Max(group => group.Rank);
        return groups.Where(group => group.Rank == best)
            .Select(group => new Series(group.Rows.Key.GroupId, group.Rows.OrderBy(row => row.Id)
                .Select(row => new Tier(row.Id, Display(row.TitleLocalTextId), row.ConditionValue,
                    Display(row.DescLocalTextId).Replace("{0}", Format(row.ConditionValue)))).ToArray())).ToArray();
    }

    private string Display(int id) => texts.TryGetValue(id, out var text)
        ? new[] { text.TextCn, text.TextEn, text.Text }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? $"[{id}]"
        : $"[{id}]";

    public static string Format(decimal value) => value.ToString("0.############################");

    private static string Normalize(string value) =>
        Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"[\s\p{P}]", "").ToLowerInvariant();

    private static int Match(string? value, string needle)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        const string slot = "\ue000";
        var marked = Normalize(Regex.Replace(value, @"\{\d+[^}]*\}", slot));
        var literal = marked.Replace(slot, "");
        if (literal == needle) return 2;
        if (literal.Contains(needle, StringComparison.Ordinal)) return 1;
        return marked.Contains(slot) && Regex.IsMatch(needle, Regex.Escape(marked).Replace(slot, @"\d+")) ? 1 : 0;
    }
}
