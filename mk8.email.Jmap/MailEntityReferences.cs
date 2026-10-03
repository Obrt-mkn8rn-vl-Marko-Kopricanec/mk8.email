namespace mk8.email.Jmap;

internal static class MailEntityReferences
{
    public static bool AreValid(IEnumerable<string>? values, JmapInvocationContext context) =>
        values is null || values.All(value => JmapId.IsValidId(value)
            || context.TryGetReferenceKey(value, out var key) && JmapId.IsValidId(key));
}
