using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using Xunit;

namespace RuleOpen.Core.Tests;

internal sealed class FakeFileSystem : IFileSystem
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string path) => Files.Contains(path);
    public bool DirectoryExists(string path) => Directories.Contains(path);
}

public sealed class RuleEngineTests
{
    private static OpeningRule MakeRule(
        string id,
        string name,
        string folder,
        string[] extensions,
        string appName,
        bool includeSubfolders = true,
        int priority = 100,
        bool enabled = true) => new()
    {
        Id = id,
        Name = name,
        FolderPath = folder,
        Extensions = [.. extensions],
        IncludeSubfolders = includeSubfolders,
        Priority = priority,
        Enabled = enabled,
        TargetApplication = new ApplicationTarget
        {
            Kind = ApplicationTargetKind.RegisteredHandler,
            ApplicationId = $"App.{appName}",
            DisplayName = appName
        }
    };

    [Fact]
    public void ExactFolderMatch_SelectsRule()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\University\syllabus.pdf");
        RuleEngine engine = new(fs);

        RuleOpenConfig config = new()
        {
            Rules = [MakeRule("r1", "University PDFs", @"D:\University\", [".pdf"], "Adobe Acrobat", includeSubfolders: false)]
        };

        RuleEvaluationResult result = engine.Evaluate(@"D:\University\syllabus.pdf", config);

        Assert.Equal(RuleEvaluationStatus.Matched, result.Status);
        Assert.Equal("r1", result.WinningRule?.Id);
        Assert.Equal("Adobe Acrobat", result.WinningRule?.TargetApplication.DisplayName);
    }

    [Fact]
    public void NonRecursiveRule_DoesNotMatchSubfolder()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\University\Semester1\lecture.pdf");
        RuleEngine engine = new(fs);

        RuleOpenConfig config = new()
        {
            Rules = [MakeRule("r1", "Root Only", @"D:\University", [".pdf"], "Adobe Acrobat", includeSubfolders: false)]
        };

        RuleEvaluationResult result = engine.Evaluate(@"D:\University\Semester1\lecture.pdf", config);

        Assert.Equal(RuleEvaluationStatus.NoMatch, result.Status);
        Assert.Null(result.WinningRule);
        Assert.Single(result.NonMatchingRules);
    }

    [Fact]
    public void RecursiveRule_MatchesNestedSubfolders()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\University\2026\Algorithms\notes.PDF");
        RuleEngine engine = new(fs);

        RuleOpenConfig config = new()
        {
            Rules = [MakeRule("r1", "University PDFs", @"D:\University", [".pdf"], "Adobe Acrobat", includeSubfolders: true)]
        };

        RuleEvaluationResult result = engine.Evaluate(@"D:\University\2026\Algorithms\notes.PDF", config);

