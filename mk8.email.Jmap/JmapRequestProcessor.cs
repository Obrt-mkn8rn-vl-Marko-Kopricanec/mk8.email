using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using mk8.email.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

public sealed class JmapRequestProcessor
{
    private readonly IReadOnlyDictionary<MailOperationKind, IJmapMethod> _methods;
    private readonly JmapAccountProfileService _sessions;
    private readonly IMailFolderReader _folderReader;
    private readonly IMailChangesReader _changesReader;
    private readonly IMailAddressBookReader _addressBookReader;
    private readonly IMailIdentityReader _identityReader;
    private readonly IMailVacationReader? _vacationReader;
    private readonly IMailPushSubscriptionReader _pushReader;
    private readonly IMailThreadReader _threadReader;
    private readonly IMailSubmissionReader? _submissionReader;
    private readonly IMailBlobCopyService? _blobCopyService;
    private readonly IMailVacationMutator? _vacationMutator;
    private readonly IMailSubmissionQueryService? _submissionQueryService;
    private readonly IMailFolderQueryService? _folderQueryService;
    private readonly IMailContactCopyService? _contactCopyService;
    private readonly EmailDbContext _database;
    private readonly EnvironmentConfig _environment;
    private readonly LargeObjectTransactionEffects _blobEffects;
    private readonly ILogger<JmapRequestProcessor> _logger;
    private readonly ApplicationOperationReceiptStore? _receipts;
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 256,
    };
    private static readonly JsonSerializerOptions StrictReceiptJsonOptions = new(ReceiptJsonOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public JmapRequestProcessor(
        IEnumerable<IJmapMethod> methods,
        JmapAccountProfileService sessions,
        EmailDbContext database,
        EnvironmentConfig environment,
        LargeObjectTransactionEffects blobEffects,
        ILogger<JmapRequestProcessor> logger,
        ApplicationOperationReceiptStore? receipts = null)
        : this(methods, sessions, database, environment, blobEffects, logger, receipts,
            null, null, null, null, null, null, null, null, null, null, null, null, null)
    {
    }

    internal JmapRequestProcessor(
        IEnumerable<IJmapMethod> methods,
        JmapAccountProfileService sessions,
        EmailDbContext database,
        EnvironmentConfig environment,
        LargeObjectTransactionEffects blobEffects,
        ILogger<JmapRequestProcessor> logger,
        ApplicationOperationReceiptStore? receipts,
        IMailFolderReader? folderReader,
        IMailChangesReader? changesReader,
        IMailAddressBookReader? addressBookReader,
        IMailIdentityReader? identityReader,
        IMailVacationReader? vacationReader,
        IMailPushSubscriptionReader? pushReader,
        IMailThreadReader? threadReader,
        IMailSubmissionReader? submissionReader,
        IMailBlobCopyService? blobCopyService,
        IMailVacationMutator? vacationMutator,
        IMailSubmissionQueryService? submissionQueryService,
        IMailFolderQueryService? folderQueryService,
        IMailContactCopyService? contactCopyService)
    {
        _methods = methods.ToDictionary(method => ValidRegisteredOperation(method.Operation, method.Feature));
        _sessions = sessions;
        if (folderReader is null)
        {
            var accountService = new JmapAccountService(database);
            _folderReader = new MailFolderReader(accountService,
                new JmapMailboxStore(database), new JmapStateService(database, accountService), environment);
        }
        else
            _folderReader = folderReader;
        if (changesReader is null)
        {
            var accountService = new JmapAccountService(database);
            _changesReader = new MailChangesReader(accountService,
                new JmapStateService(database, accountService),
                new JmapIdentityService(database, accountService), database, environment);
        }
        else
            _changesReader = changesReader;
        if (addressBookReader is null)
        {
            var accountService = new JmapAccountService(database);
            _addressBookReader = new MailAddressBookReader(database, accountService,
                new JmapStateService(database, accountService), environment);
        }
        else
            _addressBookReader = addressBookReader;
        if (identityReader is null)
        {
            var accountService = new JmapAccountService(database);
            _identityReader = new MailIdentityReader(database, accountService,
                new JmapIdentityService(database, accountService),
                new JmapStateService(database, accountService), environment);
        }
        else
            _identityReader = identityReader;
        _vacationReader = vacationReader;
        _pushReader = pushReader ?? new MailPushSubscriptionReader(database, environment);
        if (threadReader is null)
        {
            var accountService = new JmapAccountService(database);
            _threadReader = new MailThreadReader(database, accountService,
                new JmapStateService(database, accountService));
        }
        else
            _threadReader = threadReader;
        _submissionReader = submissionReader;
        _blobCopyService = blobCopyService;
        _vacationMutator = vacationMutator;
        _submissionQueryService = submissionQueryService;
        _folderQueryService = folderQueryService;
        _contactCopyService = contactCopyService;
        _database = database;
        _environment = environment;
        _blobEffects = blobEffects;
        _logger = logger;
        _receipts = receipts;
    }

    internal Task<JmapApplicationProfile> GetProfileAsync(AuthenticatedMailUser user, CancellationToken token = default) =>
        _sessions.GetProfileAsync(user, token);

    public async Task<MailOperationResult> ExecuteAsync(
        MailOperationCommand command,
        AuthenticatedMailUser user,
        Guid? operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(user);
        if (command.Features is null || command.Arguments is null || !Enum.IsDefined(command.Operation)
            || command.Operation is MailOperationKind.Failure or MailOperationKind.Echo)
            throw NotRequest("The mail operation command is incomplete.");
        var features = ValidateHeader(command.Features.ToArray(), 1);
        var createdIds = CloneCreatedIds(command.KnownEntities);
        var context = new JmapInvocationContext(user, features, createdIds)
        {
            ReferenceAliases = CloneReferenceAliases(command.ReferenceAliases),
        };
        var relational = _database.Database.IsRelational();
        if (relational && (operationId is null || operationId == Guid.Empty || _receipts is null))
            throw new InvalidOperationException("A durable operation identity and receipt store are required.");
        var inputHash = relational
            ? Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, ReceiptJsonOptions)))
            : string.Empty;
        var receiptKey = relational
            ? new ApplicationReceiptKey(operationId!.Value, 0, user.Id, "mail.operation", inputHash)
            : null;
        MailOperationResponse response;
        if (_methods.TryGetValue(command.Operation, out var method) && features.Contains(method.Feature))
            response = await ExecuteLegacyMethodAsync(method, command.Arguments, context, receiptKey,
                cancellationToken).ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadFolders && features.Contains(MailFeature.Messages))
            response = await ExecuteFoldersAsync(command, context, user, receiptKey,
                cancellationToken).ConfigureAwait(false);
        else if (MailChangeOperations.TryGetFeature(command.Operation, out var changeFeature)
            && features.Contains(changeFeature))
            response = await ExecuteChangesAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadAddressBooks && features.Contains(MailFeature.Contacts))
            response = await ExecuteAddressBooksAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadSenderIdentities && features.Contains(MailFeature.Submission))
            response = await ExecuteIdentitiesAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadVacationSettings && features.Contains(MailFeature.AutomaticReplies))
            response = await ExecuteVacationAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadNotificationSubscriptions && features.Contains(MailFeature.Basic))
            response = await ExecutePushSubscriptionsAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadThreads && features.Contains(MailFeature.Messages))
            response = await ExecuteThreadsAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadSubmissions && features.Contains(MailFeature.Submission))
            response = await ExecuteSubmissionsAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.CopyBinaryObjects && features.Contains(MailFeature.Basic))
            response = await ExecuteBlobCopyAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.MutateVacationSettings && features.Contains(MailFeature.AutomaticReplies))
            response = await ExecuteVacationSetAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.FindSubmissions && features.Contains(MailFeature.Submission))
            response = await ExecuteSubmissionQueryAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.FindSubmissionChanges && features.Contains(MailFeature.Submission))
            response = await ExecuteSubmissionQueryChangesAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.FindFolders && features.Contains(MailFeature.Messages))
            response = await ExecuteFolderQueryAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.FindFolderChanges && features.Contains(MailFeature.Messages))
            response = await ExecuteFolderQueryChangesAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.CopyContacts && features.Contains(MailFeature.Contacts))
            response = await ExecuteContactCopyAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else
        {
            response = EncodeResponse(JmapMethodResponse.Error("unknownMethod"));
        }
        var profile = await _sessions.GetProfileAsync(user, cancellationToken).ConfigureAwait(false);
        return new MailOperationResult(response, createdIds, profile);
    }

    private Task<MailOperationResponse> ExecuteLegacyMethodAsync(
        IJmapMethod method,
        JsonObject arguments,
        JmapInvocationContext context,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var cloned = (JsonObject)arguments.DeepClone();
        return InvokeAtomicallyAsync(method.Operation, context, async token =>
        {
            var methodResponse = await method.InvokeAsync(context, cloned, token).ConfigureAwait(false);
            ValidateResponse(methodResponse);
            return EncodeResponse(methodResponse);
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteFoldersAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var folderCommand = ParseFolderCommand(command.Arguments);
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await _folderReader.ReadAsync(folderCommand, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The folder reader returned an incomplete result.");
            return new MailOperationResponse(MailOperationKind.ReadFolders, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private async Task<MailOperationResponse> ExecuteChangesAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var changesCommand = ParseMailChangesCommand(command.Arguments);
        if (command.Operation is not (MailOperationKind.ReadAddressBookChanges
                or MailOperationKind.ReadContactChanges)
            && !changesCommand.AccountReferenceEligible)
            throw NotRequest("The mail changes account reference is invalid.");
        return await InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await _changesReader.ReadAsync(command.Operation, changesCommand, user, token)
                .ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The mail changes reader returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MailOperationResponse> ExecuteAddressBooksAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var readCommand = ParseAddressBookCommand(command.Arguments);
        return await InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await _addressBookReader.ReadAsync(readCommand, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The address-book reader returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MailOperationResponse> ExecuteIdentitiesAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var readCommand = ParseIdentityCommand(command.Arguments);
        return await InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await _identityReader.ReadAsync(readCommand, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The identity reader returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MailOperationResponse> ExecuteVacationAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var readCommand = ParseVacationCommand(command.Arguments);
        var reader = _vacationReader
            ?? throw new InvalidOperationException("The vacation reader is not configured.");
        return await InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await reader.ReadAsync(readCommand, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The vacation reader returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MailOperationResponse> ExecutePushSubscriptionsAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var readCommand = ParsePushSubscriptionCommand(command.Arguments);
        return await InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await _pushReader.ReadAsync(readCommand, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The push-subscription reader returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken).ConfigureAwait(false);
    }

    private Task<MailOperationResponse> ExecuteThreadsAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var readCommand = ParseThreadCommand(command.Arguments);
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await _threadReader.ReadAsync(readCommand, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The thread reader returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteSubmissionsAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var readCommand = ParseSubmissionCommand(command.Arguments);
        var reader = _submissionReader
            ?? throw new InvalidOperationException("The submission reader is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await reader.ReadAsync(readCommand, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The submission reader returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteBlobCopyAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var copyCommand = ParseBlobCopyCommand(command.Arguments);
        var service = _blobCopyService
            ?? throw new InvalidOperationException("The blob copy service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.CopyAsync(copyCommand, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The blob copy service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteVacationSetAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var mutation = ParseVacationSetCommand(command.Arguments);
        var service = _vacationMutator
            ?? throw new InvalidOperationException("The vacation mutation service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.SetAsync(mutation, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The vacation mutation service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteSubmissionQueryAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var query = ParseSubmissionQueryCommand(command.Arguments);
        var service = _submissionQueryService
            ?? throw new InvalidOperationException("The submission query service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.QueryAsync(query, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The submission query service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteSubmissionQueryChangesAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var query = ParseSubmissionQueryChangesCommand(command.Arguments);
        var service = _submissionQueryService
            ?? throw new InvalidOperationException("The submission query service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.QueryChangesAsync(query, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The submission query service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteFolderQueryAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var query = ParseFolderQueryCommand(command.Arguments);
        var service = _folderQueryService
            ?? throw new InvalidOperationException("The folder query service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.QueryAsync(query, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The folder query service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteFolderQueryChangesAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var query = ParseFolderQueryChangesCommand(command.Arguments);
        var service = _folderQueryService
            ?? throw new InvalidOperationException("The folder query service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.QueryChangesAsync(query, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The folder query service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteContactCopyAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var copy = ParseContactCopyCommand(command.Arguments);
        var service = _contactCopyService
            ?? throw new InvalidOperationException("The contact-copy service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.CopyAsync(copy, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The contact-copy service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private static MailFolderReadCommand ParseFolderCommand(JsonObject arguments)
    {
        if (arguments.Count != 3 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("folderIds") || !arguments.ContainsKey("checkAccountOnly"))
            throw NotRequest("The folder read command has an invalid shape.");
        try
        {
            return JsonSerializer.Deserialize<MailFolderReadCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The folder read command is missing.");
        }
        catch (JsonException)
        {
            throw NotRequest("The folder read command contains invalid values.");
        }
    }

    private static MailChangesCommand ParseMailChangesCommand(JsonObject arguments)
    {
        if (arguments.Count != 4 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("sinceState") || !arguments.ContainsKey("maxChanges")
            || !arguments.ContainsKey("accountReferenceEligible"))
            throw NotRequest("The mail changes command has an invalid shape.");
        try
        {
            var command = JsonSerializer.Deserialize<MailChangesCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The mail changes command is missing.");
            if (command.SinceState is null || command.MaxChanges is < 1 or > 9_007_199_254_740_991)
                throw NotRequest("The mail changes command contains invalid values.");
            return command;
        }
        catch (JsonException)
        {
            throw NotRequest("The mail changes command contains invalid values.");
        }
    }

    private static MailAddressBookReadCommand ParseAddressBookCommand(JsonObject arguments)
    {
        if (arguments.Count != 3 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("bookIds") || !arguments.ContainsKey("accountReferenceEligible"))
            throw NotRequest("The address-book read command has an invalid shape.");
        try
        {
            return JsonSerializer.Deserialize<MailAddressBookReadCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The address-book read command is missing.");
        }
        catch (JsonException)
        {
            throw NotRequest("The address-book read command contains invalid values.");
        }
    }

    private static MailIdentityReadCommand ParseIdentityCommand(JsonObject arguments)
    {
        if (arguments.Count != 2 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("identityIds"))
            throw NotRequest("The identity read command has an invalid shape.");
        try
        {
            return JsonSerializer.Deserialize<MailIdentityReadCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The identity read command is missing.");
        }
        catch (JsonException)
        {
            throw NotRequest("The identity read command contains invalid values.");
        }
    }

    private static MailVacationReadCommand ParseVacationCommand(JsonObject arguments)
    {
        if (arguments.Count != 3 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("includeSingleton") || !arguments.ContainsKey("includeBodies"))
            throw NotRequest("The vacation read command has an invalid shape.");
        try
        {
            var command = JsonSerializer.Deserialize<MailVacationReadCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The vacation read command is missing.");
            if (!command.IncludeSingleton && command.IncludeBodies)
                throw NotRequest("The vacation read command has incompatible selections.");
            return command;
        }
        catch (JsonException)
        {
            throw NotRequest("The vacation read command contains invalid values.");
        }
    }

    private static MailPushSubscriptionReadCommand ParsePushSubscriptionCommand(JsonObject arguments)
    {
        if (arguments.Count != 1 || !arguments.ContainsKey("subscriptionIds"))
            throw NotRequest("The push-subscription read command has an invalid shape.");
        try
        {
            return JsonSerializer.Deserialize<MailPushSubscriptionReadCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The push-subscription read command is missing.");
        }
        catch (JsonException)
        {
            throw NotRequest("The push-subscription read command contains invalid values.");
        }
    }

    private static MailThreadReadCommand ParseThreadCommand(JsonObject arguments)
    {
        if (arguments.Count != 1 || !arguments.ContainsKey("accountId"))
            throw NotRequest("The thread read command has an invalid shape.");
        try
        {
            return JsonSerializer.Deserialize<MailThreadReadCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The thread read command is missing.");
        }
        catch (JsonException)
        {
            throw NotRequest("The thread read command contains invalid values.");
        }
    }

    private static MailSubmissionReadCommand ParseSubmissionCommand(JsonObject arguments)
    {
        if (arguments.Count != 3 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("submissionIds") || !arguments.ContainsKey("includeDeliveryStatus"))
            throw NotRequest("The submission read command has an invalid shape.");
        try
        {
            return JsonSerializer.Deserialize<MailSubmissionReadCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The submission read command is missing.");
        }
        catch (JsonException)
        {
            throw NotRequest("The submission read command contains invalid values.");
        }
    }

    private static MailBlobCopyCommand ParseBlobCopyCommand(JsonObject arguments)
    {
        if (arguments.Count != 3 || !arguments.ContainsKey("fromAccountId")
            || !arguments.ContainsKey("accountId") || !arguments.ContainsKey("blobIds"))
            throw NotRequest("The blob copy command has an invalid shape.");
        try
        {
            var command = JsonSerializer.Deserialize<MailBlobCopyCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The blob copy command is missing.");
            if (command.BlobIds is null || command.BlobIds.Any(string.IsNullOrEmpty))
                throw NotRequest("The blob copy command contains invalid object IDs.");
            return command;
        }
        catch (JsonException)
        {
            throw NotRequest("The blob copy command contains invalid values.");
        }
    }

    private MailVacationSetCommand ParseVacationSetCommand(JsonObject arguments)
    {
        if (arguments.Count != 3 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("ifInState") || arguments["ifInState"] is not null
                && (arguments["ifInState"] is not JsonValue state
                    || !state.TryGetValue<string>(out _))
            || arguments["updates"] is not JsonArray updates)
            throw NotRequest("The vacation mutation command has an invalid shape.");
        foreach (var item in updates)
        {
            if (item is not JsonObject update || update.Count != 12
                || !update.ContainsKey("setIsEnabled") || !update.ContainsKey("isEnabled")
                || !update.ContainsKey("setFromDate") || !update.ContainsKey("fromDate")
                || !update.ContainsKey("setToDate") || !update.ContainsKey("toDate")
                || !update.ContainsKey("setSubject") || !update.ContainsKey("subject")
                || !update.ContainsKey("setTextBody") || !update.ContainsKey("textBody")
                || !update.ContainsKey("setHtmlBody") || !update.ContainsKey("htmlBody"))
                throw NotRequest("The vacation mutation command contains an invalid update.");
        }
        try
        {
            var command = JsonSerializer.Deserialize<MailVacationSetCommand>(arguments, ReceiptJsonOptions)
                ?? throw NotRequest("The vacation mutation command is missing.");
            if (command.Updates is null || command.Updates.Count > _environment.Jmap.MaxObjectsInSet
                || command.Updates.Any(update => update is null
                    || update.FromDate is { Kind: not DateTimeKind.Utc }
                    || update.ToDate is { Kind: not DateTimeKind.Utc }))
                throw NotRequest("The vacation mutation command has invalid update count.");
            return command;
        }
        catch (JsonException)
        {
            throw NotRequest("The vacation mutation command contains invalid values.");
        }
    }

    private MailSubmissionQueryCommand ParseSubmissionQueryCommand(JsonObject arguments)
    {
        if (arguments.Count != 8 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("criteria") || !arguments.ContainsKey("checkAccountOnly")
            || !arguments.ContainsKey("position") || !arguments.ContainsKey("anchorId")
            || !arguments.ContainsKey("anchorCanMatch")
            || !arguments.ContainsKey("anchorOffset") || !arguments.ContainsKey("limit"))
            throw NotRequest("The submission query command has an invalid shape.");
        try
        {
            var query = JsonSerializer.Deserialize<MailSubmissionQueryCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The submission query command is missing.");
            ValidateSubmissionCriteria(query.Criteria);
            if (query.Position is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L
                || query.AnchorOffset is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L
                || query.AnchorId is null && query.AnchorCanMatch
                || query.Limit < 0 || query.Limit > _environment.Jmap.MaxObjectsInGet)
                throw NotRequest("The submission query window is invalid.");
            return query;
        }
        catch (JsonException)
        {
            throw NotRequest("The submission query command contains invalid values.");
        }
    }

    private static MailSubmissionQueryChangesCommand ParseSubmissionQueryChangesCommand(JsonObject arguments)
    {
        if (arguments.Count != 5 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("criteria") || !arguments.ContainsKey("checkAccountOnly")
            || !arguments.ContainsKey("sinceState") || !arguments.ContainsKey("maxChanges"))
            throw NotRequest("The submission query-changes command has an invalid shape.");
        try
        {
            var query = JsonSerializer.Deserialize<MailSubmissionQueryChangesCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The submission query-changes command is missing.");
            ValidateSubmissionCriteria(query.Criteria);
            if (query.SinceState is null
                || query.MaxChanges is < 0 or > 9_007_199_254_740_991L)
                throw NotRequest("The submission query-changes values are invalid.");
            return query;
        }
        catch (JsonException)
        {
            throw NotRequest("The submission query-changes command contains invalid values.");
        }
    }

    private static void ValidateSubmissionCriteria(MailSubmissionQueryCriteria? criteria)
    {
        if (criteria?.Sort is null || criteria.Sort.Any(item => item is null
            || !Enum.IsDefined(item.Field) || !Enum.IsDefined(item.Collation)))
            throw NotRequest("The submission query sort is invalid.");
        ValidateSubmissionFilter(criteria.Filter, 0);
    }

    private static void ValidateSubmissionFilter(MailSubmissionFilter? filter, int depth)
    {
        if (filter is null) return;
        if (depth > 64 || !Enum.IsDefined(filter.Operator))
            throw NotRequest("The submission query filter is invalid.");
        if (filter.Operator != MailSubmissionFilterOperator.Condition)
        {
            if (filter.Conditions is null || filter.IdentityIds is not null || filter.EmailIds is not null
                || filter.ThreadIds is not null || filter.UndoStatus is not null
                || filter.Before is not null || filter.After is not null)
                throw NotRequest("The submission query operator is invalid.");
            foreach (var child in filter.Conditions)
                ValidateSubmissionFilter(child, depth + 1);
            return;
        }
        if (filter.Conditions is not null || !ValidSubmissionIds(filter.IdentityIds)
            || !ValidSubmissionIds(filter.EmailIds) || !ValidSubmissionIds(filter.ThreadIds)
            || filter.UndoStatus is not null and not ("pending" or "final" or "canceled")
            || filter.Before is { Kind: not DateTimeKind.Utc }
            || filter.After is { Kind: not DateTimeKind.Utc })
            throw NotRequest("The submission query condition is invalid.");
    }

    private static bool ValidSubmissionIds(IReadOnlyList<string>? ids) =>
        ids is null || ids.All(id => id is not null && JmapId.IsValidId(id));

    private MailFolderQueryCommand ParseFolderQueryCommand(JsonObject arguments)
    {
        if (arguments.Count != 8 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("criteria") || !arguments.ContainsKey("checkAccountOnly")
            || !arguments.ContainsKey("position") || !arguments.ContainsKey("anchorId")
            || !arguments.ContainsKey("anchorCanMatch")
            || !arguments.ContainsKey("anchorOffset") || !arguments.ContainsKey("limit"))
            throw NotRequest("The folder query command has an invalid shape.");
        try
        {
            var query = JsonSerializer.Deserialize<MailFolderQueryCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The folder query command is missing.");
            ValidateFolderCriteria(query.Criteria);
            if (query.Position is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L
                || query.AnchorOffset is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L
                || query.AnchorId is null && query.AnchorCanMatch
                || query.Limit < 0 || query.Limit > _environment.Jmap.MaxObjectsInGet)
                throw NotRequest("The folder query window is invalid.");
            return query;
        }
        catch (JsonException)
        {
            throw NotRequest("The folder query command contains invalid values.");
        }
    }

    private static MailFolderQueryChangesCommand ParseFolderQueryChangesCommand(JsonObject arguments)
    {
        if (arguments.Count != 5 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("criteria") || !arguments.ContainsKey("checkAccountOnly")
            || !arguments.ContainsKey("sinceState") || !arguments.ContainsKey("maxChanges"))
            throw NotRequest("The folder query-changes command has an invalid shape.");
        try
        {
            var query = JsonSerializer.Deserialize<MailFolderQueryChangesCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The folder query-changes command is missing.");
            ValidateFolderCriteria(query.Criteria);
            if (query.SinceState is null || query.Criteria.SortAsTree || query.Criteria.FilterAsTree
                || query.MaxChanges is < 0 or > 9_007_199_254_740_991L)
                throw NotRequest("The folder query-changes values are invalid.");
            return query;
        }
        catch (JsonException)
        {
            throw NotRequest("The folder query-changes command contains invalid values.");
        }
    }

    private static void ValidateFolderCriteria(MailFolderQueryCriteria? criteria)
    {
        if (criteria?.Sort is null || criteria.Sort.Any(item => item is null
            || !Enum.IsDefined(item.Field) || !Enum.IsDefined(item.Collation)))
            throw NotRequest("The folder query sort is invalid.");
        ValidateFolderFilter(criteria.Filter, 0);
    }

    private static void ValidateFolderFilter(MailFolderFilter? filter, int depth)
    {
        if (filter is null) return;
        if (depth > 64 || !Enum.IsDefined(filter.Operator)
            || !Enum.IsDefined(filter.ParentConstraint))
            throw NotRequest("The folder query filter is invalid.");
        if (filter.Operator != MailFolderFilterOperator.Condition)
        {
            if (filter.Conditions is null || filter.ParentConstraint != MailFolderParentConstraint.Any
                || filter.ParentId is not null || filter.Name is not null || filter.Role is not null
                || filter.MatchNullRole || filter.HasAnyRole is not null || filter.IsSubscribed is not null)
                throw NotRequest("The folder query operator is invalid.");
            foreach (var child in filter.Conditions)
                ValidateFolderFilter(child, depth + 1);
            return;
        }
        if (filter.Conditions is not null
            || filter.ParentConstraint == MailFolderParentConstraint.Folder && filter.ParentId is null
            || filter.ParentConstraint != MailFolderParentConstraint.Folder && filter.ParentId is not null
            || filter.MatchNullRole && filter.Role is not null)
            throw NotRequest("The folder query condition is invalid.");
    }

    private MailContactCopyCommand ParseContactCopyCommand(JsonObject arguments)
    {
        if (arguments.Count != 9 || !arguments.ContainsKey("sourceAccountId")
            || !arguments.ContainsKey("sourceReferenceParseable")
            || !arguments.ContainsKey("sourceReferenceEligible")
            || !arguments.ContainsKey("targetAccountId")
            || !arguments.ContainsKey("targetReferenceParseable")
            || !arguments.ContainsKey("targetReferenceEligible")
            || !arguments.ContainsKey("ifFromInState") || !arguments.ContainsKey("ifInState")
            || !arguments.ContainsKey("creationIds"))
            throw NotRequest("The contact-copy command has an invalid shape.");
        try
        {
            var copy = JsonSerializer.Deserialize<MailContactCopyCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The contact-copy command is missing.");
            if (copy.SourceReferenceEligible && !copy.SourceReferenceParseable
                || copy.TargetReferenceEligible && !copy.TargetReferenceParseable
                || !copy.SourceReferenceParseable && copy.SourceAccountId != Guid.Empty
                || !copy.TargetReferenceParseable && copy.TargetAccountId != Guid.Empty
                || copy.SourceReferenceEligible && copy.TargetReferenceEligible
                    && copy.SourceAccountId == copy.TargetAccountId
                || copy.CreationIds is null || copy.CreationIds.Count > _environment.Jmap.MaxObjectsInSet
                || copy.CreationIds.Any(id => id is null || !JmapId.IsValidId(id))
                || copy.CreationIds.Distinct(StringComparer.Ordinal).Count() != copy.CreationIds.Count)
                throw NotRequest("The contact-copy creation identifiers are invalid.");
            return copy;
        }
        catch (JsonException)
        {
            throw NotRequest("The contact-copy command contains invalid values.");
        }
    }

    private static Dictionary<string, string> CloneCreatedIds(IReadOnlyDictionary<string, string>? values)
    {
        var createdIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (var item in values)
            {
                if (item.Value is null || !JmapId.IsValidId(item.Key) || !JmapId.IsValidId(item.Value))
                    throw NotRequest("The known entity map contains an invalid identifier.");
                createdIds.Add(item.Key, item.Value);
            }
        }
        return createdIds;
    }

    private static Dictionary<string, string> CloneReferenceAliases(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
            throw NotRequest("The reference alias map is required.");
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            if (item.Key is null || item.Value is null)
                throw NotRequest("The reference alias map is incomplete.");
            aliases.Add(item.Key, item.Value);
        }
        return aliases;
    }

    internal void ValidatePlan(MailAdmissionPlan? plan)
    {
        if (plan is not null)
            _ = ValidateHeader(plan.Features, plan.OperationCount);
    }

    private HashSet<MailFeature> ValidateHeader(IReadOnlyList<MailFeature>? values, int invocationCount)
    {
        if (values is null || values.Any(feature => !Enum.IsDefined(feature)))
            throw NotRequest("The application batch contains invalid feature identifiers.");
        var features = values.ToHashSet();
        var supported = _methods.Values.Select(method => method.Feature)
            .Append(MailFeature.Basic).Append(MailFeature.AutomaticReplies).ToHashSet();
        if (features.Any(feature => !supported.Contains(feature)))
            throw new MailApplicationException(new MailApplicationFailure(
                MailFailureKind.UnsupportedFeature, "The requested mail features are not supported."));
        if (!features.Contains(MailFeature.Basic))
            throw NotRequest("The application batch must include the basic mail feature.");
        if (invocationCount < 0)
            throw NotRequest("The application invocation count cannot be negative.");
        if (invocationCount > _environment.Jmap.MaxCallsInRequest)
            throw new MailApplicationException(new MailApplicationFailure(
                MailFailureKind.ResourceLimit, "The application batch contains too many operations.",
                MailResourceLimit.OperationCount));
        return features;
    }

    private async Task<MailOperationResponse> InvokeAtomicallyAsync(
        MailOperationKind operation,
        JmapInvocationContext context,
        Func<CancellationToken, Task<MailOperationResponse>> invoke,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var createdIds = context.CreatedIds.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var postCommitMarker = context.MarkPostCommitActions();
        var blobEffectMarker = _blobEffects.Mark();
        var presentationMarker = context.MarkPresentationEffects();
        IDbContextTransaction? transaction = null;
        var commitAttempted = false;
        try
        {
            if (_database.Database.IsRelational())
                transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (receiptKey is not null)
            {
                var existing = await _receipts!.FindLockedAsync(receiptKey, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    var replay = JsonSerializer.Deserialize<JmapReplayState>(existing.Result.Span, ReceiptJsonOptions)
                        ?? throw new InvalidOperationException("The invocation receipt result is incomplete.");
                    ValidateEncodedResponse(replay.Response);
                    context.CreatedIds.Clear();
                    foreach (var item in replay.CreatedIds)
                        context.CreatedIds[item.Key] = item.Value;
                    return replay.Response;
                }
            }

            var encoded = await invoke(cancellationToken).ConfigureAwait(false);
            ValidateEncodedResponse(encoded);
            if (MustRollBack(encoded))
            {
                await RollBackAsync(transaction).ConfigureAwait(false);
                await _blobEffects.RollbackAsync(blobEffectMarker).ConfigureAwait(false);
                RestoreInvocationState(context, createdIds, postCommitMarker);
                context.DiscardPresentationEffects(presentationMarker);
                return encoded;
            }

            if (receiptKey is not null)
            {
                var result = JsonSerializer.SerializeToUtf8Bytes(new JmapReplayState(
                    encoded, context.CreatedIds.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)), ReceiptJsonOptions);
                await _receipts!.SaveAsync(receiptKey,
                    new ApplicationReceiptContent(result, context.PresentationEffectsSince(presentationMarker)),
                    cancellationToken).ConfigureAwait(false);
            }

            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await _blobEffects.CommitAsync(blobEffectMarker).ConfigureAwait(false);
            var actions = context.TakePostCommitActions(postCommitMarker);
            foreach (var action in actions)
            {
                try
                {
                    // The database commit makes these actions durable work.
                    // A client disconnect must not prevent push verification
                    // (or any future committed external effect) from running.
                    await action(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "A JMAP post-commit action failed");
                }
            }
            return encoded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollBackAsync(transaction).ConfigureAwait(false);
            await CompleteBlobRollbackAsync(blobEffectMarker, commitAttempted).ConfigureAwait(false);
            RestoreInvocationState(context, createdIds, postCommitMarker);
            context.DiscardPresentationEffects(presentationMarker);
            throw;
        }
        catch (Exception exception)
        {
            await RollBackAsync(transaction).ConfigureAwait(false);
            await CompleteBlobRollbackAsync(blobEffectMarker, commitAttempted).ConfigureAwait(false);
            RestoreInvocationState(context, createdIds, postCommitMarker);
            context.DiscardPresentationEffects(presentationMarker);
            _logger.LogError(exception, "Mail operation {Operation} failed", operation);
            return EncodeResponse(JmapMethodResponse.Error("serverFail"));
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
            context.DiscardPresentationEffects(presentationMarker);
        }
    }

    private static MailOperationResponse EncodeResponse(JmapMethodResponse response) => new(
        response.Operation, ApplicationValueCodec.Encode(response.Arguments),
        response.AdditionalResponses?.Select(EncodeResponse).ToArray());

    private static void ValidateEncodedResponse(MailOperationResponse response)
    {
        if (response is null || !Enum.IsDefined(response.Operation) || response.Operation is MailOperationKind.None or MailOperationKind.Echo
            || response.Data is null || ApplicationValueCodec.Decode(response.Data) is not JsonObject)
            throw new InvalidOperationException("A mail operation receipt contains an invalid result.");
        if (response.AdditionalResults is not null)
        {
            foreach (var additional in response.AdditionalResults)
                ValidateEncodedResponse(additional);
        }
    }

    private async Task CompleteBlobRollbackAsync(int marker, bool commitAttempted)
    {
        if (commitAttempted)
        {
            // A failed commit can have an ambiguous outcome. Retaining an object is safer
            // than deleting content that a committed row may reference.
            _blobEffects.Discard(marker);
            return;
        }
        await _blobEffects.RollbackAsync(marker).ConfigureAwait(false);
    }

    private static bool MustRollBack(MailOperationResponse response) =>
        response.Operation == MailOperationKind.Failure
        && (ApplicationValueCodec.Decode(response.Data) is not JsonObject arguments
            || !arguments.TryGetPropertyValue("type", out var typeNode)
            || typeNode is not JsonValue typeValue
            || !typeValue.TryGetValue<string>(out var type)
            || !string.Equals(type, "serverPartialFail", StringComparison.Ordinal));

    private async Task RollBackAsync(IDbContextTransaction? transaction)
    {
        if (transaction is null)
            return;
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not roll back a failed JMAP method");
        }
    }

    private void RestoreInvocationState(
        JmapInvocationContext context,
        IReadOnlyDictionary<string, string> createdIds,
        int postCommitMarker)
    {
        context.CreatedIds.Clear();
        foreach (var item in createdIds)
            context.CreatedIds[item.Key] = item.Value;
        context.DiscardPostCommitActions(postCommitMarker);
        _database.ChangeTracker.Clear();
    }

    private static MailOperationKind ValidRegisteredOperation(MailOperationKind operation, MailFeature feature) =>
        Enum.IsDefined(operation) && operation is not (MailOperationKind.None or MailOperationKind.Failure or MailOperationKind.Echo)
            && Enum.IsDefined(feature) && feature != MailFeature.Unsupported
            ? operation : throw new ArgumentException("A handler must register a supported mail operation.", nameof(operation));

    private static void ValidateResponse(JmapMethodResponse response)
    {
        if (response is null || response.Arguments is null || !Enum.IsDefined(response.Operation)
            || response.Operation is MailOperationKind.None or MailOperationKind.Echo)
            throw new InvalidOperationException("A handler returned an unsupported mail operation result.");
        if (response.AdditionalResponses is not null)
        {
            foreach (var additional in response.AdditionalResponses)
                ValidateResponse(additional);
        }
    }

    private static MailApplicationException NotRequest(string detail) =>
        new(new MailApplicationFailure(MailFailureKind.MalformedBatch, detail));

}
