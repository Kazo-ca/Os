using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Rename;

/// <summary>One file whose content the rename migration of 3.0.0 to 3.2.0 had lost.</summary>
/// <param name="Path">The file's path, or <c>entry &lt;id&gt;</c> for an entry no folder links.</param>
/// <param name="Recovered">
/// <see langword="true"/> when its rewritten text was found and proven; <see langword="false"/>
/// when the file was emptied (its settings fall back to their defaults).
/// </param>
public sealed record RenamedContentRepairItem(string Path, bool Recovered);

/// <summary>The entry records the repair writes, and what each one repairs.</summary>
/// <param name="Entries">The file entries to write back.</param>
/// <param name="Items">One line per repaired file, in path order.</param>
internal sealed record RenamedContentRepairPlan(IReadOnlyList<JsonObject> Entries, IReadOnlyList<RenamedContentRepairItem> Items);

/// <summary>
/// Repairs, once per browser, the files the rename migration of Kazo OS 3.0.0 to 3.2.0 left
/// pointing at deleted content, like <c>fsck</c> repairs an inode whose blocks are gone
/// (<c>doc/en/wasm/dev/rename-migration.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The migration (<see cref="RenamedOsDatabaseRewriter"/>) stored the rewritten text of each
/// configuration file under its new SHA-256 and deleted the old chunks, but did not write the new
/// hash into the file's entry. Reading or writing such a file fails with <c>ProviderFailure</c>,
/// and the settings it holds silently fall back to their defaults.
/// </para>
/// <para>
/// The rewritten text is still in the content store, referenced by no entry. For each damaged file
/// the repair looks for the unreferenced text that, read backwards through
/// <see cref="RenamedNames.ReverseForms"/>, gives back bytes whose SHA-256 and length are the ones
/// the entry still records, and whose <see cref="RenamedNames.Rewrite"/> is that text again. Only
/// such a proof points the entry at the text; otherwise the file is emptied, which every settings
/// reader takes as "no value set".
/// </para>
/// </remarks>
public static class RenamedContentRepair
{
    /// <summary><c>localStorage</c> key recorded once the repair ran, so it runs once per browser.</summary>
    public const string DoneKey = "kazoos.renameContentRepair";

    /// <summary>Most search steps for one file and one candidate text.</summary>
    private const int MaxStepsPerCandidate = 1 << 14;

    /// <summary>Most search steps for the whole repair, so a damaged profile never stalls the boot.</summary>
    private const int MaxSteps = 1 << 20;

    private const int FileKind = 0;
    private const string Local = "local";

