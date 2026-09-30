using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class MailMessageAssertions
{
    public static MailMessageMutationFailure? Verify(MailMessageSnapshot snapshot, MailMessagePatch patch)
    {
        var observer = new MailMimeObservationCodec.Observer(snapshot);
        var textAssertions = patch.Assertions.Where(item => item.MatchVisibleTextValues).ToArray();
        foreach (var assertion in textAssertions)
            if (!TryObservedText(observer, assertion, out _))
                return new(MailMessageMutationError.InvalidProperties, null, [assertion.Label], null);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assertion in patch.Assertions)
        {
            var original = assertion.MatchVisibleTextValues
                ? ObservedText(observer, assertion)
                : observer.Observe(assertion, patch.PartFields, patch.SummaryRunes);
            if (!TryApply(original, assertion.Changes, out var revised))
                return new(MailMessageMutationError.InvalidPatch, null, null, null);
            if (!JsonNode.DeepEquals(original, revised)) invalid.Add(assertion.Label);
        }
        return invalid.Count == 0 ? null : new(MailMessageMutationError.InvalidProperties, null,
            invalid.Order(StringComparer.Ordinal).ToArray(), null);
    }

    private static JsonObject ObservedText(MailMimeObservationCodec.Observer observer, MailMessageAssertion assertion)
    {
        if (!TryObservedText(observer, assertion, out var observed))
            throw new InvalidOperationException("A checked text assertion could not be reproduced.");
        return observed!;
    }

    private static bool TryObservedText(MailMimeObservationCodec.Observer observer, MailMessageAssertion assertion,
        out JsonObject? observed)
    {
        observed = null;
        var root = assertion.Changes.FirstOrDefault(change => change.Path.Count == 0);
        if (root is null || ApplicationValueCodec.Decode(root.Value) is not JsonObject requested) return false;
        JsonObject? complete = null;
        foreach (var flags in TextSelections)
        {
            var candidate = observer.ObserveTextValues(flags.Text, flags.Html, flags.All);
            if (!candidate.Select(item => item.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(requested.Select(item => item.Key))) continue;
            complete = candidate;
            break;
        }
        if (complete is null || !MatchesVisibleText(requested, complete)) return false;
        observed = (JsonObject)requested.DeepClone();
        return true;
    }

    private static bool MatchesVisibleText(JsonObject requested, JsonObject complete)
    {
        var minimum = 1;
        var maximum = int.MaxValue;
        var truncated = false;
        foreach (var item in requested)
        {
            if (!TryTextValue(item.Value, out var text, out var problem, out var incomplete)
                || !TryTextValue(complete[item.Key], out var full, out var fullProblem, out _)
                || problem != fullProblem) return false;
            if (!incomplete)
            {
                if (!string.Equals(text, full, StringComparison.Ordinal)) return false;
                minimum = Math.Max(minimum, Encoding.UTF8.GetByteCount(full));
                continue;
            }
            if (string.Equals(text, full, StringComparison.Ordinal) || !full.StartsWith(text, StringComparison.Ordinal)
                || !Rune.TryGetRuneAt(full, text.Length, out var next)) return false;
            var prefix = Encoding.UTF8.GetByteCount(text);
            truncated = true;
            minimum = Math.Max(minimum, prefix);
            maximum = Math.Min(maximum, prefix + next.Utf8SequenceLength - 1);
        }
        return !truncated || minimum <= maximum;
    }

    private static bool TryTextValue(JsonNode? node, out string text, out bool problem, out bool incomplete)
    {
        text = string.Empty;
        problem = false;
        incomplete = false;
        return node is JsonObject { Count: 3 } value
            && value["Text"] is JsonValue textNode && textNode.TryGetValue(out text!) && text is not null
            && value["EncodingError"] is JsonValue problemNode && problemNode.TryGetValue(out problem)
            && value["Incomplete"] is JsonValue incompleteNode && incompleteNode.TryGetValue(out incomplete);
    }

    internal static bool TryApply(JsonNode? original, IReadOnlyList<MailMessageValueChange> changes, out JsonNode? result)
    {
        var wrapper = new JsonObject { ["Observed"] = original?.DeepClone() };
        foreach (var change in changes)
        {
            JsonObject parent = wrapper;
            if (change.Path.Count > 0)
            {
                if (wrapper["Observed"] is not JsonObject root) { result = null; return false; }
                parent = root;
                for (var index = 0; index < change.Path.Count - 1; index++)
                {
                    if (parent[change.Path[index]] is not JsonObject child) { result = null; return false; }
                    parent = child;
                }
            }
            var key = change.Path.Count == 0 ? "Observed" : change.Path[^1];
            var value = ApplicationValueCodec.Decode(change.Value);
            if (value is null) parent.Remove(key);
            else parent[key] = value;
        }
        result = wrapper["Observed"]?.DeepClone();
        return true;
    }

    private static readonly (bool Text, bool Html, bool All)[] TextSelections =
        [(false, false, false), (true, false, false), (false, true, false), (true, true, false), (false, false, true)];
}
