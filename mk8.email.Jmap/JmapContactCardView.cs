using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed record JmapContactCardView(
    DavResourceDB Resource,
    JsonObject Card)
{
    public string Id => JmapId.ContactCard(Resource.Id);
    public string AddressBookId => JmapId.AddressBook(Resource.CollectionId);
}