    /// <summary>
    /// Runs the repair over the page's OS database unless it already ran in this browser.
    /// </summary>
    /// <param name="runtime">The page's JavaScript runtime.</param>
    /// <param name="cancellationToken">Cancels the repair; nothing is recorded then.</param>
    /// <returns>The repaired files; empty when nothing was damaged or the repair already ran.</returns>
    public static async Task<IReadOnlyList<RenamedContentRepairItem>> RunAsync(
        IJSRuntime runtime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        await using BrowserRenamedStorage browser = new(runtime);
        return await RunAsync(browser, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the repair over <paramref name="browser"/> (a fake in the unit tests).</summary>
    internal static async Task<IReadOnlyList<RenamedContentRepairItem>> RunAsync(
        IRenamedStorageBrowser browser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        IReadOnlyList<KeyValuePair<string, string>> local = await browser.ReadStorageAsync(Local, cancellationToken).ConfigureAwait(false);
        if (local.Any(entry => entry.Key == DoneKey))
        {
            return [];
        }

        // A browser with no OS database yet (first start) has nothing to repair.
        IReadOnlyList<string> databases = await browser.ListDatabasesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RenamedContentRepairItem> repaired = databases.Contains(KazoOsIndexedDbSchema.DatabaseName, StringComparer.Ordinal)
            ? await browser.RepairLostContentAsync(cancellationToken).ConfigureAwait(false)
            : [];
        await browser.WriteStorageAsync(Local, DoneKey, "1", cancellationToken).ConfigureAwait(false);
        return repaired;
    }

    /// <summary>Gets whether a file entry points at a content hash that has no stored chunk.</summary>
    /// <param name="entries">The records of the <c>fsEntries</c> store.</param>
    /// <param name="storedHashes">The content hashes that have at least one chunk.</param>
    internal static bool HasLostContent(IEnumerable<JsonObject> entries, IReadOnlySet<string> storedHashes) =>
        entries.Any(entry => LostHash(entry, storedHashes) is not null);

    /// <summary>Plans the repair of every file entry whose content is lost.</summary>
    /// <param name="stores">The records of the <c>fsEntries</c>, <c>fsLinks</c> and <c>fsContent</c> stores.</param>
    internal static RenamedContentRepairPlan Plan(IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        IReadOnlyList<JsonObject> entries = Records(stores, KazoOsIndexedDbSchema.FileSystemEntryStoreName);
        Dictionary<string, List<JsonObject>> chunksByHash = Records(stores, KazoOsIndexedDbSchema.FileContentStoreName)
            .Where(chunk => Text(chunk, "contentHash") is not null)
            .GroupBy(chunk => Text(chunk, "contentHash")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(chunk => (int?)chunk["chunkIndex"] ?? 0).ToList(), StringComparer.Ordinal);
        HashSet<string> stored = new(chunksByHash.Keys, StringComparer.Ordinal);
        Dictionary<string, string> paths = RenamedOsDatabaseRewriter.EntryPaths(Records(stores, KazoOsIndexedDbSchema.FileSystemLinkStoreName));

        var damaged = entries
            .Select(entry => (Entry: entry, Hash: LostHash(entry, stored)))
            .Where(item => item.Hash is not null)
            .Select(item => (item.Entry, Hash: item.Hash!, Path: PathOf(item.Entry, paths)))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToList();
        if (damaged.Count == 0)
        {
            return new RenamedContentRepairPlan([], []);
        }

        // The rewritten texts are the stored contents no entry references any more.
        HashSet<string> referenced = new(entries.Select(entry => Text(entry, "contentHash")).OfType<string>(), StringComparer.Ordinal);
        List<(string Hash, string Text)> candidates = [.. chunksByHash
            .Where(pair => !referenced.Contains(pair.Key))
            .Select(pair => (pair.Key, Text: RenamedOsDatabaseRewriter.ReadText(pair.Value)))
            .Where(pair => pair.Text is not null)
            .Select(pair => (pair.Key, pair.Text!))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)];

        SearchBudget budget = new(MaxSteps);
        // Content is addressed by its SHA-256: files that had the same old bytes share one rewritten
        // text. A proven candidate therefore stays available, and the answer for an old hash and
        // length is kept so the next file with the same bytes is not searched again.
        Dictionary<(string Hash, long Length), (string Hash, string Text)?> proven = [];
        List<JsonObject> written = [];
        List<RenamedContentRepairItem> items = [];
        foreach ((JsonObject entry, string hash, string path) in damaged)
        {
            JsonObject repaired = entry.DeepClone().AsObject();
            long expectedLength = Number(entry["length"]) ?? -1;
            if (!proven.TryGetValue((hash, expectedLength), out (string Hash, string Text)? match))
            {
                int index = candidates.FindIndex(candidate => IsRewriteOf(candidate.Text, hash, expectedLength, budget));
                match = index >= 0 ? candidates[index] : null;
                proven[(hash, expectedLength)] = match;
            }

            if (match is (string newHash, string text))
            {
                repaired["contentHash"] = newHash;
                repaired["length"] = (long)Encoding.UTF8.GetByteCount(text);
            }
            else
            {
                // The "touch" state: no content blob, read as an empty file.
                repaired["contentHash"] = null;
                repaired["length"] = 0L;
            }

            if (Number(repaired["revision"]) is long revision)
            {
                repaired["revision"] = revision + 1;
            }

            written.Add(repaired);
            items.Add(new RenamedContentRepairItem(path, match is not null));
        }

        return new RenamedContentRepairPlan(written, items);
    }

    /// <summary>
    /// Gets whether <paramref name="rewritten"/> is the migration's rewrite of the bytes whose SHA-256
    /// is <paramref name="originalHash"/> and whose length is <paramref name="originalLength"/>.
    /// </summary>
    /// <remarks>
    /// Every place where <paramref name="rewritten"/> holds a new spelling may have held the old one,
    /// or the new one already: the search tries those choices left to right, keeps only the ones
    /// whose byte length can still reach <paramref name="originalLength"/>, and proves each full
    /// choice by its hash and by rewriting it again.
    /// </remarks>
    internal static bool IsRewriteOf(string rewritten, string originalHash, long originalLength, SearchBudget budget)
    {
        List<Site> sites = Sites(rewritten);
        long delta = originalLength - Encoding.UTF8.GetByteCount(rewritten);
        if (sites.Count == 0 || budget.Exhausted)
        {
            return false;
        }

        // Bounds of the length change the sites from index i on can still bring.
        int[] gainAfter = new int[sites.Count + 1];
        int[] lossAfter = new int[sites.Count + 1];
        for (int index = sites.Count - 1; index >= 0; index--)
        {
            gainAfter[index] = gainAfter[index + 1] + Math.Max(0, sites[index].Delta);
            lossAfter[index] = lossAfter[index + 1] + Math.Min(0, sites[index].Delta);
        }

        Stack<Site> chosen = new();
        int steps = 0;
        return Search(0, 0, 0);

        bool Search(int index, int cursor, long change)
        {
            if (++steps > MaxStepsPerCandidate || !budget.Spend())
            {
                return false;
            }

            if (change + gainAfter[index] < delta || change + lossAfter[index] > delta)
            {
                return false;
            }

            if (index == sites.Count)
            {
                return change == delta && Proves(Original(rewritten, chosen), rewritten, originalHash);
            }

            Site site = sites[index];
            if (site.Position >= cursor)
            {
                chosen.Push(site);
                if (Search(index + 1, site.Position + site.NewForm.Length, change + site.Delta))
                {
                    return true;
                }

                chosen.Pop();
            }

            return Search(index + 1, cursor, change);
        }
    }

    /// <summary>Bounds the work of one repair.</summary>
    internal sealed class SearchBudget(int steps)
    {
        private int _remaining = steps;

        /// <summary>Gets whether no step is left.</summary>
        public bool Exhausted => _remaining <= 0;

        /// <summary>Takes one step; <see langword="false"/> when none is left.</summary>
        public bool Spend() => --_remaining >= 0;
    }

    private readonly record struct Site(int Position, string NewForm, string OldForm)
    {
        public int Delta => Encoding.UTF8.GetByteCount(OldForm) - Encoding.UTF8.GetByteCount(NewForm);
    }

    private static List<Site> Sites(string text)
    {
        List<Site> sites = [];
        foreach ((string newForm, string oldForm) in RenamedNames.ReverseForms)
        {
            for (int position = text.IndexOf(newForm, StringComparison.Ordinal);
                 position >= 0;
                 position = text.IndexOf(newForm, position + 1, StringComparison.Ordinal))
            {
                sites.Add(new Site(position, newForm, oldForm));
            }
        }

        sites.Sort((left, right) => left.Position != right.Position
            ? left.Position.CompareTo(right.Position)
            : right.NewForm.Length.CompareTo(left.NewForm.Length));
        return sites;
    }

    private static string Original(string rewritten, Stack<Site> chosen)
    {
        StringBuilder original = new(rewritten.Length + (chosen.Count * 4));
        int cursor = 0;
        foreach (Site site in chosen.Reverse())
        {
            original.Append(rewritten, cursor, site.Position - cursor).Append(site.OldForm);
            cursor = site.Position + site.NewForm.Length;
        }

        return original.Append(rewritten, cursor, rewritten.Length - cursor).ToString();
    }

    private static bool Proves(string original, string rewritten, string originalHash) =>
        string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original))), originalHash, StringComparison.OrdinalIgnoreCase)
        && RenamedNames.Rewrite(original) == rewritten;

    private static string? LostHash(JsonObject entry, IReadOnlySet<string> storedHashes) =>
        (int?)entry["kind"] == FileKind && Text(entry, "contentHash") is { } hash && !storedHashes.Contains(hash) ? hash : null;

    private static string PathOf(JsonObject entry, Dictionary<string, string> paths) =>
        Text(entry, "id") is { } id && paths.TryGetValue(id, out string? path) ? path : $"entry {Text(entry, "id")}";

    private static IReadOnlyList<JsonObject> Records(IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> stores, string name) =>
        stores.TryGetValue(name, out IReadOnlyList<JsonObject>? records) ? records : [];

    /// <summary>Reads a whole number whether the node was parsed or built in memory (as an int or a long).</summary>
    private static long? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && long.TryParse(value.ToJsonString(), out long number)
            ? number
            : null;

    private static string? Text(JsonObject record, string property) =>
        record[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
