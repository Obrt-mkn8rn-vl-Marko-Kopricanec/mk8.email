using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsFolderMutations
{
    public static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, GatewayEwsFolderGraph? graph,
        int capacity, string unavailable, CancellationToken cancellationToken)
    {
        var count = request.Operation is "CreateFolder" ? request.Names!.Count : request.Folders.Count;
        var codes = new string?[count];
        var plans = BuildPlans(request, graph, profile.Username, codes, unavailable);
        if (plans.Count == 0) return Render(request.Operation, account, codes, new Dictionary<int, Guid>(), null);
        if (count > profile.Limits.MaxObjectsInSet || request.Operation is "CreateFolder" && graph!.Count + plans.Count > capacity)
        {
            foreach (ref readonly var plan in CollectionsMarshal.AsSpan(plans)) codes[plan.Index] = "ErrorExceededFindCountLimit";
            plans.Clear();
        }
        if (plans.Count == 0) return Render(request.Operation, account, codes, new Dictionary<int, Guid>(), null);
        var command = BuildCommand(account, graph!.State, request.Operation, plans);
        var reply = await application.ExecuteOperationAsync(authentication, profile, MailOperationKind.MutateFolders,
            command, cancellationToken).ConfigureAwait(false);
        var result = GatewayEwsMutationReply.Decode(reply, command);
        if (result.Status != MailFolderMutationStatus.Ok)
        {
            var failure = result.Status == MailFolderMutationStatus.StateMismatch ? "ErrorIrresolvableConflict" : "ErrorFolderNotFound";
            foreach (ref readonly var plan in CollectionsMarshal.AsSpan(plans)) codes[plan.Index] = failure;
            return Render(request.Operation, account, codes, new Dictionary<int, Guid>(), null);
        }
        var ids = ApplyResults(request.Operation, plans, result, codes);
        if (request.Operation is "CreateFolder" && ids.Values.Any(id => graph.Snapshot(id) is not null))
            throw new InvalidOperationException("A created folder reused an existing identity.");
        return Render(request.Operation, account, codes, ids, result.NewState);
    }

    private static List<Plan> BuildPlans(GatewayEwsRequest request, GatewayEwsFolderGraph? graph, string username,
        string?[] codes, string unavailable)
    {
        var plans = new List<Plan>();
        var seen = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < codes.Length; index++)
        {
            var reference = request.Folders[request.Operation is "CreateFolder" ? 0 : index];
            var (id, error) = graph is null ? (Guid.Empty, unavailable) : graph.Resolve(reference, username);
            error ??= ValidateTarget(request.Operation, graph!, reference, id);
            if (error is not null) { codes[index] = error; continue; }
            var name = request.Operation is "DeleteFolder" ? null : request.Names![index].Normalize(NormalizationForm.FormC);
            var parent = request.Operation is "CreateFolder" ? id == Guid.Empty ? null : (Guid?)id : graph!.Snapshot(id)!.ParentId;
            if (name is not null && (graph!.HasName(parent, name, request.Operation is "UpdateFolder" ? id : null)
                || request.Operation is "CreateFolder" && !names.Add(name)))
            { codes[index] = "ErrorFolderExists"; continue; }
            if (request.Operation is not "CreateFolder" && !seen.Add(id))
            { codes[index] = "ErrorInvalidRequest"; continue; }
            var token = request.Operation is "CreateFolder" ? "ews" + index.ToString(CultureInfo.InvariantCulture) : $"M{id:N}";
            plans.Add(new(index, token, id, parent, name));
        }
        return plans;
    }

    private static string? ValidateTarget(string operation, GatewayEwsFolderGraph graph, GatewayEwsFolderReference reference, Guid id)
    {
        if (reference.ChangeKey is not null)
        {
            var bytes = new byte[1024];
            if (reference.ChangeKey.Length > 2048 || !Convert.TryFromBase64String(reference.ChangeKey, bytes, out var written)
                || written == 0 || !string.Equals(Convert.ToBase64String(bytes.AsSpan(0, written)), reference.ChangeKey, StringComparison.Ordinal))
                return "ErrorInvalidChangeKey";
            if (!string.Equals(reference.ChangeKey, graph.ChangeKey, StringComparison.Ordinal)) return "ErrorIrresolvableConflict";
        }
        if (operation is "CreateFolder") return null;
        if (id == Guid.Empty || graph.Snapshot(id)!.IsProtected) return "ErrorAccessDenied";
        return operation is "DeleteFolder" && graph.HasChildren(id) ? "ErrorCannotDeleteObject" : null;
    }

    private static MailFolderMutationCommand BuildCommand(Guid account, string state, string operation, IReadOnlyList<Plan> plans)
    {
        return new(account, state, true,
            operation is "CreateFolder" ? plans.Select(plan => new MailFolderCreate(plan.Token,
                new(plan.Name!, plan.Parent is null ? null : $"M{plan.Parent:N}", null, 0, false), null)).ToArray() : [],
            operation is "UpdateFolder" ? plans.Select(plan => new MailFolderUpdate(plan.Token,
                new(MailFolderFields.Name, new(plan.Name!, null, null, 0, false), [], null))).ToArray() : [],
            operation is "DeleteFolder" ? plans.Select(plan => new MailFolderDestroy(plan.Token)).ToArray() : []);
    }

    private static Dictionary<int, Guid> ApplyResults(string operation, IReadOnlyList<Plan> plans,
        MailFolderMutationResult result, string?[] codes)
    {
        var ids = new Dictionary<int, Guid>();
        var slots = plans.ToDictionary(plan => plan.Token, plan => plan.Index, StringComparer.Ordinal);
        foreach (var item in result.Created)
            Apply(slots[item.CreationId], item.Folder?.Id, item.Failure, ids, codes);
        foreach (var item in result.Updated)
            Apply(slots[item.RequestedId], item.FolderId, item.Failure, ids, codes);
        foreach (var item in result.Destroyed)
            Apply(slots[item.RequestedId], item.FolderId, item.Failure, ids, codes);
        if (ids.Count + codes.Count(code => code is not null) != codes.Length)
            throw new InvalidOperationException($"The {operation} outcomes are incomplete.");
        return ids;
    }

    private static void Apply(int index, Guid? id, MailFolderMutationFailure? failure,
        Dictionary<int, Guid> ids, string?[] codes)
    {
        if (id is not null) { ids.Add(index, id.Value); return; }
        codes[index] = failure!.Error switch
        {
            MailFolderMutationError.NotFound => "ErrorFolderNotFound",
            MailFolderMutationError.Forbidden => "ErrorAccessDenied",
            MailFolderMutationError.MailboxHasChild or MailFolderMutationError.MailboxHasEmail => "ErrorCannotDeleteObject",
            _ => "ErrorInvalidFolderName",
        };
    }

    private static string Render(string operation, Guid account, string?[] codes,
        Dictionary<int, Guid> ids, string? state)
    {
        var messages = GatewayEwsSoap.Messages;
        var responses = new XElement(messages + "ResponseMessages");
        for (var index = 0; index < codes.Length; index++)
        {
            var code = codes[index];
            var response = new XElement(messages + (operation + "ResponseMessage"),
                new XAttribute("ResponseClass", code is null ? "Success" : "Error"));
            if (code is not null) response.Add(new XElement(messages + "MessageText", "The mail-folder write was refused."));
            response.Add(new XElement(messages + "ResponseCode", code ?? "NoError"));
            if (code is not null) response.Add(new XElement(messages + "DescriptiveLinkKey", 0));
            else if (operation is not "DeleteFolder")
                response.Add(new XElement(messages + "Folders", new XElement(GatewayEwsSoap.Types + "Folder",
                    new XElement(GatewayEwsSoap.Types + "FolderId",
                        new XAttribute("Id", GatewayEwsFolderIdCodec.Encode(account, ids[index])),
                        new XAttribute("ChangeKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(state!)))))));
            responses.Add(response);
        }
        return GatewayEwsSoap.Envelope(new XElement(messages + (operation + "Response"), responses));
    }

    private sealed record Plan(int Index, string Token, Guid Id, Guid? Parent, string? Name);
}
