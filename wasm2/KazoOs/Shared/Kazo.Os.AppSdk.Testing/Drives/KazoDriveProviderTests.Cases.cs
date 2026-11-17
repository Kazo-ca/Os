using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.AppSdk.Testing.Drives;

/// <summary>The names of the cases of <see cref="KazoDriveProviderTests"/>.</summary>
public static class KazoDriveConformanceCases
{
    /// <summary>The root of the drive is a folder.</summary>
    public const string RootIsAFolder = "root-is-a-folder";

    /// <summary>A file written is read back, described and listed as written.</summary>
    public const string WriteThenRead = "write-then-read";

    /// <summary>An empty file is created and read back empty.</summary>
    public const string EmptyFile = "empty-file";

    /// <summary>A second write replaces the content.</summary>
    public const string Overwrite = "overwrite";

    /// <summary>A folder is created, listed, and holds what is written in it.</summary>
    public const string CreateFolderAndList = "create-folder-and-list";

    /// <summary>Creating a folder or a file whose name is taken throws <see cref="KazoDriveErrorKind.Exists"/>.</summary>
    public const string CreateExisting = "create-existing";

    /// <summary>Stat, list, read and delete of a missing path throw <see cref="KazoDriveErrorKind.NotFound"/>.</summary>
    public const string Missing = "missing";

    /// <summary>Listing a file or reading a folder throws <see cref="KazoDriveErrorKind.TypeMismatch"/>.</summary>
    public const string WrongKind = "wrong-kind";

    /// <summary>Delete of a file, of an empty folder, of a full folder with and without recursion.</summary>
    public const string Delete = "delete";

    /// <summary>A conditional write of a file another device changed throws <see cref="KazoDriveErrorKind.RevisionConflict"/>.</summary>
    public const string RevisionConflict = "revision-conflict";

    /// <summary>A move the service does in one call leaves nothing behind.</summary>
    public const string Move = "move";

    /// <summary>The quota, when the service gives one, is coherent.</summary>
    public const string Quota = "quota";

    /// <summary>Names with spaces, accents, <c>%</c>, <c>#</c>, <c>?</c> and other characters round-trip, or are refused with <see cref="KazoDriveErrorKind.InvalidName"/>.</summary>
    public const string Names = "names";

    /// <summary>Without the network: opening never reaches the service, the state is unavailable, operations throw <see cref="KazoDriveErrorKind.Unavailable"/>.</summary>
    public const string Offline = "offline";

    /// <summary>When the service refuses the sign-in: the state and operations say <see cref="KazoDriveErrorKind.SignInRequired"/>.</summary>
    public const string SignInExpired = "sign-in-expired";

    /// <summary>A file larger than <see cref="KazoDriveLimits.MaxFileBytes"/> stays listed but is not read.</summary>
    public const string FileTooLarge = "file-too-large";
}

public abstract partial class KazoDriveProviderTests
{
    private async Task<string?> RootIsAFolderAsync(CaseScope scope)
    {
        KazoDriveEntry root = await scope.Drive.StatAsync(KazoDrivePath.Root, scope.Token).ConfigureAwait(false);
        scope.Expect(root.Kind == KazoDriveEntryKind.Folder, "StatAsync(Root) must describe a folder.");
        KazoDriveState state = await scope.Drive.GetStateAsync(scope.Token).ConfigureAwait(false);
        scope.Expect(state == KazoDriveState.Ready, $"GetStateAsync must answer Ready when the service answers, it answered {state}.");
        return null;
    }

    private async Task<string?> WriteThenReadAsync(CaseScope scope)
    {
        KazoDrivePath file = scope.In("hello.txt");
        byte[] content = Bytes("Bonjour, Kazo OS.\n");
        await scope.Drive.WriteAsync(file, content, ifRevision: null, scope.Token).ConfigureAwait(false);

        byte[] read = await scope.Drive.ReadAsync(file, scope.Token).ConfigureAwait(false);
        scope.Expect(read.AsSpan().SequenceEqual(content), "ReadAsync must return the bytes WriteAsync stored.");

        KazoDriveEntry entry = await scope.Drive.StatAsync(file, scope.Token).ConfigureAwait(false);
        scope.Expect(entry.Kind == KazoDriveEntryKind.File, "StatAsync of a file must describe a file.");
        scope.Expect(entry.Name == "hello.txt", $"StatAsync must name the entry 'hello.txt', it named it '{entry.Name}'.");
        scope.Expect(entry.Length == content.Length, $"StatAsync must give the length {content.Length}, it gave {entry.Length}.");

        IReadOnlyList<KazoDriveEntry> entries = await scope.Drive.ListAsync(scope.Folder, scope.Token).ConfigureAwait(false);
        scope.Expect(entries.Count == 1 && entries[0].Name == "hello.txt" && entries[0].Kind == KazoDriveEntryKind.File,
            "ListAsync of the folder must list the one file written in it, by its name.");
        return null;
    }

