namespace SmartShop.Infrastructure.Persistence;

/// <summary>Helpers for SQL LIKE / ILIKE patterns built from user input.</summary>
public static class Like
{
    /// <summary>Escapes the LIKE wildcards so "50%" or "a_b" match literally (PostgreSQL's default escape is the backslash).</summary>
    public static string Escape(string text) => text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
