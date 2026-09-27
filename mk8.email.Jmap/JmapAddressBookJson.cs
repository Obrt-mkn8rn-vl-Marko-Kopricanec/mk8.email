using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class JmapAddressBookJson
{
    public static readonly HashSet<string> Properties = new HashSet<string>(
        [
            "id", "name", "description", "sortOrder", "isDefault", "isSubscribed",
            "shareWith", "myRights",
        ],
        StringComparer.Ordinal);
}
