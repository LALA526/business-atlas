using System.Reflection;

namespace BuildingBlocks.Persistence;

internal static class InitialAtlasSql
{
    internal static readonly string Up = Load();

    private static string Load()
    {
        var assembly = typeof(InitialAtlasSql).Assembly;
        using var stream = assembly.GetManifestResourceStream("BusinessAtlas.db.migrations.sql.0001_initial.sql")
            ?? throw new InvalidOperationException("The frozen initial database migration resource is missing.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        // EF owns the outer transaction and its migration-history write. Removing only
        // the reference script's transaction markers keeps DDL and history atomic.
        sql = sql.Replace("BEGIN;\n", string.Empty, StringComparison.Ordinal);
        sql = sql.Replace("\nCOMMIT;", string.Empty, StringComparison.Ordinal);
        return sql;
    }
}