        Assert.Equal(RuleEvaluationStatus.Matched, result.Status);
        Assert.Equal("r1", result.WinningRule?.Id);
        Assert.Equal(".pdf", result.NormalizedExtension);
    }

    [Fact]
    public void FolderPrefixCollision_NeverMatchesSiblingDirectory()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"C:\Test\Workspace\file.pdf");
        RuleEngine engine = new(fs);

        RuleOpenConfig config = new()
        {
            Rules = [MakeRule("r1", "Work Folder", @"C:\Test\Work", [".pdf"], "Microsoft Edge", includeSubfolders: true)]
        };

        RuleEvaluationResult result = engine.Evaluate(@"C:\Test\Workspace\file.pdf", config);

        Assert.Equal(RuleEvaluationStatus.NoMatch, result.Status);
        Assert.Null(result.WinningRule);
    }

    [Fact]
    public void MoreSpecificFolder_TakesPrecedenceOverParentFolder()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\Projects\ClientA\notes.txt");
        RuleEngine engine = new(fs);

        OpeningRule broad = MakeRule("r-broad", "Projects", @"D:\Projects\", [".txt"], "Notepad", priority: 500);
        OpeningRule specific = MakeRule("r-spec", "ClientA", @"D:\Projects\ClientA\", [".txt"], "VS Code", priority: 100);

        RuleOpenConfig config = new() { Rules = [broad, specific] };

        RuleEvaluationResult result = engine.Evaluate(@"D:\Projects\ClientA\notes.txt", config);

        Assert.Equal(RuleEvaluationStatus.Matched, result.Status);
        Assert.Equal("r-spec", result.WinningRule?.Id);
        Assert.Equal(2, result.MatchingCandidates.Count);
    }

    [Fact]
    public void PriorityFirstMode_AllowsHigherPriorityParentRuleToWin()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\Projects\ClientA\notes.txt");
        RuleEngine engine = new(fs);

        OpeningRule broad = MakeRule("r-broad", "Projects", @"D:\Projects", [".txt"], "Notepad", priority: 500);
        OpeningRule specific = MakeRule("r-spec", "ClientA", @"D:\Projects\ClientA", [".txt"], "VS Code", priority: 100);

        RuleOpenConfig config = new()
        {
            Rules = [broad, specific],
            Settings = new AppSettings { ConflictResolutionMode = ConflictResolutionMode.PriorityFirst }
        };

        RuleEvaluationResult result = engine.Evaluate(@"D:\Projects\ClientA\notes.txt", config);

        Assert.Equal("r-broad", result.WinningRule?.Id);
    }

    [Fact]
    public void RuleEvaluation_IsIndependentOfCollectionOrder()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\Work\Contracts\2026\draft.md");
        RuleEngine engine = new(fs);

        OpeningRule[] rules =
        [
            MakeRule("id-c", "Root Drive", @"D:\", [".md"], "Notepad", priority: 100),
            MakeRule("id-b", "Work", @"D:\Work", [".md", ".txt"], "WordPad", priority: 200),
            MakeRule("id-a", "Contracts", @"D:\Work\Contracts", [".md", ".txt", ".markdown"], "Typora", priority: 150),
            MakeRule("id-d", "Contracts Single", @"D:\Work\Contracts", [".md"], "VS Code", priority: 150)
        ];

        string? expectedWinner = null;
        foreach (OpeningRule[] permutation in new[]
        {
            rules,
            [rules[3], rules[2], rules[1], rules[0]],
            [rules[1], rules[3], rules[0], rules[2]],
            [rules[2], rules[0], rules[3], rules[1]]
        })
        {
            RuleEvaluationResult result = engine.Evaluate(
                @"D:\Work\Contracts\2026\draft.md",
                new RuleOpenConfig { Rules = [.. permutation] });

            Assert.Equal("id-d", result.WinningRule?.Id);
            expectedWinner ??= result.WinningRule?.Id;
            Assert.Equal(expectedWinner, result.WinningRule?.Id);
        }
    }

    [Fact]
    public void DisabledRules_AreIgnored()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\Work\report.pdf");
        RuleEngine engine = new(fs);

        RuleOpenConfig config = new()
        {
            Rules = [MakeRule("r1", "Disabled", @"D:\Work", [".pdf"], "Edge", enabled: false)]
        };

        RuleEvaluationResult result = engine.Evaluate(@"D:\Work\report.pdf", config);

        Assert.Equal(RuleEvaluationStatus.NoMatch, result.Status);
        Assert.Null(result.WinningRule);
        Assert.Contains("disabled", result.NonMatchingRules[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnicodeAndSpacesPaths_MatchCaseInsensitively()
    {
        FakeFileSystem fs = new();
        string path = @"D:\Universität\Übungen 2026\日本語 ファイル\Aufgabe 1.MaRkDoWn";
        fs.Files.Add(path);
        RuleEngine engine = new(fs);

        RuleOpenConfig config = new()
        {
            Rules = [MakeRule("r1", "Uni Notes", @"d:/universität/übungen 2026/", [".md", ".markdown", ".txt"], "VS Code")]
        };

        RuleEvaluationResult result = engine.Evaluate(path, config);

        Assert.Equal(RuleEvaluationStatus.Matched, result.Status);
        Assert.Equal(".markdown", result.NormalizedExtension);
        Assert.Equal("r1", result.WinningRule?.Id);
    }

    [Fact]
    public void UncPathsAndDriveRoot_ComputeDepthProperly()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"\\nas01\TeamShare\Specs\architecture.pdf");
        RuleEngine engine = new(fs);

        OpeningRule uncRoot = MakeRule("unc-root", "Share Root", @"\\nas01\TeamShare", [".pdf"], "Edge");
        OpeningRule uncSub = MakeRule("unc-sub", "Specs Folder", @"\\nas01\TeamShare\Specs", [".pdf"], "Acrobat");

        RuleEvaluationResult result = engine.Evaluate(
            @"\\nas01\TeamShare\Specs\architecture.pdf",
            new RuleOpenConfig { Rules = [uncRoot, uncSub] });

        Assert.Equal("unc-sub", result.WinningRule?.Id);
        Assert.Equal(1, PathNormalizer.GetFolderDepth(@"\\nas01\TeamShare"));
        Assert.Equal(2, PathNormalizer.GetFolderDepth(@"\\nas01\TeamShare\Specs"));
        Assert.Equal(1, PathNormalizer.GetFolderDepth(@"D:\"));
        Assert.Equal(2, PathNormalizer.GetFolderDepth(@"D:\Work"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\path\file.pdf")]
    [InlineData(@"D:\Work\")]
    [InlineData(@"D:\Work\*.pdf")]
    [InlineData(@"D:\")]
    public void InvalidInputs_ReturnInvalidInputStatus(string badPath)
    {
        RuleEngine engine = new(new FakeFileSystem());
        RuleEvaluationResult result = engine.Evaluate(badPath, new RuleOpenConfig());

        Assert.Equal(RuleEvaluationStatus.InvalidInput, result.Status);
        Assert.Null(result.WinningRule);
    }

    [Fact]
    public void MissingFile_ReturnsFileMissingWithDiagnosticMatch()
    {
        FakeFileSystem fs = new();
        RuleEngine engine = new(fs);

        RuleOpenConfig config = new()
        {
            Rules = [MakeRule("r1", "Work PDFs", @"D:\Work", [".pdf"], "Edge")]
        };

        RuleEvaluationResult result = engine.Evaluate(@"D:\Work\deleted.pdf", config);

        Assert.Equal(RuleEvaluationStatus.FileMissing, result.Status);
        Assert.False(result.FileExists);
        Assert.Equal("r1", result.WinningRule?.Id);
    }

    [Fact]
    public void ExtensionlessFile_ReturnsNoMatch()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\Projects\Makefile");
        RuleEngine engine = new(fs);

        RuleOpenConfig config = new()
        {
            Rules = [MakeRule("r1", "Markdown", @"D:\Projects", [".md"], "VS Code")]
        };

        RuleEvaluationResult result = engine.Evaluate(@"D:\Projects\Makefile", config);

        Assert.Equal(RuleEvaluationStatus.NoMatch, result.Status);
        Assert.Null(result.NormalizedExtension);
    }

    [Fact]
    public void FindOverlaps_DetectsExactAndNestedOverlaps()
    {
        RuleEngine engine = new(new FakeFileSystem());
        OpeningRule existingRoot = MakeRule("r1", "Work All", @"D:\Work", [".pdf", ".txt"], "Edge");
        OpeningRule existingSub = MakeRule("r2", "Work Legal", @"D:\Work\Legal", [".pdf"], "Acrobat");
        OpeningRule candidate = MakeRule("r3", "Work Sub", @"D:\Work\Contracts", [".pdf"], "SumatraPDF");

        IReadOnlyList<RuleOverlapInfo> overlaps = engine.FindOverlaps(candidate, [existingRoot, existingSub]);

        Assert.Single(overlaps);
        Assert.Equal("r1", overlaps[0].ExistingRuleId);
        Assert.Equal(RuleOverlapKind.ChildInsideParent, overlaps[0].Kind);
    }
}
