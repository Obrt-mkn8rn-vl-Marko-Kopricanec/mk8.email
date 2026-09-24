using System.Text;
using MimeKit.Utils;
using mk8.email.Contracts.Imap;

namespace mk8.email.Application.Protocol;

internal static class Rfc5256Threading
{
    public static IReadOnlyList<string> ParseMessageIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var result = new List<string>();
        foreach (var parsed in MimeUtils.EnumerateReferences(value))
        {
            var normalized = NormalizeParsedMessageId(parsed);
            if (normalized is not null)
                result.Add(normalized);
        }
        return result;
    }

    public static string? ParseFirstMessageId(string? value)
    {
        var references = ParseMessageIds(value);
        if (references.Count > 0)
            return references[0];
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return NormalizeParsedMessageId(MimeUtils.ParseMessageId(value));
    }

    public static string BuildReferences(IReadOnlyList<Rfc5256ThreadMessage> messages) =>
        Render(BuildReferenceGraph(messages));

    public static List<ImapThreadNode> BuildReferencesTree(
        IReadOnlyList<Rfc5256ThreadMessage> messages) =>
        Flatten(BuildReferenceGraph(messages));

    private static IReadOnlyList<ThreadNode> BuildReferenceGraph(
        IReadOnlyList<Rfc5256ThreadMessage> messages)
    {
        if (messages.Count == 0)
            return [];

        var orderedMessages = messages
            .OrderBy(message => message.SequenceNumber)
            .ToArray();
        var containerTable = new Dictionary<string, ThreadNode>(StringComparer.Ordinal);
        var messageNodes = new List<(Rfc5256ThreadMessage Message, ThreadNode Node)>(
            orderedMessages.Length);
        var allNodes = new List<ThreadNode>(orderedMessages.Length);
        var nextOrder = 0;

        foreach (var message in orderedMessages)
        {
            ThreadNode node;
            if (message.MessageId is not null
                && !containerTable.ContainsKey(message.MessageId))
            {
                node = new ThreadNode(nextOrder++, message);
                containerTable.Add(message.MessageId, node);
            }
            else
            {
                // Missing and duplicate Message-IDs are deliberately not placed
                // in the lookup table: no References value can target them.
                node = new ThreadNode(nextOrder++, message);
            }

            messageNodes.Add((message, node));
            allNodes.Add(node);
        }

        foreach (var (message, current) in messageNodes)
        {
            ThreadNode? previousReference = null;
            foreach (var reference in message.References)
            {
                if (!containerTable.TryGetValue(reference, out var referenceNode))
                {
                    referenceNode = new ThreadNode(nextOrder++, null);
                    containerTable.Add(reference, referenceNode);
                    allNodes.Add(referenceNode);
                }

                if (previousReference is not null
                    && referenceNode.Parent is null
                    && !WouldCreateLoop(previousReference, referenceNode))
                {
                    Link(previousReference, referenceNode);
                }
                previousReference = referenceNode;
            }

            // Step 1.B replaces a tentative link made while reconstructing a
            // different message's (possibly truncated) References chain.
            Detach(current);
            if (previousReference is not null
                && !WouldCreateLoop(previousReference, current))
            {
                Link(previousReference, current);
            }
        }

        var root = new ThreadNode(nextOrder++, null);
        foreach (var node in allNodes)
        {
            if (node.Parent is null)
                Link(root, node);
        }

        PruneDummyNodes(root);
        foreach (var node in root.Children)
        {
            if (node.IsDummy)
                node.Children.Sort(CompareBySentDate);
        }
        root.Children.Sort(CompareBySentDate);

        MergeRootsBySubject(root, ref nextOrder);
        SortAllSiblingSets(root);
        return root.Children;
    }

    private static List<ImapThreadNode> Flatten(IReadOnlyList<ThreadNode> roots)
    {
        var result = new List<ImapThreadNode>();
        var pending = new Stack<(ThreadNode Node, int ParentIndex)>();
        for (var index = roots.Count - 1; index >= 0; index--)
            pending.Push((roots[index], -1));

        while (pending.Count > 0)
        {
            var (node, parentIndex) = pending.Pop();
            var nodeIndex = result.Count;
            result.Add(new ImapThreadNode(node.Message?.Identifier, parentIndex));
            for (var index = node.Children.Count - 1; index >= 0; index--)
                pending.Push((node.Children[index], nodeIndex));
        }
        return result;
    }

    private static string? NormalizeParsedMessageId(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var separator = -1;
        var quoted = false;
        var escaped = false;
        var domainLiteral = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quoted)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                continue;
            }

            if (character == '"' && separator < 0)
            {
                quoted = true;
                continue;
            }
            if (separator >= 0 && character == '[')
            {
                domainLiteral = true;
                continue;
            }
            if (separator >= 0 && character == ']')
            {
                domainLiteral = false;
                continue;
            }
            if (character != '@' || domainLiteral)
                continue;
            if (separator >= 0)
                return null;
            separator = index;
        }

        if (quoted || escaped || domainLiteral
            || separator <= 0
            || separator == value.Length - 1)
        {
            return null;
        }

        var localPart = UnquoteLocalPart(value.AsSpan(0, separator));
        var domain = value[(separator + 1)..];
        if (localPart.Length == 0 || domain.Length == 0)
            return null;

        // NUL cannot occur in a valid msg-id, so it safely preserves the
        // local/domain boundary even when a quoted local part contains '@'.
        return string.Concat(localPart, "\0", domain);
    }

    private static string UnquoteLocalPart(ReadOnlySpan<char> value)
    {
        var result = new StringBuilder(value.Length);
        var quoted = false;
        var escaped = false;
        foreach (var character in value)
        {
            if (!quoted)
            {
                if (character == '"')
                    quoted = true;
                else
                    result.Append(character);
                continue;
            }

            if (escaped)
            {
                result.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '"')
            {
                quoted = false;
            }
            else
            {
                result.Append(character);
            }
        }
        return result.ToString();
    }

    private static bool WouldCreateLoop(ThreadNode parent, ThreadNode child)
    {
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, child))
                return true;
        }
        return false;
    }

    private static void Link(ThreadNode parent, ThreadNode child)
    {
        child.Parent = parent;
        parent.Children.Add(child);
    }

    private static void Detach(ThreadNode child)
    {
        if (child.Parent is null)
            return;
        child.Parent.Children.Remove(child);
        child.Parent = null;
    }

    private static void Move(ThreadNode child, ThreadNode parent)
    {
        Detach(child);
        Link(parent, child);
    }

    private static void PruneDummyNodes(ThreadNode root)
    {
        var traversal = new Stack<ThreadNode>();
        var postOrder = new List<ThreadNode>();
        traversal.Push(root);
        while (traversal.Count > 0)
        {
            var node = traversal.Pop();
            postOrder.Add(node);
            foreach (var child in node.Children)
                traversal.Push(child);
        }

        for (var nodeIndex = postOrder.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var parent = postOrder[nodeIndex];
            var parentIsRoot = ReferenceEquals(parent, root);
            for (var childIndex = 0; childIndex < parent.Children.Count;)
            {
                var child = parent.Children[childIndex];
                if (!child.IsDummy)
                {
                    childIndex++;
                    continue;
                }

                if (child.Children.Count == 0)
                {
                    parent.Children.RemoveAt(childIndex);
                    child.Parent = null;
                    continue;
                }
                if (parentIsRoot && child.Children.Count > 1)
                {
                    childIndex++;
                    continue;
                }

                var promoted = child.Children.ToArray();
                child.Children.Clear();
                parent.Children.RemoveAt(childIndex);
                child.Parent = null;
                for (var index = 0; index < promoted.Length; index++)
                {
                    promoted[index].Parent = parent;
                    parent.Children.Insert(childIndex + index, promoted[index]);
                }
                childIndex += promoted.Length;
            }
        }
    }

    private static void MergeRootsBySubject(ThreadNode root, ref int nextOrder)
    {
        var subjectTable = new Dictionary<string, ThreadNode>(StringComparer.Ordinal);
        var initialRoots = root.Children.ToArray();
        foreach (var current in initialRoots)
        {
            var subject = ThreadSubject(current);
            if (subject.Length == 0)
                continue;

            if (!subjectTable.TryGetValue(subject, out var associated))
            {
                subjectTable.Add(subject, current);
                continue;
            }

            if (!associated.IsDummy
                && (current.IsDummy
                    || (associated.Message!.IsReplyOrForward
                        && !current.Message!.IsReplyOrForward)))
            {
                subjectTable[subject] = current;
            }
        }

        foreach (var current in initialRoots)
        {
            if (!ReferenceEquals(current.Parent, root))
                continue;

            var subject = ThreadSubject(current);
            if (subject.Length == 0
                || !subjectTable.TryGetValue(subject, out var associated)
                || ReferenceEquals(current, associated))
            {
                continue;
            }

            if (associated.IsDummy && current.IsDummy)
            {
                foreach (var child in current.Children.ToArray())
                    Move(child, associated);
                Detach(current);
            }
            else if (associated.IsDummy)
            {
                Move(current, associated);
            }
            else if (current.Message!.IsReplyOrForward
                     && !associated.Message!.IsReplyOrForward)
            {
                Move(current, associated);
            }
            else
            {
                var dummy = new ThreadNode(nextOrder++, null);
                Link(root, dummy);
                Move(associated, dummy);
                Move(current, dummy);
                subjectTable[subject] = dummy;
            }
        }
    }

    private static string ThreadSubject(ThreadNode node) =>
        Representative(node).BaseSubjectKey;

    private static Rfc5256ThreadMessage Representative(ThreadNode node)
    {
        var current = node;
        while (current.Message is null && current.Children.Count > 0)
            current = current.Children[0];
        return current.Message
            ?? throw new InvalidOperationException("A retained thread node has no message.");
    }

    private static int CompareBySentDate(ThreadNode left, ThreadNode right)
    {
        var leftMessage = Representative(left);
        var rightMessage = Representative(right);
        var dateComparison = leftMessage.SentAt.CompareTo(rightMessage.SentAt);
        if (dateComparison != 0)
            return dateComparison;
        var sequenceComparison = leftMessage.SequenceNumber.CompareTo(
            rightMessage.SequenceNumber);
        return sequenceComparison != 0
            ? sequenceComparison
            : left.Order.CompareTo(right.Order);
    }

    private static void SortAllSiblingSets(ThreadNode root)
    {
        var traversal = new Stack<ThreadNode>();
        var postOrder = new List<ThreadNode>();
        traversal.Push(root);
        while (traversal.Count > 0)
        {
            var node = traversal.Pop();
            postOrder.Add(node);
            foreach (var child in node.Children)
                traversal.Push(child);
        }

        for (var index = postOrder.Count - 1; index >= 0; index--)
            postOrder[index].Children.Sort(CompareBySentDate);
    }

    private static string Render(IReadOnlyList<ThreadNode> roots)
    {
        var result = new StringBuilder();
        foreach (var root in roots)
            AppendThreadList(result, root);
        return result.ToString();
    }

    private static void AppendThreadList(StringBuilder result, ThreadNode root)
    {
        var actions = new Stack<RenderAction>();
        actions.Push(new RenderAction(root, false));
        while (actions.Count > 0)
        {
            var action = actions.Pop();
            if (action.Close)
            {
                result.Append(')');
                continue;
            }

            var node = action.Node!;
            result.Append('(');
            actions.Push(new RenderAction(null, true));
            if (node.IsDummy)
            {
                for (var index = node.Children.Count - 1; index >= 0; index--)
                    actions.Push(new RenderAction(node.Children[index], false));
                continue;
            }

            result.Append(node.Message!.Identifier);
            var tail = node;
            while (tail.Children.Count == 1 && !tail.Children[0].IsDummy)
            {
                tail = tail.Children[0];
                result.Append(' ').Append(tail.Message!.Identifier);
            }
            if (tail.Children.Count == 0)
                continue;

            result.Append(' ');
            for (var index = tail.Children.Count - 1; index >= 0; index--)
                actions.Push(new RenderAction(tail.Children[index], false));
        }
    }

    private sealed class ThreadNode(int order, Rfc5256ThreadMessage? message)
    {
        public int Order { get; } = order;
        public Rfc5256ThreadMessage? Message { get; } = message;
        public bool IsDummy => Message is null;
        public ThreadNode? Parent { get; set; }
        public List<ThreadNode> Children { get; } = [];
    }

    private readonly record struct RenderAction(ThreadNode? Node, bool Close);
}
