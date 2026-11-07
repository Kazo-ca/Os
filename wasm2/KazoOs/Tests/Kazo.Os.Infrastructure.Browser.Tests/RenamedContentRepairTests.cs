using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kazo.Os.Infrastructure.Browser.Rename;
using Kazo.Os.Infrastructure.Browser.Schema;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>
/// The repair of the files the rename migration of 3.0.0 to 3.2.0 left pointing at deleted content
/// (<c>doc/en/wasm/dev/rename-migration.md</c>).
/// </summary>
public sealed class RenamedContentRepairTests
{
    private const string Root = "00000000000000000000000000000001";
    private const string OldPermissions = "# /etc/hackeros/app-permissions.config\n[decisions]\norg.hackeros.webamp:filesystem.user-home.read=allow\n";
    private const string NewPermissions = "# /etc/kazoos/app-permissions.config\n[decisions]\norg.kazoos.webamp:filesystem.user-home.read=allow\n";

    [Fact]
    public void A_file_left_on_its_deleted_old_content_points_again_at_its_rewritten_text()
    {
        Dictionary<string, IReadOnlyList<JsonObject>> stores = DamagedDatabase(withRewrittenText: true);

        RenamedContentRepairPlan plan = RenamedContentRepair.Plan(stores);

        JsonObject entry = Assert.Single(plan.Entries);
        Assert.Equal(Hash(NewPermissions), (string?)entry["contentHash"]);
        Assert.Equal(Encoding.UTF8.GetByteCount(NewPermissions), (long?)entry["length"]);
        Assert.Equal(8, (long?)entry["revision"]);
        Assert.Equal(new RenamedContentRepairItem("/etc/kazoos/app-permissions.config", Recovered: true), Assert.Single(plan.Items));
    }

    [Fact]
    public void A_file_whose_rewritten_text_is_gone_is_emptied()
    {
        Dictionary<string, IReadOnlyList<JsonObject>> stores = DamagedDatabase(withRewrittenText: false);

        RenamedContentRepairPlan plan = RenamedContentRepair.Plan(stores);

        JsonObject entry = Assert.Single(plan.Entries);
        Assert.Null(entry["contentHash"]);
        Assert.Equal(0, (long?)entry["length"]);
        Assert.False(Assert.Single(plan.Items).Recovered);
    }

    [Fact]
    public void A_text_that_only_looks_like_the_rewrite_is_never_used()
    {
        Dictionary<string, IReadOnlyList<JsonObject>> stores = DamagedDatabase(withRewrittenText: false);
        // Same length and the same new names, but not the bytes the entry recorded: no proof.
        string impostor = NewPermissions.Replace("allow", "deny!", StringComparison.Ordinal);
        stores[KazoOsIndexedDbSchema.FileContentStoreName] = [.. stores[KazoOsIndexedDbSchema.FileContentStoreName], Chunk(impostor)];

        RenamedContentRepairPlan plan = RenamedContentRepair.Plan(stores);

        Assert.Null(Assert.Single(plan.Entries)["contentHash"]);
    }

    [Fact]
    public void A_sound_database_needs_no_repair()
    {
        Dictionary<string, IReadOnlyList<JsonObject>> stores = DamagedDatabase(withRewrittenText: true);
        stores[KazoOsIndexedDbSchema.FileContentStoreName] = [.. stores[KazoOsIndexedDbSchema.FileContentStoreName], Chunk(OldPermissions)];

        RenamedContentRepairPlan plan = RenamedContentRepair.Plan(stores);

        Assert.Empty(plan.Entries);
        Assert.False(RenamedContentRepair.HasLostContent(
            stores[KazoOsIndexedDbSchema.FileSystemEntryStoreName],
            new HashSet<string>([Hash(OldPermissions)], StringComparer.Ordinal)));
    }

