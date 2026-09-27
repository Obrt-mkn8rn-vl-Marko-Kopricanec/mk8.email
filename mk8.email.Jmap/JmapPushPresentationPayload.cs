using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class JmapPushPresentationPayload
{
    public static byte[] Serialize(JsonObject value) =>
        Encoding.UTF8.GetBytes(value.ToJsonString(JmapJson.SerializerOptions));
}
