using System;
using System.Collections.Generic;
using System.Linq;

namespace singC.Models;

public static class ConnectionPresentation
{
    public static IEnumerable<ConnectionInfo> Select(IEnumerable<ConnectionInfo> connections, string search, string sort)
    {
        var selected = string.IsNullOrWhiteSpace(search) ? connections : connections.Where(c =>
            (c.Host?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (c.Network?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (c.Rule?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (c.Source?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (c.Destination?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        return sort switch
        {
            "host" => selected.OrderBy(c => c.PrimaryDisplay, StringComparer.OrdinalIgnoreCase),
            "upload" => selected.OrderByDescending(c => c.UploadBytes),
            "download" => selected.OrderByDescending(c => c.DownloadBytes),
            _ => selected.OrderByDescending(c => c.StartTime)
        };
    }
}
