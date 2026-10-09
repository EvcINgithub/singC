using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace singC.Models;

public static class LogAnalysis
{
    public static string CleanAnsiSequences(string input) => Regex.Replace(input, @"\x1b\[[\d;]*[\x40-\x7E]", string.Empty);

    public static IReadOnlyList<string> Summarize(IEnumerable<string> messages)
    {
        var logs = messages.ToList();
        return new[] { "error", "failed", "timeout", "dns", "connect", "reject", "warn", "panic" }
            .Select(keyword => new { Keyword = keyword, Count = logs.Count(log => log.Contains(keyword, StringComparison.OrdinalIgnoreCase)) })
            .Where(item => item.Count > 0).OrderByDescending(item => item.Count).Take(6)
            .Select(item => $"{item.Keyword}: {item.Count}").DefaultIfEmpty("暂无高频关键词").ToList();
    }
}
