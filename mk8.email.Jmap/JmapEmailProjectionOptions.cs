using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapEmailProjectionOptions(
    IReadOnlyList<string> Properties,
    IReadOnlyList<string> BodyProperties,
    bool FetchTextBodyValues,
    bool FetchHtmlBodyValues,
    bool FetchAllBodyValues,
    int MaxBodyValueBytes);
