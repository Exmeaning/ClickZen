using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Core.Recording;

namespace ClickZen.Core.Tests.Automation;

public sealed class SchemePackageTests
{
    private static Scheme Sample() => new()
    {
        Name = "demo",
        RefSize = new SizeI(2400, 1080),
        Variables = [new VariableDefinition { Name = "score", Type = ClickZen.Core.Variables.VariableType.Int, Initial = 5, Sync = SyncDirection.Both }],
        Templates = [new TemplateAsset { Id = "start", Name = "Start button", Size = new SizeI(120, 40), Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3] }],
        Tasks =
        [
            new AutomationTask
            {
                Id = "t1", Name = "Start", CooldownMs = 2000, Priority = 3,
                Precondition = new ConditionGroup { Logic = GroupLogic.None, Conditions = [new ExpressionCondition { Expression = "score > 10" }] },
                Rules =
                [
                    new Rule
                    {
                        Name = "click start",
                        When = new ConditionGroup { Conditions = [new ImageCondition { TemplateId = "start", Area = new RectI(10, 20, 300, 400), Threshold = 0.9 }] },
                        Then =
                        [
                            new TapAction { Target = new Target { Kind = TargetKind.LastMatch, Offset = new PointI(0, 5) } },
                            new WaitAction { MinMs = 500, MaxMs = 900 },
                            new SetVariableAction { Variable = "score", Expression = "score + 1" },
                        ],
                    },
                    new Rule
                    {
                        Pick = RulePick.RandomOne,
                        When = new ConditionGroup { Conditions = [new ElapsedCondition { AtLeastMs = 5000 }] },
                        Branches = [new ActionBranch { Weight = 2, Actions = [new PlayRecordingAction { Recording = "intro", Speed = 1.5 }] }],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Round_trip_preserves_everything()
    {
        var recordings = new Dictionary<string, RecordingDocument> { ["intro"] = new() { Name = "intro", ScreenSize = new SizeI(2400, 1080) } };
        using var ms = new MemoryStream();
        SchemePackage.Write(ms, Sample(), recordings);
        ms.Position = 0;

        var loaded = SchemePackage.Read(ms);
        var s = loaded.Scheme;

        Assert.Equal("demo", s.Name);
        Assert.Equal(new SizeI(2400, 1080), s.RefSize);
        Assert.Equal(5, s.Variables[0].Initial.AsInt);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, s.Templates[0].Png);
        var task = Assert.Single(s.Tasks);
        Assert.Equal(GroupLogic.None, task.Precondition.Logic);
        var img = Assert.IsType<ImageCondition>(task.Rules[0].When.Conditions[0]);
        Assert.Equal(new RectI(10, 20, 300, 400), img.Area);
        Assert.Equal(0.9, img.Threshold);
        Assert.Equal(TargetKind.LastMatch, Assert.IsType<TapAction>(task.Rules[0].Then[0]).Target.Kind);
        Assert.Equal(900, Assert.IsType<WaitAction>(task.Rules[0].Then[1]).MaxMs);
        Assert.Equal(RulePick.RandomOne, task.Rules[1].Pick);
        Assert.Equal(1.5, Assert.IsType<PlayRecordingAction>(task.Rules[1].Branches[0].Actions[0]).Speed);
        Assert.Equal("intro", Assert.Single(loaded.Recordings).Key);
        Assert.Empty(SchemePackage.Validate(s, loaded.Recordings.Keys));
    }

    [Fact]
    public void Scheme_json_uses_readable_type_discriminators()
    {
        using var ms = new MemoryStream();
        SchemePackage.Write(ms, Sample());
        ms.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(ms);
        using var reader = new StreamReader(zip.GetEntry("scheme.json")!.Open());
        var json = reader.ReadToEnd();

        Assert.Contains("\"type\": \"image\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"tap\"", json, StringComparison.Ordinal);
        Assert.Contains("\"logic\": \"none\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("png", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(zip.GetEntry("assets/start.png"));
    }

    [Fact]
    public void Missing_asset_is_reported()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("scheme.json");
            using var w = new StreamWriter(e.Open());
            w.Write("""{ "formatVersion": 1, "templates": [ { "id": "gone", "name": "x" } ] }""");
        }

        ms.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => SchemePackage.Read(ms));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_package_is_rejected()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("readme.txt");
        }

        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => SchemePackage.Read(ms));
    }

    [Fact]
    public void Path_traversal_ids_are_rejected()
    {
        var s = Sample();
        s.Templates[0].Id = "../evil";
        using var ms = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => SchemePackage.Write(ms, s));
    }

    [Fact]
    public void Validate_finds_common_mistakes()
    {
        var s = Sample();
        s.Tasks[0].Rules[0].When.Conditions.Add(new ImageCondition { TemplateId = "nope" });
        s.Tasks[0].Rules[0].When.Conditions.Add(new ExpressionCondition { Expression = "score >" });
        s.Tasks.Add(new AutomationTask { Id = "empty", Name = "Empty" });
        s.Variables.Add(new VariableDefinition { Name = "score" });

        var issues = SchemePackage.Validate(s, Array.Empty<string>());

        Assert.Contains(issues, i => i.Message.Contains("missing template", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("invalid condition", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("no rules", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("declared more than once", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("intro", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_and_load_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cz-{Guid.NewGuid():N}{SchemePackage.FileExtension}");
        try
        {
            SchemePackage.Save(path, Sample());
            Assert.Equal("demo", SchemePackage.Load(path).Scheme.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
