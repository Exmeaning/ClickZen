using System.IO.Compression;
using System.Text.Json;
using ClickZen.Core.Persistence;
using ClickZen.Core.Recording;

namespace ClickZen.Core.Automation;

/// <summary>
/// Reads and writes .czscheme packages – a zip containing:
/// <code>
///   scheme.json            the Scheme (templates listed without bytes)
///   assets/{id}.png        template images
///   recordings/{id}.czrec  embedded recordings
/// </code>
/// </summary>
public static class SchemePackage
{
    public const string FileExtension = ".czscheme";
    private const string SchemeEntry = "scheme.json";
    private const string AssetsDir = "assets/";
    private const string RecordingsDir = "recordings/";

    public static JsonSerializerOptions JsonOptions => RecordingDocument.Options;

    /// <summary>A loaded package: the scheme plus its embedded recordings.</summary>
    public sealed class Contents
    {
        public required Scheme Scheme { get; init; }
        public Dictionary<string, RecordingDocument> Recordings { get; init; } = new(StringComparer.Ordinal);
    }

    public static void Save(string path, Scheme scheme, IReadOnlyDictionary<string, RecordingDocument>? recordings = null)
    {
        using var ms = new MemoryStream();
        Write(ms, scheme, recordings);
        AtomicFile.WriteAllBytes(path, ms.ToArray());
    }

    public static void Write(Stream output, Scheme scheme, IReadOnlyDictionary<string, RecordingDocument>? recordings = null)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        scheme.FormatVersion = Scheme.CurrentFormatVersion;
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var schemeEntry = zip.CreateEntry(SchemeEntry, CompressionLevel.Optimal);
        using (var s = schemeEntry.Open())
        {
            JsonSerializer.Serialize(s, scheme, JsonOptions);
        }

        foreach (var t in scheme.Templates)
        {
            ValidateId(t.Id);
            var e = zip.CreateEntry(AssetsDir + t.Id + ".png", CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(t.Png);
        }

        if (recordings is not null)
        {
            foreach (var (id, rec) in recordings)
            {
                ValidateId(id);
                var e = zip.CreateEntry(RecordingsDir + id + RecordingDocument.FileExtension, CompressionLevel.Optimal);
                using var s = e.Open();
                JsonSerializer.Serialize(s, rec, RecordingDocument.Options);
            }
        }
    }

    public static Contents Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static Contents Read(Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry(SchemeEntry) ?? throw new InvalidDataException("Not a ClickZen scheme package (scheme.json missing).");

        Scheme scheme;
        using (var s = entry.Open())
        {
            scheme = JsonSerializer.Deserialize<Scheme>(s, JsonOptions) ?? throw new InvalidDataException("scheme.json is empty.");
        }

        if (scheme.FormatVersion > Scheme.CurrentFormatVersion)
        {
            throw new InvalidDataException($"Scheme format {scheme.FormatVersion} is newer than this version of ClickZen supports ({Scheme.CurrentFormatVersion}).");
        }

        foreach (var t in scheme.Templates)
        {
            ValidateId(t.Id);
            var asset = zip.GetEntry(AssetsDir + t.Id + ".png") ?? throw new InvalidDataException($"Template '{t.Name}' ({t.Id}) is missing from the package.");
            using var s = asset.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            t.Png = ms.ToArray();
        }

        var recordings = new Dictionary<string, RecordingDocument>(StringComparer.Ordinal);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(RecordingsDir, StringComparison.Ordinal)
                                                  && e.FullName.EndsWith(RecordingDocument.FileExtension, StringComparison.Ordinal)))
        {
            var id = e.FullName[RecordingsDir.Length..^RecordingDocument.FileExtension.Length];
            using var s = e.Open();
            recordings[id] = JsonSerializer.Deserialize<RecordingDocument>(s, RecordingDocument.Options)
                             ?? throw new InvalidDataException($"Recording '{id}' is empty.");
        }

        Normalize(scheme);
        return new Contents { Scheme = scheme, Recordings = recordings };
    }

    /// <summary>
    /// Problems that would make the scheme misbehave at run time (dangling template ids,
    /// unparsable expressions, empty rules). Returned as human-readable strings; empty = OK.
    /// </summary>
    public static IReadOnlyList<SchemeIssue> Validate(Scheme scheme, IReadOnlyCollection<string>? embeddedRecordings = null)
    {
        var issues = new List<SchemeIssue>();
        var templateIds = scheme.Templates.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var dup in scheme.Variables.GroupBy(v => v.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            issues.Add(new SchemeIssue(null, $"Variable '{dup.Key}' is declared more than once."));
        }

        foreach (var task in scheme.Tasks)
        {
            if (task.Rules.Count == 0)
            {
                issues.Add(new SchemeIssue(task.Id, $"Task '{task.Name}' has no rules."));
            }

            foreach (var c in AllConditions(task.Precondition).Concat(task.Rules.SelectMany(r => AllConditions(r.When))))
            {
                switch (c)
                {
                    case ImageCondition ic when !templateIds.Contains(ic.TemplateId):
                        issues.Add(new SchemeIssue(task.Id, $"Task '{task.Name}' references a missing template."));
                        break;
                    case ExpressionCondition ec when !Variables.Expression.TryParse(ec.Expression, out _, out var err):
                        issues.Add(new SchemeIssue(task.Id, $"Task '{task.Name}': invalid condition \"{ec.Expression}\": {err}"));
                        break;
                }
            }

            foreach (var rule in task.Rules)
            {
                var actions = rule.Pick == RulePick.Sequential ? rule.Then : rule.Branches.SelectMany(b => b.Actions).ToList();
                if (actions.Count == 0)
                {
                    issues.Add(new SchemeIssue(task.Id, $"Task '{task.Name}', rule '{rule.Name}' has no actions."));
                }

                foreach (var a in actions)
                {
                    switch (a)
                    {
                        case SetVariableAction sv when string.IsNullOrWhiteSpace(sv.Variable):
                            issues.Add(new SchemeIssue(task.Id, $"Task '{task.Name}': a Set variable action has no variable name."));
                            break;
                        case SetVariableAction sv when !Variables.Expression.TryParse(sv.Expression, out _, out var err):
                            issues.Add(new SchemeIssue(task.Id, $"Task '{task.Name}': invalid expression \"{sv.Expression}\": {err}"));
                            break;
                        case PlayRecordingAction pr when embeddedRecordings is not null && !embeddedRecordings.Contains(pr.Recording)
                                                         && !pr.Recording.EndsWith(RecordingDocument.FileExtension, StringComparison.OrdinalIgnoreCase):
                            issues.Add(new SchemeIssue(task.Id, $"Task '{task.Name}' plays recording '{pr.Recording}', which is not in the scheme."));
                            break;
                    }
                }
            }
        }

        return issues;
    }

    public static IEnumerable<Condition> AllConditions(ConditionGroup g) =>
        g.Conditions.Concat(g.Groups.SelectMany(AllConditions));

    private static void Normalize(Scheme s)
    {
        s.Settings ??= new();
        s.Variables ??= [];
        s.Tasks ??= [];
        s.Templates ??= [];
        foreach (var t in s.Tasks)
        {
            t.Precondition ??= new();
            t.Rules ??= [];
        }
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(['/', '\\', ':', '.']) >= 0 || id.Length > 64)
        {
            throw new InvalidDataException($"Invalid asset id '{id}'.");
        }
    }
}

/// <summary>A validation finding; <see cref="TaskId"/> is null for scheme-level issues.</summary>
public sealed record SchemeIssue(string? TaskId, string Message);
