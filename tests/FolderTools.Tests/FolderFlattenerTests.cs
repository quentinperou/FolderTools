using FolderTools.Core;

namespace FolderTools.Tests;

public class FolderFlattenerTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string P(string relative) => Path.Combine(_tree.Root, relative);

    private string Text(string relative, string content)
    {
        string path = _tree.File(relative);
        File.WriteAllText(path, content);
        return path;
    }

    private static FolderFlattener Flattener => new(new EmptyFolderCleaner(["Thumbs.db", "desktop.ini"], [".git"]));

    private FlattenResult Run(FlattenDepth depth = FlattenDepth.AllLevels, FlattenNaming naming = FlattenNaming.Suffix,
        bool recycleIdentical = true, List<string>? recycled = null)
    {
        var plan = Flattener.Plan(_tree.Root, depth, naming);
        var recycle = recycleIdentical ? plan.Identical.ToHashSet() : [];
        var targets = FolderFlattener.Resolve(plan, recycle);
        return Flattener.Execute(plan, targets, paths =>
        {
            foreach (string path in paths)
            {
                recycled?.Add(path);
                File.Delete(path); // stands for the recycle bin
            }
        });
    }

    [Fact]
    public void AllLevels_BringsEveryFileUp_AndRemovesTheEmptiedFolders()
    {
        Text(@"Compta\budget.xlsx", "b");
        Text(@"Photos\Noël\IMG_001.jpg", "n");
        Text(@"Photos\Vacances\Plage\IMG_002.jpg", "p");
        _tree.File(@"Photos\Thumbs.db");
        _tree.Dir(@"Photos\Empty");
        Text("already.txt", "a");

        var result = Run();

        Assert.Empty(result.Failures);
        Assert.Equal(3, result.Moves.Count);
        foreach (string name in new[] { "budget.xlsx", "IMG_001.jpg", "IMG_002.jpg", "already.txt" })
            Assert.True(File.Exists(P(name)), name);
        Assert.False(Directory.Exists(P("Compta")));
        Assert.False(Directory.Exists(P("Photos")));
        Assert.Equal([P("Compta"), P("Photos")], result.RemovedFolders);
    }

    [Fact]
    public void NameTaken_GetsASuffix_AndNeverOverwrites()
    {
        Text("contrat.docx", "root");
        Text(@"Clients\Dupont\contrat.docx", "dupont");
        Text(@"Clients\Martin\contrat.docx", "martin");

        Run();

        Assert.Equal("root", File.ReadAllText(P("contrat.docx")));
        Assert.Equal("dupont", File.ReadAllText(P("contrat (2).docx")));
        Assert.Equal("martin", File.ReadAllText(P("contrat (3).docx")));
    }

    [Fact]
    public void Prefix_OnlyWhenTheNameIsTaken()
    {
        Text(@"Photos\Vacances\IMG_001.jpg", "v");
        Text(@"Photos\Noël\IMG_001.jpg", "n");
        Text(@"Photos\Noël\IMG_002.jpg", "2");

        Run(naming: FlattenNaming.Prefix);

        // "Noël" comes before "Vacances": its file keeps the name.
        Assert.Equal("n", File.ReadAllText(P("IMG_001.jpg")));
        Assert.Equal("v", File.ReadAllText(P("Photos - Vacances - IMG_001.jpg")));
        Assert.True(File.Exists(P("IMG_002.jpg")));
    }

    [Fact]
    public void IdenticalFiles_AreFound_AndCanGoToTheRecycleBin()
    {
        Text(@"Clients\Dupont\devis.pdf", "same");
        Text(@"Archives\2024\devis.pdf", "same");
        Text(@"Site\img\logo.png", "one");
        Text(@"Charte\logo.png", "two");

        var plan = Flattener.Plan(_tree.Root, FlattenDepth.AllLevels, FlattenNaming.Suffix);
        var identical = Assert.Single(plan.Identical);
        Assert.Equal(P(@"Clients\Dupont\devis.pdf"), identical.Path);
        Assert.Equal(P(@"Archives\2024\devis.pdf"), identical.IdenticalTo);

        var recycled = new List<string>();
        var result = Run(recycled: recycled);

        Assert.Equal([P(@"Clients\Dupont\devis.pdf")], recycled);
        Assert.Equal([P(@"Clients\Dupont\devis.pdf")], result.Recycled);
        Assert.True(File.Exists(P("devis.pdf")));
        Assert.False(File.Exists(P("devis (2).pdf")));
        Assert.True(File.Exists(P("logo (2).png")));
    }

    [Fact]
    public void IdenticalFiles_KeptBoth_WhenTheUserSaysSo()
    {
        Text(@"A\devis.pdf", "same");
        Text(@"B\devis.pdf", "same");

        Run(recycleIdentical: false);

        Assert.True(File.Exists(P("devis.pdf")));
        Assert.True(File.Exists(P("devis (2).pdf")));
    }

    [Fact]
    public void OneLevel_MovesFilesAndSubfoldersUpOneLevel()
    {
        Text(@"Clients\liste.csv", "l");
        Text(@"Clients\Dupont\contrat.docx", "d");
        Text(@"Clients\Clients\notes.txt", "c");

        var result = Run(FlattenDepth.OneLevel);

        Assert.Empty(result.Failures);
        Assert.True(File.Exists(P("liste.csv")));
        Assert.True(File.Exists(P(@"Dupont\contrat.docx")));
        // A folder named like the one being emptied: that name is still taken while the items move.
        Assert.True(File.Exists(P(@"Clients (2)\notes.txt")));
        Assert.False(Directory.Exists(P("Clients")));
    }

    [Fact]
    public void IgnoredFoldersAndLinks_AreNeverTouched()
    {
        Text(@"Repo\.git\HEAD", "ref");
        Text(@"Repo\readme.md", "r");
        string target = _tree.Dir("Elsewhere");
        Text(@"Elsewhere\inside.txt", "i");
        _tree.Junction(@"Linked\Shortcut", target);
        Text(@"Linked\file.txt", "f");

        var result = Run();

        Assert.True(File.Exists(P(@"Repo\.git\HEAD")));
        Assert.True(File.Exists(P("readme.md")));
        Assert.True(Directory.Exists(P("Repo")));
        Assert.True(Directory.Exists(P(@"Linked\Shortcut")));
        Assert.True(File.Exists(P("inside.txt"))); // "Elsewhere" is an ordinary subfolder of the root
        Assert.DoesNotContain(result.Moves, m => m.From.Contains("Shortcut"));
    }

    [Fact]
    public void Plan_OnlyCountsTheSubfoldersThatWillBeEmptied()
    {
        Text(@"Repo\.git\HEAD", "ref");
        Text(@"Repo\readme.md", "r");
        Text(@"Docs\Old\a.txt", "a");
        _tree.Dir(@"Empty\Nothing");

        foreach (var depth in new[] { FlattenDepth.AllLevels, FlattenDepth.OneLevel })
        {
            var plan = Flattener.Plan(_tree.Root, depth, FlattenNaming.Suffix);
            Assert.Equal([P("Docs"), P("Empty")], plan.EmptiedFolders);
        }
    }

    [Fact]
    public void Undo_PutsEverythingBack()
    {
        Text(@"Photos\Noël\IMG_001.jpg", "n");
        Text(@"Photos\Vacances\IMG_001.jpg", "v");
        _tree.Dir(@"Docs");
        Text(@"Docs\a.txt", "a");

        var result = Run();
        var failures = FolderFlattener.Undo(result);

        Assert.Empty(failures);
        Assert.Equal("n", File.ReadAllText(P(@"Photos\Noël\IMG_001.jpg")));
        Assert.Equal("v", File.ReadAllText(P(@"Photos\Vacances\IMG_001.jpg")));
        Assert.True(File.Exists(P(@"Docs\a.txt")));
        Assert.False(File.Exists(P("IMG_001.jpg")));
        Assert.False(File.Exists(P("a.txt")));
    }

    [Fact]
    public void Undo_OneLevel_MovesTheFoldersBack()
    {
        Text(@"Clients\Dupont\contrat.docx", "d");

        var result = Run(FlattenDepth.OneLevel);
        FolderFlattener.Undo(result);

        Assert.True(File.Exists(P(@"Clients\Dupont\contrat.docx")));
        Assert.False(Directory.Exists(P("Dupont")));
    }

    [Fact]
    public void FolderThatKeepsAFile_IsNotRemoved()
    {
        string locked = Text(@"Busy\open.txt", "x");
        Text(@"Busy\free.txt", "y");

        FlattenResult result;
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            result = Run();

        Assert.Single(result.Failures);
        Assert.True(File.Exists(P("free.txt")));
        Assert.True(File.Exists(P(@"Busy\open.txt")));
        Assert.Empty(result.RemovedFolders);
    }
}