    private async Task<string?> EmptyFileAsync(CaseScope scope)
    {
        KazoDrivePath written = scope.In("written-empty");
        await scope.Drive.WriteAsync(written, ReadOnlyMemory<byte>.Empty, ifRevision: null, scope.Token).ConfigureAwait(false);
        byte[] read = await scope.Drive.ReadAsync(written, scope.Token).ConfigureAwait(false);
        scope.Expect(read.Length == 0, "An empty file must read back empty.");

        KazoDrivePath created = scope.In("created-empty");
        await scope.Drive.CreateFileAsync(created, scope.Token).ConfigureAwait(false);
        KazoDriveEntry entry = await scope.Drive.StatAsync(created, scope.Token).ConfigureAwait(false);
        scope.Expect(entry.Kind == KazoDriveEntryKind.File && entry.Length == 0, "CreateFileAsync must create an empty file.");
        return null;
    }

    private async Task<string?> OverwriteAsync(CaseScope scope)
    {
        KazoDrivePath file = scope.In("notes.txt");
        await scope.Drive.WriteAsync(file, Bytes("first version, longer"), ifRevision: null, scope.Token).ConfigureAwait(false);
        await scope.Drive.WriteAsync(file, Bytes("second"), ifRevision: null, scope.Token).ConfigureAwait(false);
        byte[] read = await scope.Drive.ReadAsync(file, scope.Token).ConfigureAwait(false);
        scope.Expect(read.AsSpan().SequenceEqual(Bytes("second")), "A second WriteAsync must replace the whole content.");
        return null;
    }

    private async Task<string?> CreateFolderAndListAsync(CaseScope scope)
    {
        KazoDrivePath folder = scope.In("Photos");
        await scope.Drive.CreateFolderAsync(folder, scope.Token).ConfigureAwait(false);
        KazoDriveEntry entry = await scope.Drive.StatAsync(folder, scope.Token).ConfigureAwait(false);
        scope.Expect(entry.Kind == KazoDriveEntryKind.Folder, "StatAsync of a new folder must describe a folder.");

        IReadOnlyList<KazoDriveEntry> empty = await scope.Drive.ListAsync(folder, scope.Token).ConfigureAwait(false);
        scope.Expect(empty.Count == 0, "A new folder must list no entry.");

        await scope.Drive.WriteAsync(folder.Append("a.txt"), Bytes("a"), ifRevision: null, scope.Token).ConfigureAwait(false);
        await scope.Drive.CreateFolderAsync(folder.Append("2026"), scope.Token).ConfigureAwait(false);
        IReadOnlyList<KazoDriveEntry> entries = await scope.Drive.ListAsync(folder, scope.Token).ConfigureAwait(false);
        string[] names = [.. entries.Select(static e => e.Name).Order(StringComparer.Ordinal)];
        scope.Expect(names.SequenceEqual(["2026", "a.txt"]), $"ListAsync must list '2026' and 'a.txt', it listed [{string.Join(", ", names)}].");
        scope.Expect(entries.Single(static e => e.Name == "2026").Kind == KazoDriveEntryKind.Folder, "A listed folder must be a folder.");

        IReadOnlyList<KazoDriveEntry> parent = await scope.Drive.ListAsync(scope.Folder, scope.Token).ConfigureAwait(false);
        scope.Expect(parent.Count == 1 && parent[0].Name == "Photos", "ListAsync must list only the direct children of a folder.");
        return null;
    }

    private async Task<string?> CreateExistingAsync(CaseScope scope)
    {
        KazoDrivePath folder = scope.In("taken");
        await scope.Drive.CreateFolderAsync(folder, scope.Token).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.Exists, "CreateFolderAsync of a folder that exists",
            () => scope.Drive.CreateFolderAsync(folder, scope.Token).AsTask()).ConfigureAwait(false);

