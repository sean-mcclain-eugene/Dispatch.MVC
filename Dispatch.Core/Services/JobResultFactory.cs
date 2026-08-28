using System.Text.Json;
using Dispatch.Core.Models;

namespace Dispatch.Core.Services;

/// <summary>
/// Deterministic fake reports so a given job id always yields the same board pack.
/// Replace with the real aggregation in a production port.
/// </summary>
public static class JobResultFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Create(string kind, Guid jobId)
    {
        var rng = new Seeded(Hash(jobId.ToString("N") + kind));
        object payload = kind switch
        {
            "export" => BuildExport(rng),
            "index" => BuildIndex(rng),
            "invoices" => BuildInvoices(rng),
            "inventory" => BuildInventory(rng),
            _ => BuildSales(rng)
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static object BuildSales(Seeded rng) => new
    {
        type = "sales",
        period = "Q3 2026",
        currency = "USD",
        total = rng.Money(4_200_000, 9_800_000),
        outliersExcluded = rng.Int(2, 11),
        regions = new[]
        {
            new { name = "Pacific Northwest", revenue = rng.Money(900_000, 2_200_000), orders = rng.Int(1200, 3400), change = rng.Change() },
            new { name = "Great Lakes", revenue = rng.Money(700_000, 1_900_000), orders = rng.Int(900, 2600), change = rng.Change() },
            new { name = "Northeast", revenue = rng.Money(800_000, 2_400_000), orders = rng.Int(1100, 3100), change = rng.Change() },
            new { name = "Sun Belt", revenue = rng.Money(1_100_000, 2_800_000), orders = rng.Int(1500, 4200), change = rng.Change() }
        }
    };

    private static object BuildExport(Seeded rng) => new
    {
        type = "export",
        format = "parquet",
        rows = rng.Int(18_000, 84_000),
        bytes = rng.Int(2_400_000, 12_000_000),
        sample = new[]
        {
            new { account = "Northwind Retail", region = "PNW", seats = rng.Int(12, 80), status = "active" },
            new { account = "Lumen Forge", region = "NE", seats = rng.Int(8, 40), status = "active" },
            new { account = "Harbor & Co", region = "GL", seats = rng.Int(20, 110), status = "paused" },
            new { account = "Redwood Labs", region = "SB", seats = rng.Int(6, 28), status = "active" }
        }
    };

    private static object BuildIndex(Seeded rng) => new
    {
        type = "index",
        documents = rng.Int(250_000, 1_200_000),
        segments = rng.Int(18, 64),
        tokens = rng.Int(12_000_000, 48_000_000),
        p95Ms = Math.Round(rng.Double(12, 48), 1),
        alias = "search-live"
    };

    private static object BuildInvoices(Seeded rng) => new
    {
        type = "invoices",
        processed = rng.Int(420, 1800),
        exceptions = rng.Int(4, 28),
        postedUsd = rng.Money(180_000, 920_000),
        aging = new[]
        {
            new { bucket = "Current", count = rng.Int(200, 900), amount = rng.Money(80_000, 400_000) },
            new { bucket = "1–30", count = rng.Int(40, 180), amount = rng.Money(20_000, 120_000) },
            new { bucket = "31–60", count = rng.Int(10, 60), amount = rng.Money(8_000, 40_000) },
            new { bucket = "60+", count = rng.Int(2, 24), amount = rng.Money(2_000, 18_000) }
        }
    };

    private static object BuildInventory(Seeded rng) => new
    {
        type = "inventory",
        skus = rng.Int(4_200, 18_000),
        shrinkUsd = rng.Money(4_800, 38_000),
        warehouses = new[]
        {
            new { name = "SEA-1", onHand = rng.Int(8_000, 22_000), delta = rng.Int(-400, 350), flags = rng.Int(0, 12) },
            new { name = "ORD-4", onHand = rng.Int(10_000, 28_000), delta = rng.Int(-280, 410), flags = rng.Int(0, 9) },
            new { name = "EWR-2", onHand = rng.Int(6_500, 19_000), delta = rng.Int(-510, 220), flags = rng.Int(1, 16) }
        }
    };

    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (var c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        return h;
    }

    private sealed class Seeded
    {
        private uint _state;
        public Seeded(uint seed) => _state = seed == 0 ? 1u : seed;

        public double Double(double min, double max) => min + Next() * (max - min);
        public int Int(int min, int max) => (int)Math.Floor(Double(min, max + 1));
        public int Money(int min, int max) => Int(min, max);
        public double Change() => Math.Round(Double(-0.12, 0.24), 3);

        private double Next()
        {
            _state = _state * 1664525u + 1013904223u;
            return (_state >> 8) / (double)(1 << 24);
        }
    }
}
