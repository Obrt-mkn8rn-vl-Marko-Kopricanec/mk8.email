using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Jmap;

internal sealed record JmapStateChangePoll(
    long Cursor,
    JsonObject? StateChange);
