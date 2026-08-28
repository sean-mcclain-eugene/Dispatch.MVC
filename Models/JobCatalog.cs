namespace Dispatch.Mvc.Models;

public sealed record JobKindInfo(
    string Kind,
    string Title,
    string Blurb,
    TimeSpan StepDelay,
    IReadOnlyList<string> Steps);

/// <summary>
/// Catalog of demo jobs. Durations are short enough to watch, long enough
/// to detach mid-flight — the whole point of the pattern.
/// </summary>
public static class JobCatalog
{
    public static readonly IReadOnlyList<JobKindInfo> All =
    [
        new("sales", "Q3 sales rollup",
            "Pull ledgers across regions, normalize currency, and pack a board-ready summary.",
            TimeSpan.FromSeconds(3.2),
            [
                "Connect to regional ledgers",
                "Normalize FX to USD",
                "Aggregate by territory",
                "Flag statistical outliers",
                "Compose the board pack",
                "Sign the session artifact"
            ]),
        new("export", "Customer extract",
            "Scan tenants, apply retention rules, and write a signed download of the working set.",
            TimeSpan.FromSeconds(3.4),
            [
                "Scan tenant catalogs",
                "Apply retention windows",
                "Tokenize restricted fields",
                "Write columnar extract",
                "Sign the download"
            ]),
        new("index", "Search index rebuild",
            "Snapshot the corpus, rebuild postings, compact segments, and swap the live alias.",
            TimeSpan.FromSeconds(3.5),
            [
                "Snapshot the live corpus",
                "Tokenize documents",
                "Build postings lists",
                "Compact segments",
                "Warm query caches",
                "Swap the serving alias"
            ]),
        new("invoices", "Invoice batch",
            "Validate VAT, match purchase orders, and post the clean set to the ledger.",
            TimeSpan.FromSeconds(3.1),
            [
                "Load the posting queue",
                "Validate VAT numbers",
                "Match purchase orders",
                "Post clean invoices",
                "Hold exceptions",
                "Close the batch"
            ]),
        new("inventory", "Inventory reconciliation",
            "Diff warehouse snapshots, flag shrink, and freeze counts for the close.",
            TimeSpan.FromSeconds(3.2),
            [
                "Snapshot warehouses",
                "Diff SKU on-hand",
                "Flag shrink and overage",
                "Apply adjustments",
                "Freeze cycle counts"
            ])
    ];

    public static JobKindInfo Get(string kind) =>
        All.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
