using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Dla operacji każdego modułu (prawdziwe usługi z DI): plik w folderze ze spacją i polskimi znakami → otwarcie przez
/// IDocumentManager → polecenie w sesji → wynik sprawdzony w pliku → Cofnij = identyczne bajty jak przed operacją →
/// Ponów = identyczne bajty jak po operacji → Zapisz jako (polska ścieżka) → ponowne otwarcie i sprawdzenie wyniku.
/// </summary>
public class UndoRedoAndPolishPathTests
{
    public static IEnumerable<object[]> OperationNames() => Operations.Names();

    [Theory]
    [MemberData(nameof(OperationNames))]
    public async Task Operation_result_is_in_file_undo_restores_identical_bytes_redo_and_save_to_polish_path(string name)
    {
        var op = Operations.Get(name);
        using var host = new TestHost();
        var manager = host.Documents;
        var path = TestPaths.WriteSample(op.Sample(), "zażółć gęślą jaźń.pdf");

        var session = await manager.OpenAsync(path);
        Assert.NotNull(session);
        var before = session!.Current.GetBytes();

        Assert.True(await session.ExecuteAsync(new DelegateDocumentCommand("Test." + name, (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.All, op.Permission)),
            "operacja nie zmieniła dokumentu");
        var after = session.Current.GetBytes();
        await op.Verify(host, after);
        Assert.True(session.IsModified);

        await session.History.UndoAsync();
        Assert.Equal(before, session.Current.GetBytes());
        Assert.False(session.IsModified);

        await session.History.RedoAsync();
        Assert.Equal(after, session.Current.GetBytes());

        var outDir = Path.Combine(Path.GetDirectoryName(path)!, "Wynik ąę ść");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"zapisany {name} źdźbło.pdf");
        await manager.SaveAsync(session, outPath);
        Assert.False(session.IsModified);
        Assert.Equal(outPath, session.FilePath);
        Assert.Equal(before, File.ReadAllBytes(path)); // oryginał bez zmian (Zapisz jako)
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(outDir, "*.bak"));

        var saved = File.ReadAllBytes(outPath);
        await op.Verify(host, saved);

        manager.Close(session);
        var reopened = await manager.OpenAsync(outPath);
        Assert.NotNull(reopened);
        await op.Verify(host, reopened!.Current.GetBytes());
        manager.Close(reopened);
    }

    [Fact]
    public async Task Undo_and_redo_of_a_chain_of_operations_from_every_module_restore_each_intermediate_state()
    {
        using var host = new TestHost();
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(3), "łańcuch operacji.pdf");
        var session = (await host.Documents.OpenAsync(path))!;
        var chain = new[] { "Pages.Rotate", "Pages.Watermark", "Annot.Highlight", "Edit.AddText", "Viewer.Bookmarks", "Core.Metadata", "Pages.Delete" };
        var states = new List<byte[]> { session.Current.GetBytes() };
        foreach (var n in chain)
        {
            var op = Operations.Get(n);
            Assert.True(await session.ExecuteAsync(new DelegateDocumentCommand("Test." + n, (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.All, op.Permission)), n);
            states.Add(session.Current.GetBytes());
        }
        for (int i = chain.Length - 1; i >= 0; i--)
        {
            await session.History.UndoAsync();
            Assert.Equal(states[i], session.Current.GetBytes());
        }
        Assert.False(session.History.CanUndo);
        for (int i = 1; i <= chain.Length; i++)
        {
            await session.History.RedoAsync();
            Assert.Equal(states[i], session.Current.GetBytes());
        }
        // stan końcowy: wynik ostatniej operacji (usunięcie strony) i wcześniejsze zmiany obecne
        var final = session.Current.GetBytes();
        Assert.Equal(2, Inspect.PageCount(final));
        Assert.Contains("Dodanytekstźdźbło", Inspect.Squash(Inspect.PigText(final)));
    }

    [Fact]
    public async Task Save_over_existing_file_in_polish_folder_replaces_it_atomically_without_leftovers()
    {
        using var host = new TestHost();
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(2), "nadpisz mnie ąę.pdf");
        var dir = Path.GetDirectoryName(path)!;
        var session = (await host.Documents.OpenAsync(path))!;
        var op = Operations.Get("Pages.InsertBlank");
        await session.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.All, op.Permission));
        await host.Documents.SaveAsync(session);
        Assert.Equal(4, Inspect.PageCount(File.ReadAllBytes(path)));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir));
        Assert.Contains(path, host.Get<IRecentFilesService>().Files);
    }
}
