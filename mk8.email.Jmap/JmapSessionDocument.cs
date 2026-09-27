using System.Security.Cryptography;
using System.Text.Json.Nodes;
using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

public sealed record JmapSessionDocument(JsonObject Value, string State);