        KazoDrivePath file = scope.In("taken.txt");
        await scope.Drive.WriteAsync(file, Bytes("x"), ifRevision: null, scope.Token).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.Exists, "CreateFileAsync of a file that exists",
            () => scope.Drive.CreateFileAsync(file, scope.Token).AsTask()).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.Exists, "CreateFolderAsync where a file is",
            () => scope.Drive.CreateFolderAsync(file, scope.Token).AsTask()).ConfigureAwait(false);

        byte[] read = await scope.Drive.ReadAsync(file, scope.Token).ConfigureAwait(false);
        scope.Expect(read.AsSpan().SequenceEqual(Bytes("x")), "A refused creation must leave the existing file as it was.");
        return null;
    }

    private async Task<string?> MissingAsync(CaseScope scope)
    {
        KazoDrivePath missing = scope.In("nothing-here");
        await scope.ExpectErrorAsync(KazoDriveErrorKind.NotFound, "StatAsync of a missing path",
            () => scope.Drive.StatAsync(missing, scope.Token).AsTask()).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.NotFound, "ReadAsync of a missing file",
            () => scope.Drive.ReadAsync(missing, scope.Token).AsTask()).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.NotFound, "ListAsync of a missing folder",
            () => scope.Drive.ListAsync(missing, scope.Token).AsTask()).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.NotFound, "DeleteAsync of a missing path",
            () => scope.Drive.DeleteAsync(missing, recursive: false, scope.Token).AsTask()).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.NotFound, "WriteAsync in a missing folder",
            () => scope.Drive.WriteAsync(missing.Append("file.txt"), Bytes("x"), null, scope.Token).AsTask()).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> WrongKindAsync(CaseScope scope)
    {
        KazoDrivePath file = scope.In("file.txt");
        KazoDrivePath folder = scope.In("folder");
        await scope.Drive.WriteAsync(file, Bytes("x"), ifRevision: null, scope.Token).ConfigureAwait(false);
        await scope.Drive.CreateFolderAsync(folder, scope.Token).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.TypeMismatch, "ListAsync of a file",
            () => scope.Drive.ListAsync(file, scope.Token).AsTask()).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.TypeMismatch, "ReadAsync of a folder",
            () => scope.Drive.ReadAsync(folder, scope.Token).AsTask()).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> DeleteAsync(CaseScope scope)
    {
        KazoDrivePath file = scope.In("gone.txt");
        await scope.Drive.WriteAsync(file, Bytes("x"), ifRevision: null, scope.Token).ConfigureAwait(false);
        await scope.Drive.DeleteAsync(file, recursive: false, scope.Token).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.NotFound, "StatAsync of a deleted file",
            () => scope.Drive.StatAsync(file, scope.Token).AsTask()).ConfigureAwait(false);

        KazoDrivePath empty = scope.In("empty");
        await scope.Drive.CreateFolderAsync(empty, scope.Token).ConfigureAwait(false);
        await scope.Drive.DeleteAsync(empty, recursive: false, scope.Token).ConfigureAwait(false);

        KazoDrivePath full = scope.In("full");
        await scope.Drive.CreateFolderAsync(full, scope.Token).ConfigureAwait(false);
        await scope.Drive.CreateFolderAsync(full.Append("inner"), scope.Token).ConfigureAwait(false);
        await scope.Drive.WriteAsync(full.Append("inner").Append("deep.txt"), Bytes("x"), null, scope.Token).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.NotEmpty, "DeleteAsync without recursion of a folder that has entries",
            () => scope.Drive.DeleteAsync(full, recursive: false, scope.Token).AsTask()).ConfigureAwait(false);
        await scope.Drive.StatAsync(full.Append("inner").Append("deep.txt"), scope.Token).ConfigureAwait(false);

        await scope.Drive.DeleteAsync(full, recursive: true, scope.Token).ConfigureAwait(false);
        IReadOnlyList<KazoDriveEntry> left = await scope.Drive.ListAsync(scope.Folder, scope.Token).ConfigureAwait(false);
        scope.Expect(left.Count == 0, $"After the deletes the folder must be empty, it lists [{string.Join(", ", left.Select(static e => e.Name))}].");
        return null;
    }

    private async Task<string?> RevisionConflictAsync(CaseScope scope)
    {
        KazoDrivePath file = scope.In("shared.txt");
        await scope.Drive.WriteAsync(file, Bytes("v1"), ifRevision: null, scope.Token).ConfigureAwait(false);
        string? first = (await scope.Drive.StatAsync(file, scope.Token).ConfigureAwait(false)).Revision;
        if (first is null)
        {
            return scope.Skip("The service keeps no revision (KazoDriveEntry.Revision is null): conditional writes are not checked.");
        }

        // Another device changes the file.
        KazoDriveFolder other = await scope.OpenAsync().ConfigureAwait(false);
        await other.WriteAsync(file, Bytes("v2 from another device"), ifRevision: null, scope.Token).ConfigureAwait(false);
        string? second = (await scope.Drive.StatAsync(file, scope.Token).ConfigureAwait(false)).Revision;
        scope.Expect(second is not null && second != first, "A write must change the file's revision.");

        await scope.ExpectErrorAsync(KazoDriveErrorKind.RevisionConflict, "WriteAsync with a revision another device replaced",
            () => scope.Drive.WriteAsync(file, Bytes("v3 lost"), first, scope.Token).AsTask()).ConfigureAwait(false);
        byte[] kept = await scope.Drive.ReadAsync(file, scope.Token).ConfigureAwait(false);
        scope.Expect(kept.AsSpan().SequenceEqual(Bytes("v2 from another device")), "A refused conditional write must leave the other device's content.");

        await scope.Drive.WriteAsync(file, Bytes("v3"), second, scope.Token).ConfigureAwait(false);
        byte[] read = await scope.Drive.ReadAsync(file, scope.Token).ConfigureAwait(false);
        scope.Expect(read.AsSpan().SequenceEqual(Bytes("v3")), "WriteAsync with the current revision must write.");
        return null;
    }

    private async Task<string?> MoveAsync(CaseScope scope)
    {
        KazoDrivePath from = scope.In("before.txt");
        KazoDrivePath to = scope.In("after.txt");
        await scope.Drive.WriteAsync(from, Bytes("moved"), ifRevision: null, scope.Token).ConfigureAwait(false);
        if (!await scope.Drive.TryMoveAsync(from, to, scope.Token).ConfigureAwait(false))
        {
            return "TryMoveAsync answers false: the OS copies then deletes.";
        }

        await scope.ExpectErrorAsync(KazoDriveErrorKind.NotFound, "StatAsync of a moved file's old path",
            () => scope.Drive.StatAsync(from, scope.Token).AsTask()).ConfigureAwait(false);
        byte[] read = await scope.Drive.ReadAsync(to, scope.Token).ConfigureAwait(false);
        scope.Expect(read.AsSpan().SequenceEqual(Bytes("moved")), "A moved file must keep its content.");

        KazoDrivePath folder = scope.In("old-folder");
        await scope.Drive.CreateFolderAsync(folder, scope.Token).ConfigureAwait(false);
        await scope.Drive.WriteAsync(folder.Append("inside.txt"), Bytes("in"), null, scope.Token).ConfigureAwait(false);
        KazoDrivePath renamed = scope.In("new-folder");
        scope.Expect(await scope.Drive.TryMoveAsync(folder, renamed, scope.Token).ConfigureAwait(false),
            "A provider that moves files must also move folders, or answer false for both.");
        byte[] inside = await scope.Drive.ReadAsync(renamed.Append("inside.txt"), scope.Token).ConfigureAwait(false);
        scope.Expect(inside.AsSpan().SequenceEqual(Bytes("in")), "A moved folder must keep its entries.");
        return null;
    }

    private async Task<string?> QuotaAsync(CaseScope scope)
    {
        KazoDriveQuota? quota = await scope.Drive.GetQuotaAsync(scope.Token).ConfigureAwait(false);
        if (quota is null)
        {
            return "GetQuotaAsync answers null: df shows '-'.";
        }

        scope.Expect(quota.TotalBytes >= 0 && quota.FreeBytes >= 0 && quota.FreeBytes <= quota.TotalBytes,
            $"The quota must have 0 <= free <= total, it has total {quota.TotalBytes} and free {quota.FreeBytes}.");
        return null;
    }

    private async Task<string?> NamesAsync(CaseScope scope)
    {
        string[] names =
        [
            "espace et accents é à ç.txt", "100% #1?.txt", "a+b&c=d;e.txt", "crochets [x] {y}.txt",
            "apostrophe l'été.txt", "日本語のファイル.txt", "emoji 🙂.txt", ".cache", "tilde~$.txt",
        ];
        List<string> refused = [];
        foreach (string name in names)
        {
            KazoDrivePath path = scope.In(name);
            byte[] content = Bytes(name);
            try
            {
                await scope.Drive.WriteAsync(path, content, ifRevision: null, scope.Token).ConfigureAwait(false);
            }
            catch (KazoDriveException exception) when (exception.Kind == KazoDriveErrorKind.InvalidName)
            {
                refused.Add(name);
                continue;
            }

            byte[] read = await scope.Drive.ReadAsync(path, scope.Token).ConfigureAwait(false);
            scope.Expect(read.AsSpan().SequenceEqual(content), $"The file '{name}' must read back what was written in it (a name escaped wrongly reaches another file).");
        }

        IReadOnlyList<KazoDriveEntry> listed = await scope.Drive.ListAsync(scope.Folder, scope.Token).ConfigureAwait(false);
        string[] expected = [.. names.Except(refused).Order(StringComparer.Ordinal)];
        string[] actual = [.. listed.Select(static e => e.Name).Order(StringComparer.Ordinal)];
        scope.Expect(actual.SequenceEqual(expected, StringComparer.Ordinal),
            $"ListAsync must give each name exactly as written; expected [{string.Join(" | ", expected)}], got [{string.Join(" | ", actual)}].");
        return refused.Count == 0 ? null : $"The service refuses: {string.Join(", ", refused)}.";
    }

    private async Task<string?> OfflineAsync(CaseScope scope)
    {
        if (!await SetServiceReachableAsync(false, scope.Token).ConfigureAwait(false))
        {
            return scope.Skip("The test class does not override SetServiceReachableAsync.");
        }

        KazoDriveFolder opened;
        try
        {
            opened = await scope.OpenAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not KazoDriveConformanceException)
        {
            throw new KazoDriveConformanceException(scope.Name,
                "OpenAsync must not reach the service (the OS calls it at login, which never waits on the network), it failed offline.", exception);
        }

        KazoDriveState state = await opened.GetStateAsync(scope.Token).ConfigureAwait(false);
        scope.Expect(state == KazoDriveState.Unavailable, $"GetStateAsync must answer Unavailable without the network, it answered {state}.");
        await scope.ExpectErrorAsync(KazoDriveErrorKind.Unavailable, "StatAsync without the network",
            () => opened.StatAsync(KazoDrivePath.Root, scope.Token).AsTask()).ConfigureAwait(false);
        await scope.ExpectErrorAsync(KazoDriveErrorKind.Unavailable, "WriteAsync without the network",
            () => scope.Drive.WriteAsync(scope.In("offline.txt"), Bytes("x"), null, scope.Token).AsTask()).ConfigureAwait(false);

        await SetServiceReachableAsync(true, scope.Token).ConfigureAwait(false);
        KazoDriveState back = await opened.GetStateAsync(scope.Token).ConfigureAwait(false);
        scope.Expect(back == KazoDriveState.Ready, $"The same drive must answer Ready once the network is back, it answered {back}.");
        return null;
    }

    private async Task<string?> SignInExpiredAsync(CaseScope scope)
    {
        if (!await ExpireSignInAsync(scope.Token).ConfigureAwait(false))
        {
            return scope.Skip("The test class does not override ExpireSignInAsync.");
        }

        KazoDriveState state = await scope.Drive.GetStateAsync(scope.Token).ConfigureAwait(false);
        scope.Expect(state == KazoDriveState.SignInRequired, $"GetStateAsync must answer SignInRequired when the service refuses the sign-in, it answered {state}.");
        await scope.ExpectErrorAsync(KazoDriveErrorKind.SignInRequired, "ListAsync with a refused sign-in",
            () => scope.Drive.ListAsync(scope.Folder, scope.Token).AsTask()).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> FileTooLargeAsync(CaseScope scope)
    {
        KazoDrivePath file = scope.In("huge.bin");
        long length = KazoDriveLimits.MaxFileBytes + 1;
        if (!await PlaceLargeFileAsync(file, length, scope.Token).ConfigureAwait(false))
        {
            return scope.Skip("The test class does not override PlaceLargeFileAsync.");
        }

        KazoDriveEntry entry = await scope.Drive.StatAsync(file, scope.Token).ConfigureAwait(false);
        scope.Expect(entry.Length == length, $"StatAsync must give a large file's real length ({length}), it gave {entry.Length}.");
        IReadOnlyList<KazoDriveEntry> listed = await scope.Drive.ListAsync(scope.Folder, scope.Token).ConfigureAwait(false);
        scope.Expect(listed.Any(static e => e.Name == "huge.bin"), "A file larger than the limit must stay listed.");
        await scope.ExpectErrorAsync(KazoDriveErrorKind.FileTooLarge, "ReadAsync of a file larger than KazoDriveLimits.MaxFileBytes",
            () => scope.Drive.ReadAsync(file, scope.Token).AsTask()).ConfigureAwait(false);
        return null;
    }
}