    [Fact]
    public void Every_file_the_old_migration_damaged_is_recovered()
    {
        // What 3.0.0 to 3.2.0 left in the browser: every change written except the new hash of the
        // rewritten configuration files, whose old chunks were deleted.
        Dictionary<string, IReadOnlyList<JsonObject>> stored = RenamedOsDatabaseRewriterTests.SmallDatabase();
        stored[KazoOsIndexedDbSchema.FileSystemEntryStoreName] = [.. stored[KazoOsIndexedDbSchema.FileSystemEntryStoreName].Where(entry => (string?)entry["id"] != "copy")];
        RenamedOsDatabase rewritten = RenamedOsDatabaseRewriter.Rewrite(stored);
        Dictionary<string, IReadOnlyList<JsonObject>> damaged = RenamedOsDatabaseRewriterTests.ApplyChanges(stored, rewritten.Changes);
        damaged[KazoOsIndexedDbSchema.FileSystemEntryStoreName] = [.. rewritten.Stores[KazoOsIndexedDbSchema.FileSystemEntryStoreName]
            .Select(entry => WithOldContent(entry, stored))];

        RenamedContentRepairPlan plan = RenamedContentRepair.Plan(damaged);

        Assert.Equal(new RenamedContentRepairItem("/etc/kazoos/apps.config", Recovered: true), Assert.Single(plan.Items));
        JsonObject expected = rewritten.Stores[KazoOsIndexedDbSchema.FileSystemEntryStoreName].Single(entry => (string?)entry["id"] == "config");
        JsonObject repaired = Assert.Single(plan.Entries);
        Assert.Equal((string?)expected["contentHash"], (string?)repaired["contentHash"]);
        Assert.Equal(expected["length"]!.ToJsonString(), repaired["length"]!.ToJsonString());
    }

    [Fact]
    public async Task The_repair_runs_once_per_browser_and_only_over_an_existing_database()
    {
        RenamedStorageMigrationTests.FakeBrowser fresh = new([]);
        RenamedStorageMigrationTests.FakeBrowser upgraded = new([KazoOsIndexedDbSchema.DatabaseName])
        {
            RepairResult = [new RenamedContentRepairItem("/etc/kazoos/app-permissions.config", true)],
        };

        IReadOnlyList<RenamedContentRepairItem> none = await RenamedContentRepair.RunAsync(fresh, CancellationToken.None);
        IReadOnlyList<RenamedContentRepairItem> first = await RenamedContentRepair.RunAsync(upgraded, CancellationToken.None);
        IReadOnlyList<RenamedContentRepairItem> second = await RenamedContentRepair.RunAsync(upgraded, CancellationToken.None);

        Assert.Empty(none);
        Assert.Equal(0, fresh.Repairs);
        Assert.True(fresh.Local.ContainsKey(RenamedContentRepair.DoneKey));
        Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal(1, upgraded.Repairs);
    }

    /// <summary>
    /// The entry as 3.0.0 to 3.2.0 left it: the other values renamed, but the content hash and length
    /// of the text before the rename.
    /// </summary>
    private static JsonObject WithOldContent(JsonObject entry, Dictionary<string, IReadOnlyList<JsonObject>> stored)
    {
        JsonObject before = stored[KazoOsIndexedDbSchema.FileSystemEntryStoreName].Single(old => (string?)old["id"] == (string?)entry["id"]);
        JsonObject damaged = entry.DeepClone().AsObject();
        damaged["contentHash"] = before["contentHash"]?.DeepClone();
        damaged["length"] = before["length"]?.DeepClone();
        return damaged;
    }

    /// <summary>
    /// <c>/etc/kazoos/app-permissions.config</c> after the old migration: its entry still records the
    /// old text's hash and length, whose chunks are deleted; the rewritten text is stored, unreferenced.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<JsonObject>> DamagedDatabase(bool withRewrittenText) => new(StringComparer.Ordinal)
    {
        [KazoOsIndexedDbSchema.FileSystemLinkStoreName] =
        [
            Link(Root, "etc", "etc-id"),
            Link("etc-id", "kazoos", "etc-kazoos-id"),
            Link("etc-kazoos-id", "app-permissions.config", "permissions"),
        ],
        [KazoOsIndexedDbSchema.FileSystemEntryStoreName] =
        [
            new JsonObject
            {
                ["id"] = "permissions", ["kind"] = 0, ["ownerId"] = "system", ["revision"] = 7,
                ["contentHash"] = Hash(OldPermissions), ["length"] = Encoding.UTF8.GetByteCount(OldPermissions),
            },
        ],
        [KazoOsIndexedDbSchema.FileContentStoreName] = withRewrittenText ? [Chunk(NewPermissions)] : [],
    };

    private static JsonObject Link(string parent, string name, string entry) =>
        new() { ["parentId"] = parent, ["name"] = name, ["entryId"] = entry };

    private static JsonObject Chunk(string text) => new()
    {
        ["contentHash"] = Hash(text), ["chunkIndex"] = 0,
        ["dataBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), ["createdUtcMs"] = 1,
    };

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
