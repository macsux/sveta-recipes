using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SvetaRecipes.Core.Assistant;

public sealed record SqlResult(string Text, int RowsChanged);

/// <summary>Runs any SQL the assistant writes against the database and renders the result as a compact table.</summary>
public static class SqlRunner
{
    public const int MaxRows = 200;
    private const int MaxCell = 300;

    public static SqlResult Run(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True");
        conn.Open();
        var before = TotalChanges(conn);
        var text = new StringBuilder();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;
            using var reader = cmd.ExecuteReader();
            do
            {
                if (reader.FieldCount > 0) Render(reader, text);
            } while (reader.NextResult());
        }
        var changed = (int)(TotalChanges(conn) - before);
        if (changed > 0 || text.Length == 0) text.Append($"{changed} row(s) changed.");
        return new SqlResult(text.ToString().TrimEnd(), changed);
    }

    private static long TotalChanges(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT total_changes()";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Render(SqliteDataReader reader, StringBuilder text)
    {
        text.AppendLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        var rows = 0;
        while (reader.Read())
        {
            if (++rows > MaxRows) continue;
            text.AppendLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader, i))));
        }
        text.AppendLine(rows > MaxRows ? $"({rows} rows, first {MaxRows} shown)" : $"({rows} row{(rows == 1 ? "" : "s")})");
        text.AppendLine();
    }

    private static string Cell(SqliteDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return "NULL";
        var value = reader.GetValue(i);
        var s = value switch
        {
            byte[] b => $"<blob {b.Length} bytes>",
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
        s = s.Replace("\r", "").Replace("\n", "\\n").Replace("|", "/");
        return s.Length > MaxCell ? s[..MaxCell] + "…" : s;
    }
}
