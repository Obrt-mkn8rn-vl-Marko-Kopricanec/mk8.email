using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class ApplicationArgumentBindingResolver
{
    public static bool TryResolve(
        JmapApplicationCall call,
        List<JmapApplicationInvocation> completed,
        out JsonObject arguments,
        out ApplicationBindingFailure failure)
    {
        arguments = (JsonObject)call.Arguments.DeepClone();
        failure = ApplicationBindingFailure.None;
        if (call.Bindings is null)
            return true;
        foreach (var binding in call.Bindings)
        {
            failure = binding.Failure;
            if (failure != ApplicationBindingFailure.None)
                return false;
            if (binding.Target.Length == 0 || call.Arguments.ContainsKey(binding.Target))
            {
                failure = ApplicationBindingFailure.InvalidTarget;
                return false;
            }
            // Preserve first-completed-result semantics, including additional results.
            var source = completed.FirstOrDefault(item => string.Equals(item.CorrelationId, binding.SourceCorrelationId, StringComparison.Ordinal));
            if (source is null || source.Operation != binding.SourceOperation
                || !TrySelect(source.Arguments, binding.Path, 0, out var value))
            {
                failure = ApplicationBindingFailure.InvalidSource;
                return false;
            }
            arguments[binding.Target] = value;
        }
        return true;
    }

    private static bool TrySelect(
        JsonNode? current, ApplicationValuePathSegment[] path, int index, out JsonNode? value)
    {
        value = null;
        if (index == path.Length)
        {
            value = current?.DeepClone();
            return true;
        }
        var segment = path[index];
        if (current is JsonArray array && segment.AllArrayItems)
            return TrySelectAll(array, path, index + 1, out value);
        if (current is JsonObject jsonObject && jsonObject.TryGetPropertyValue(segment.Property, out var property))
            return TrySelect(property, path, index + 1, out value);
        if (current is JsonArray items && segment.ArrayIndex is { } arrayIndex && arrayIndex < items.Count)
            return TrySelect(items[arrayIndex], path, index + 1, out value);
        return false;
    }

    private static bool TrySelectAll(
        JsonArray items, ApplicationValuePathSegment[] path, int index, out JsonNode? value)
    {
        value = null;
        var mapped = new JsonArray();
        foreach (var item in items)
        {
            if (!TrySelect(item, path, index, out var selected))
                return false;
            if (selected is JsonArray nested)
            {
                foreach (var entry in nested)
                    mapped.Add(entry?.DeepClone());
            }
            else
                mapped.Add(selected);
        }
        value = mapped;
        return true;
    }
}
