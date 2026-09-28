using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class JmapPatchExtensions
{
    public static IEnumerable<string> KeysForPatch(this JsonObject patch)
    {
        foreach (var item in patch)
        {
            var separator = item.Key.IndexOf('/', StringComparison.Ordinal);
            var firstToken = separator < 0 ? item.Key : item.Key[..separator];
            yield return firstToken.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
        }
    }
}
