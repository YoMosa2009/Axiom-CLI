using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Axiom.Core.Agent
{
    public static class DataFileTools
    {
        public static string ReadCsv(string path, int maxRows = 30, int maxCols = 20)
        {
            if (!File.Exists(path))
                return $"Error: file not found: {path}";

            maxRows = Math.Clamp(maxRows, 1, 200);
            maxCols = Math.Clamp(maxCols, 1, 100);

            var sb = new StringBuilder();
            sb.AppendLine($"[[CSV]] {path}");
            int row = 0;
            foreach (string line in File.ReadLines(path))
            {
                if (row >= maxRows)
                {
                    sb.AppendLine("...[more rows truncated]");
                    break;
                }
                string[] cells = SplitCsvLine(line);
                if (cells.Length > maxCols)
                {
                    cells = cells.Take(maxCols).Append("…").ToArray();
                }
                sb.AppendLine(string.Join(" | ", cells.Select(c => c.Length > 40 ? c[..37] + "…" : c)));
                row++;
            }
            sb.AppendLine($"rows_shown: {row}");
            return sb.ToString().TrimEnd();
        }

        public static string ReadNotebook(string path, int maxCells = 20)
        {
            if (!File.Exists(path))
                return $"Error: file not found: {path}";

            maxCells = Math.Clamp(maxCells, 1, 80);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("cells", out JsonElement cells)
                    || cells.ValueKind != JsonValueKind.Array)
                    return "Error: not a valid Jupyter notebook (missing cells).";

                var sb = new StringBuilder();
                sb.AppendLine($"[[NOTEBOOK]] {path}");
                int i = 0;
                foreach (var cell in cells.EnumerateArray())
                {
                    if (i >= maxCells)
                    {
                        sb.AppendLine("...[more cells truncated]");
                        break;
                    }
                    string type = cell.TryGetProperty("cell_type", out JsonElement t) ? t.GetString() ?? "?" : "?";
                    string source = "";
                    if (cell.TryGetProperty("source", out JsonElement src))
                    {
                        if (src.ValueKind == JsonValueKind.String)
                            source = src.GetString() ?? "";
                        else if (src.ValueKind == JsonValueKind.Array)
                            source = string.Concat(src.EnumerateArray().Select(x => x.GetString() ?? ""));
                    }
                    if (source.Length > 600)
                        source = source[..597] + "…";
                    sb.AppendLine($"--- cell {i} [{type}] ---");
                    sb.AppendLine(source.TrimEnd());
                    i++;
                }
                sb.AppendLine($"cells_shown: {i}");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: failed to parse notebook: {ex.Message}";
            }
        }

        private static string[] SplitCsvLine(string line)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else inQuotes = !inQuotes;
                }
                else if ((c == ',' && !inQuotes) || (c == '\t' && !inQuotes && list.Count == 0 && !line.Contains(',')))
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
                else sb.Append(c);
            }
            list.Add(sb.ToString());
            return list.ToArray();
        }
    }
}
