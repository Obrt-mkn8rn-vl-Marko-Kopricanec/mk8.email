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
    private readonly Dictionary<MailOperationKind, IJmapMethod> _methods;
    private readonly JmapAccountProfileService _sessions;
    private readonly IMailFolderReader _folderReader;
    private readonly IMailFolderMutationService? _folderMutationService;
    private readonly IMailChangesReader _changesReader;
    private readonly IMailAddressBookReader _addressBookReader;
    private readonly IMailAddressBookMutationService? _addressBookMutationService;
    private readonly IMailIdentityReader _identityReader;
    private readonly IMailIdentityMutationService? _identityMutationService;
    private readonly IMailVacationReader? _vacationReader;
    private readonly IMailPushSubscriptionReader _pushReader;
    private readonly IMailPushSubscriptionMutationService? _pushMutationService;
    private readonly IJmapPushPresentationClient? _pushDelivery;
    private readonly IMailThreadReader _threadReader;
    private readonly IMailSubmissionReader? _submissionReader;
    private readonly IMailBlobCopyService? _blobCopyService;
    private readonly IMailVacationMutator? _vacationMutator;
    private readonly IMailSubmissionQueryService? _submissionQueryService;
    private readonly IMailFolderQueryService? _folderQueryService;
    private readonly IMailMessageQueryService? _messageQueryService;
    private readonly IMailMessageProjectionService? _messageProjectionService;
    private readonly IMailMessageMutationService? _messageMutationService;
    private readonly IMailSubmissionMutationService? _submissionMutationService;
    private readonly IMailSearchSnippetService? _searchSnippetService;
    private readonly IMailContactCopyService? _contactCopyService;
    private readonly IMailContactQueryService? _contactQueryService;
    private readonly IMailContactReader? _contactReader;
    private readonly IMailContactMutationService? _contactMutationService;
    private readonly IMailImportService? _importService;
    private readonly IMailCopyService? _copyService;
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
    private static readonly Action<ILogger, string, Exception?> PushVerificationWarning =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1201, "PushVerificationDelivery"),
            "Could not deliver JMAP push verification for {PushSubscriptionId}");

    public JmapRequestProcessor(
        IEnumerable<IJmapMethod> methods,
        JmapAccountProfileService sessions,
        EmailDbContext database,
        EnvironmentConfig environment,
        LargeObjectTransactionEffects blobEffects,
        ILogger<JmapRequestProcessor> logger,
        ApplicationOperationReceiptStore? receipts = null)
        : this(methods, sessions, database, environment, blobEffects, logger, receipts,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null)
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
        IMailAddressBookMutationService? addressBookMutationService,
        IMailIdentityReader? identityReader,
        IMailIdentityMutationService? identityMutationService,
        IMailVacationReader? vacationReader,
        IMailPushSubscriptionReader? pushReader,
        IMailThreadReader? threadReader,
        IMailSubmissionReader? submissionReader,
        IMailBlobCopyService? blobCopyService,
        IMailVacationMutator? vacationMutator,
        IMailSubmissionQueryService? submissionQueryService,
        IMailFolderQueryService? folderQueryService,
        IMailMessageQueryService? messageQueryService,
        IMailSearchSnippetService? searchSnippetService,
        IMailContactCopyService? contactCopyService,
        IMailContactQueryService? contactQueryService,
        IMailContactReader? contactReader,
        IMailImportService? importService,
        IMailCopyService? copyService,
        IMailPushSubscriptionMutationService? pushMutationService,
        IJmapPushPresentationClient? pushDelivery,
        IMailContactMutationService? contactMutationService,
        IMailFolderMutationService? folderMutationService,
        IMailMessageProjectionService? messageProjectionService,
        IMailMessageMutationService? messageMutationService,
        IMailSubmissionMutationService? submissionMutationService)
    {
        _methods = methods.ToDictionary(method => ValidRegisteredOperation(method.Operation, method.Feature));
        _sessions = sessions;
        var accountService = new JmapAccountService(database);
        var stateService = new JmapStateService(database, accountService);
        _folderReader = folderReader ?? new MailFolderReader(accountService,
            new JmapMailboxStore(database), stateService, environment);
        _folderMutationService = folderMutationService;
        _messageProjectionService = messageProjectionService;
        _messageMutationService = messageMutationService;
        _submissionMutationService = submissionMutationService;
        _changesReader = changesReader ?? new MailChangesReader(accountService, stateService,
            new JmapIdentityService(database, accountService), database, environment);
        _addressBookReader = addressBookReader ?? new MailAddressBookReader(database,
            accountService, stateService, environment);
        _addressBookMutationService = addressBookMutationService;
        _identityReader = identityReader ?? new MailIdentityReader(database, accountService,
            new JmapIdentityService(database, accountService), stateService, environment);
        _identityMutationService = identityMutationService;
        _vacationReader = vacationReader;
        _pushReader = pushReader ?? new MailPushSubscriptionReader(database, environment);
        _pushMutationService = pushMutationService;
        _pushDelivery = pushDelivery;
        _contactMutationService = contactMutationService;
        _threadReader = threadReader ?? new MailThreadReader(database, accountService, stateService);
        _submissionReader = submissionReader;
        _blobCopyService = blobCopyService;
        _vacationMutator = vacationMutator;
        _submissionQueryService = submissionQueryService;
        _folderQueryService = folderQueryService;
        _messageQueryService = messageQueryService;
        _searchSnippetService = searchSnippetService;
        _contactCopyService = contactCopyService;
        _contactQueryService = contactQueryService;
        (_contactReader, _importService) = (contactReader, importService);
        _copyService = copyService;
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
        else if (command.Operation == MailOperationKind.MutateFolders && features.Contains(MailFeature.Messages))
            response = await ExecuteFolderMutationAsync(command, context, receiptKey,
                cancellationToken).ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadMessages && features.Contains(MailFeature.Messages))
            response = await ExecuteMessageReadAsync(command, context, user, receiptKey,
                cancellationToken).ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ParseMessages && features.Contains(MailFeature.Messages))
            response = await ExecuteMessageParseAsync(command, context, user, receiptKey,
                cancellationToken).ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.MutateMessages && features.Contains(MailFeature.Messages))
            response = await ExecuteMessageMutationAsync(command, context, user, receiptKey,
                cancellationToken).ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.MutateSubmissions && features.Contains(MailFeature.Submission))
            response = await ExecuteSubmissionMutationAsync(command, context, user, receiptKey,
                cancellationToken).ConfigureAwait(false);
        else if (MailChangeOperations.TryGetFeature(command.Operation, out var changeFeature)
            && features.Contains(changeFeature))
            response = await ExecuteChangesAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadAddressBooks && features.Contains(MailFeature.Contacts))
            response = await ExecuteAddressBooksAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.MutateAddressBooks && features.Contains(MailFeature.Contacts))
            response = await ExecuteAddressBookMutationAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadSenderIdentities && features.Contains(MailFeature.Submission))
            response = await ExecuteIdentitiesAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.MutateSenderIdentities && features.Contains(MailFeature.Submission))
            response = await ExecuteIdentityMutationAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadVacationSettings && features.Contains(MailFeature.AutomaticReplies))
            response = await ExecuteVacationAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadNotificationSubscriptions && features.Contains(MailFeature.Basic))
            response = await ExecutePushSubscriptionsAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.MutateNotificationSubscriptions && features.Contains(MailFeature.Basic))
            response = await ExecutePushMutationAsync(command, context, user, receiptKey, cancellationToken)
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
        else if (command.Operation == MailOperationKind.FindMessages && features.Contains(MailFeature.Messages))
            response = await ExecuteMessageQueryAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.FindMessageChanges && features.Contains(MailFeature.Messages))
            response = await ExecuteMessageQueryChangesAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadSearchSnippets && features.Contains(MailFeature.Messages))
            response = await ExecuteSearchSnippetsAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.CopyContacts && features.Contains(MailFeature.Contacts))
            response = await ExecuteContactCopyAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.FindContacts && features.Contains(MailFeature.Contacts))
            response = await ExecuteContactQueryAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.FindContactChanges && features.Contains(MailFeature.Contacts))
            response = await ExecuteContactQueryChangesAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ReadContacts && features.Contains(MailFeature.Contacts))
            response = await ExecuteContactsAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.MutateContacts && features.Contains(MailFeature.Contacts))
            response = await ExecuteContactMutationAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.ImportMessages && features.Contains(MailFeature.Messages))
            response = await ExecuteImportAsync(command, context, user, receiptKey, cancellationToken)
                .ConfigureAwait(false);
        else if (command.Operation == MailOperationKind.CopyMessages && features.Contains(MailFeature.Messages))
            response = await ExecuteCopyAsync(command, context, user, receiptKey, cancellationToken)
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

    private Task<MailOperationResponse> ExecuteFolderMutationAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var mutation = ParseFolderMutationCommand(command.Arguments, context);
        var service = _folderMutationService
            ?? throw new InvalidOperationException("The folder mutation service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.MutateAsync(mutation, context, token).ConfigureAwait(false);
            if (result.Status == MailFolderMutationStatus.Ok)
            {
                if (result.Created.Count != mutation.Creates.Count)
                    throw new InvalidOperationException("The folder mutation returned incomplete creations.");
                for (var index = 0; index < result.Created.Count; index++)
                {
                    var item = result.Created[index];
                    if (!string.Equals(item.CreationId, mutation.Creates[index].CreationId,
                            StringComparison.Ordinal))
                        throw new InvalidOperationException("The folder mutation returned inconsistent creations.");
                    if (item.Folder is { } folder)
                        context.CreatedIds[item.CreationId] = JmapId.Mailbox(folder.Id);
                }
            }
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The folder mutation service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteMessageReadAsync(
        MailOperationCommand command, JmapInvocationContext context,
        AuthenticatedMailUser user, ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var read = ParseMessageReadCommand(command.Arguments);
        var service = _messageProjectionService
            ?? throw new InvalidOperationException("The message projection service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.ReadAsync(read, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The message reader returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteMessageParseAsync(
        MailOperationCommand command, JmapInvocationContext context,
        AuthenticatedMailUser user, ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var parse = ParseMessageParseCommand(command.Arguments);
        var service = _messageProjectionService
            ?? throw new InvalidOperationException("The message projection service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.ParseAsync(parse, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The message parser returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteMessageMutationAsync(
        MailOperationCommand command, JmapInvocationContext context,
        AuthenticatedMailUser user, ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var mutation = ParseMessageMutationCommand(command.Arguments, context);
        var service = _messageMutationService
            ?? throw new InvalidOperationException("The message mutation service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.MutateAsync(mutation, user, context, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The message mutator returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteSubmissionMutationAsync(
        MailOperationCommand command, JmapInvocationContext context,
        AuthenticatedMailUser user, ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var mutation = ParseSubmissionMutationCommand(command.Arguments, context);
        var service = _submissionMutationService
            ?? throw new InvalidOperationException("The submission mutation service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.MutateAsync(mutation, user, context, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The submission mutator returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
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

    private Task<MailOperationResponse> ExecuteAddressBookMutationAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var mutation = ParseAddressBookMutationCommand(command.Arguments);
        var service = _addressBookMutationService
            ?? throw new InvalidOperationException("The address-book mutation service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.MutateAsync(mutation, user, token).ConfigureAwait(false);
            if (result.Status == MailAddressBookMutationStatus.Ok)
            {
                if (result.Created.Count != mutation.Creates.Count)
                    throw new InvalidOperationException("The address-book mutation returned incomplete creations.");
                for (var index = 0; index < result.Created.Count; index++)
                {
                    var item = result.Created[index];
                    if (!string.Equals(item.CreationId, mutation.Creates[index].CreationId, StringComparison.Ordinal))
                        throw new InvalidOperationException("The address-book mutation returned inconsistent creations.");
                    if (item.Error == MailAddressBookMutationError.None && item.Book is { } book)
                        context.CreatedIds[item.CreationId] = JmapId.AddressBook(book.Id);
                }
            }
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The address-book mutation service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
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

    private Task<MailOperationResponse> ExecuteIdentityMutationAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var mutation = ParseIdentityMutationCommand(command.Arguments);
        var service = _identityMutationService
            ?? throw new InvalidOperationException("The identity mutation service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.MutateAsync(mutation, user, token).ConfigureAwait(false);
            if (result.Status == MailIdentityMutationStatus.Ok)
            {
                if (result.Created.Count != mutation.Creates.Count)
                    throw new InvalidOperationException("The identity mutation returned incomplete creations.");
                for (var index = 0; index < result.Created.Count; index++)
                {
                    var item = result.Created[index];
                    if (!string.Equals(item.CreationId, mutation.Creates[index].CreationId, StringComparison.Ordinal))
                        throw new InvalidOperationException("The identity mutation returned inconsistent creations.");
                    if (item.Error == MailIdentityMutationError.None && item.IdentityId is { } id)
                        context.CreatedIds[item.CreationId] = JmapId.Identity(id);
                }
            }
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The identity mutation service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
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

    private Task<MailOperationResponse> ExecutePushMutationAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var mutation = ParsePushMutationCommand(command);
        var service = _pushMutationService
            ?? throw new InvalidOperationException("The push-subscription mutation service is not configured.");
        var delivery = _pushDelivery
            ?? throw new InvalidOperationException("Push verification delivery is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var execution = await service.MutateAsync(mutation, user, token).ConfigureAwait(false);
            if (execution.Result.Created.Count != mutation.Creates.Count)
                throw new InvalidOperationException("The push mutation returned incomplete creations.");
            for (var index = 0; index < execution.Result.Created.Count; index++)
            {
                var outcome = execution.Result.Created[index];
                if (!string.Equals(outcome.CreationId, mutation.Creates[index].CreationId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The push mutation returned inconsistent creations.");
                if (outcome.Error == MailPushSubscriptionMutationError.None
                    && outcome.SubscriptionId is { } id)
                    context.CreatedIds[outcome.CreationId] = $"P{id:N}";
            }
            foreach (var verification in execution.Verifications)
            {
                if (_database.Database.IsRelational())
                {
                    var effect = delivery.CreateVerificationRequest(verification.Url, verification.KeysJson,
                        verification.ExpiresAt, verification.Message);
                    if (effect is not null) context.AddPresentationEffect(effect);
                }
                else context.AddPostCommitAction(async postCommitToken =>
                {
                    try
                    {
                        await delivery.EnqueueVerificationAsync(verification.Url, verification.KeysJson,
                            verification.ExpiresAt, verification.Message, postCommitToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        PushVerificationWarning(_logger, verification.Message.SubscriptionId!, exception);
                    }
                });
            }
            var data = JsonSerializer.SerializeToNode(execution.Result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The push mutation service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
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

    private Task<MailOperationResponse> ExecuteMessageQueryAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var query = ParseMessageQueryCommand(command.Arguments);
        var service = _messageQueryService
            ?? throw new InvalidOperationException("The message query service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.QueryAsync(query, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The message query service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteMessageQueryChangesAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var query = ParseMessageQueryChangesCommand(command.Arguments);
        var service = _messageQueryService
            ?? throw new InvalidOperationException("The message query service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.QueryChangesAsync(query, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The message query service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteSearchSnippetsAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var request = ParseSearchSnippetCommand(command.Arguments);
        var service = _searchSnippetService
            ?? throw new InvalidOperationException("The search snippet service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.ReadAsync(request, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The search snippet service returned an incomplete result.");
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

    private Task<MailOperationResponse> ExecuteContactQueryAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var query = ParseContactQueryCommand(command.Arguments);
        var service = _contactQueryService
            ?? throw new InvalidOperationException("The contact query service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.QueryAsync(query, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The contact query service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteContactQueryChangesAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var query = ParseContactQueryChangesCommand(command.Arguments);
        var service = _contactQueryService
            ?? throw new InvalidOperationException("The contact query service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.QueryChangesAsync(query, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The contact query service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteContactsAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var read = ParseContactReadCommand(command.Arguments);
        var service = _contactReader
            ?? throw new InvalidOperationException("The contact reader is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.ReadAsync(read, user, token).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The contact reader returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteContactMutationAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var mutation = ParseContactMutationCommand(command);
        var service = _contactMutationService
            ?? throw new InvalidOperationException("The contact mutation service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.MutateAsync(mutation, user, token).ConfigureAwait(false);
            if (result.Status == MailContactMutationStatus.Ok)
            {
                if (result.Created.Count != mutation.Creates.Count)
                    throw new InvalidOperationException("The contact mutation returned incomplete creations.");
                for (var index = 0; index < result.Created.Count; index++)
                {
                    var item = result.Created[index];
                    if (!string.Equals(item.CreationId, mutation.Creates[index].CreationId,
                            StringComparison.Ordinal))
                        throw new InvalidOperationException("The contact mutation returned inconsistent creations.");
                    if (item.Error == MailContactMutationError.None && item.CardId is { } id)
                        context.CreatedIds[item.CreationId] = $"C{id:N}";
                }
            }
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The contact mutation service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteImportAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var import = ParseImportCommand(command.Arguments);
        var service = _importService
            ?? throw new InvalidOperationException("The import service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.ImportAsync(import, user, token).ConfigureAwait(false);
            if (result.Status == MailImportStatus.Ok)
            {
                if (result.Items.Count != import.Items.Count)
                    throw new InvalidOperationException("The import service returned incomplete outcomes.");
                for (var index = 0; index < import.Items.Count; index++)
                {
                    var outcome = result.Items[index];
                    if (outcome is null || !string.Equals(outcome.CreationId,
                            import.Items[index].CreationId, StringComparison.Ordinal))
                        throw new InvalidOperationException("The import service returned inconsistent outcomes.");
                    if (outcome.Error == MailImportItemError.None)
                    {
                        if (outcome.EmailId is null || outcome.EmailId == Guid.Empty)
                            throw new InvalidOperationException("The import service returned an invalid message identity.");
                        context.CreatedIds[outcome.CreationId] = JmapId.Email(outcome.EmailId.Value);
                    }
                }
            }
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The import service returned an incomplete result.");
            return new MailOperationResponse(command.Operation, ApplicationValueCodec.Encode(data));
        }, receiptKey, cancellationToken);
    }

    private Task<MailOperationResponse> ExecuteCopyAsync(
        MailOperationCommand command,
        JmapInvocationContext context,
        AuthenticatedMailUser user,
        ApplicationReceiptKey? receiptKey,
        CancellationToken cancellationToken)
    {
        var copy = ParseCopyCommand(command.Arguments);
        var service = _copyService
            ?? throw new InvalidOperationException("The copy service is not configured.");
        return InvokeAtomicallyAsync(command.Operation, context, async token =>
        {
            var result = await service.CopyAsync(copy, user, token).ConfigureAwait(false);
            if (result.Status == MailCopyStatus.Ok)
            {
                if (result.Items.Count != copy.Items.Count)
                    throw new InvalidOperationException("The copy service returned incomplete outcomes.");
                for (var index = 0; index < copy.Items.Count; index++)
                {
                    var outcome = result.Items[index];
                    if (outcome is null || !string.Equals(outcome.CreationId,
                            copy.Items[index].CreationId, StringComparison.Ordinal))
                        throw new InvalidOperationException("The copy service returned inconsistent outcomes.");
                    if (outcome.Error == MailCopyItemError.None)
                    {
                        if (outcome.EmailId is null || outcome.EmailId == Guid.Empty)
                            throw new InvalidOperationException("The copy service returned an invalid message identity.");
                        context.CreatedIds[outcome.CreationId] = JmapId.Email(outcome.EmailId.Value);
                    }
                }
            }
            var data = JsonSerializer.SerializeToNode(result, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The copy service returned an incomplete result.");
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

    private MailMessageReadCommand ParseMessageReadCommand(JsonObject arguments)
    {
        if (arguments.Count != 3 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("messageIds") || !arguments.ContainsKey("includeText"))
            throw NotRequest("The message read command has an invalid shape.");
        try
        {
            var read = JsonSerializer.Deserialize<MailMessageReadCommand>(arguments,
                StrictReceiptJsonOptions)
                ?? throw NotRequest("The message read command is missing.");
            if (read.MessageIds is not null && (read.MessageIds.Count > _environment.Jmap.MaxObjectsInGet
                    || read.MessageIds.Contains(Guid.Empty)))
                throw NotRequest("The message read values are invalid.");
            return read;
        }
        catch (JsonException)
        {
            throw NotRequest("The message read command contains invalid values.");
        }
    }

    private MailMessageParseCommand ParseMessageParseCommand(JsonObject arguments)
    {
        if (arguments.Count != 3 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("blobIds") || !arguments.ContainsKey("includeText"))
            throw NotRequest("The message parse command has an invalid shape.");
        try
        {
            var parse = JsonSerializer.Deserialize<MailMessageParseCommand>(arguments,
                StrictReceiptJsonOptions)
                ?? throw NotRequest("The message parse command is missing.");
            if (parse.BlobIds is null
                || parse.BlobIds.Count > _environment.Jmap.MaxObjectsInGet
                || parse.BlobIds.Any(id => id is null || !JmapId.IsValidId(id))
                || parse.BlobIds.Distinct(StringComparer.Ordinal).Count() != parse.BlobIds.Count)
                throw NotRequest("The message parse values are invalid.");
            return parse;
        }
        catch (JsonException)
        {
            throw NotRequest("The message parse command contains invalid values.");
        }
    }

    private MailMessageMutationCommand ParseMessageMutationCommand(
        JsonObject arguments, JmapInvocationContext context)
    {
        if (arguments.Count != 5 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("ifInState") || !arguments.ContainsKey("creates")
            || !arguments.ContainsKey("updates") || !arguments.ContainsKey("destroys"))
            throw NotRequest("The message mutation command has an invalid shape.");
        try
        {
            var mutation = JsonSerializer.Deserialize<MailMessageMutationCommand>(arguments,
                StrictReceiptJsonOptions)
                ?? throw NotRequest("The message mutation command is missing.");
            if (mutation.Creates is null || mutation.Updates is null || mutation.Destroys is null
                || mutation.Creates.Count + mutation.Updates.Count + mutation.Destroys.Count
                    > _environment.Jmap.MaxObjectsInSet
                || mutation.Creates.Any(item => item is null || item.CreationId is null
                    || !JmapId.IsValidId(item.CreationId) || item.Draft is null
                    || !MailMimeDraftValidator.IsValid(item.Draft, context))
                || mutation.Creates.Select(item => item.CreationId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Creates.Count
                || mutation.Updates.Any(item => item is null || !ValidMessageReference(item.RequestedId, context)
                    || item.Patch is null || !MailMessagePatchValidator.IsValid(item.Patch))
                || mutation.Updates.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Updates.Count
                || mutation.Destroys.Any(item => item is null || !ValidMessageReference(item.RequestedId, context))
                || mutation.Destroys.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Destroys.Count)
                throw NotRequest("The message mutation values are invalid.");
            return mutation;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw NotRequest("The message mutation command contains invalid values.");
        }
    }

    private static bool ValidMessageReference(string? value, JmapInvocationContext context) =>
        value is not null && (JmapId.IsValidId(value)
            || context.TryGetReferenceKey(value, out var key) && JmapId.IsValidId(key));

    private MailSubmissionMutationCommand ParseSubmissionMutationCommand(
        JsonObject arguments, JmapInvocationContext context)
    {
        if (arguments.Count != 7 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("ifInState") || !arguments.ContainsKey("creates")
            || !arguments.ContainsKey("updates") || !arguments.ContainsKey("destroys")
            || !arguments.ContainsKey("onSuccessUpdates")
            || !arguments.ContainsKey("onSuccessDestroys"))
            throw NotRequest("The submission mutation command has an invalid shape.");
        try
        {
            var mutation = JsonSerializer.Deserialize<MailSubmissionMutationCommand>(arguments,
                StrictReceiptJsonOptions)
                ?? throw NotRequest("The submission mutation command is missing.");
            if (mutation.Creates is null || mutation.Updates is null || mutation.Destroys is null
                || mutation.OnSuccessUpdates is null || mutation.OnSuccessDestroys is null
                || mutation.Creates.Count + mutation.Updates.Count + mutation.Destroys.Count
                    > _environment.Jmap.MaxObjectsInSet
                || mutation.Creates.Any(item => item is null || !JmapId.IsValidId(item.CreationId)
                    || item.Draft is null || ApplicationValueCodec.Decode(item.Draft) is not JsonObject)
                || mutation.Creates.Select(item => item.CreationId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Creates.Count
                || mutation.Updates.Any(item => item is null
                    || !ValidMessageReference(item.RequestedId, context)
                    || item.Patch is null || ApplicationValueCodec.Decode(item.Patch) is not JsonObject)
                || mutation.Updates.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Updates.Count
                || mutation.Destroys.Any(item => item is null
                    || !ValidMessageReference(item.RequestedId, context))
                || mutation.Destroys.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Destroys.Count
                || mutation.OnSuccessUpdates.Any(item => item is null
                    || !ValidMessageReference(item.RequestedSubmissionId, context)
                    || !MailMessagePatchValidator.ValidFragments(item.Fragments))
                || mutation.OnSuccessUpdates.Select(item => item.RequestedSubmissionId)
                    .Distinct(StringComparer.Ordinal).Count() != mutation.OnSuccessUpdates.Count
                || mutation.OnSuccessDestroys.Any(item => item is null
                    || !ValidMessageReference(item.RequestedSubmissionId, context)))
                throw NotRequest("The submission mutation values are invalid.");
            return mutation;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw NotRequest("The submission mutation command contains invalid values.");
        }
    }

    private MailFolderMutationCommand ParseFolderMutationCommand(
        JsonObject arguments, JmapInvocationContext context)
    {
        if (arguments.Count != 6 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("ifInState") || !arguments.ContainsKey("removeEmailsOnDestroy")
            || !arguments.ContainsKey("creates") || !arguments.ContainsKey("updates")
            || !arguments.ContainsKey("destroys"))
            throw NotRequest("The folder mutation command has an invalid shape.");
        try
        {
            var mutation = JsonSerializer.Deserialize<MailFolderMutationCommand>(arguments,
                StrictReceiptJsonOptions)
                ?? throw NotRequest("The folder mutation command is missing.");
            if (mutation.Creates is null || mutation.Updates is null || mutation.Destroys is null
                || mutation.Creates.Count + mutation.Updates.Count + mutation.Destroys.Count
                    > _environment.Jmap.MaxObjectsInSet
                || mutation.Creates.Any(item => item is null || item.CreationId is null
                    || !JmapId.IsValidId(item.CreationId)
                    || (item.Values is null) == (item.Failure is null)
                    || item.Values is { Name: null } || !ValidFolderInputFailure(item.Failure))
                || mutation.Creates.Select(item => item.CreationId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Creates.Count
                || mutation.Updates.Any(item => item is null || !ValidFolderReference(item.RequestedId, context)
                    || item.Patch is null || item.Patch.Expectations is null
                    || (item.Patch.Fields & ~(MailFolderFields.Name | MailFolderFields.Parent | MailFolderFields.Role
                        | MailFolderFields.SortOrder | MailFolderFields.Subscription)) != MailFolderFields.None
                    || item.Patch.Expectations.Any(expected => expected is null || !Enum.IsDefined(expected.Field))
                    || item.Patch.Values is { Name: null } || !ValidFolderInputFailure(item.Patch.Failure))
                || mutation.Updates.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Updates.Count
                || mutation.Destroys.Any(item => item is null || !ValidFolderReference(item.RequestedId, context))
                || mutation.Destroys.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Destroys.Count)
                throw NotRequest("The folder mutation values are invalid.");
            return mutation;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw NotRequest("The folder mutation command contains invalid values.");
        }
    }

    private static bool ValidFolderReference(string? value, JmapInvocationContext context) =>
        value is not null && (JmapId.IsValidId(value)
            || context.TryGetReferenceKey(value, out var key) && JmapId.IsValidId(key));

    private static bool ValidFolderInputFailure(MailFolderMutationFailure? failure) =>
        failure is null || failure.Error is MailFolderMutationError.InvalidProperties or MailFolderMutationError.InvalidPatch;

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

    private MailAddressBookMutationCommand ParseAddressBookMutationCommand(JsonObject arguments)
    {
        if (arguments.Count != 8 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("accountReferenceEligible") || !arguments.ContainsKey("ifInState")
            || !arguments.ContainsKey("onDestroyRemoveContents")
            || !arguments.ContainsKey("onSuccessSetIsDefault") || !arguments.ContainsKey("creates")
            || !arguments.ContainsKey("updates") || !arguments.ContainsKey("destroys"))
            throw NotRequest("The address-book mutation command has an invalid shape.");
        try
        {
            var mutation = JsonSerializer.Deserialize<MailAddressBookMutationCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The address-book mutation command is missing.");
            if (mutation.Creates is null || mutation.Updates is null || mutation.Destroys is null
                || mutation.Creates.Count + mutation.Updates.Count + mutation.Destroys.Count
                    > _environment.Jmap.MaxObjectsInSet
                || mutation.Creates.Any(item => item is null || item.CreationId is null
                    || !JmapId.IsValidId(item.CreationId))
                || mutation.Creates.Select(item => item.CreationId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Creates.Count
                || mutation.Updates.Any(item => item is null || !ValidBookTarget(item.RequestedId, item.Target))
                || mutation.Updates.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Updates.Count
                || mutation.Destroys.Any(item => item is null || !ValidBookTarget(item.RequestedId, item.Target))
                || mutation.Destroys.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Destroys.Count
                || mutation.OnSuccessSetIsDefault is { } target && !ValidBookTarget(null, target))
                throw NotRequest("The address-book mutation values are invalid.");
            return mutation;
        }
        catch (JsonException)
        {
            throw NotRequest("The address-book mutation command contains invalid values.");
        }
    }

    private static bool ValidBookTarget(string? requestedId, MailAddressBookTarget? target) =>
        target is not null
        && (requestedId is null || JmapId.IsValidId(requestedId)
            || requestedId.Length > 1 && requestedId[0] == '#' && JmapId.IsValidId(requestedId[1..]))
        && target.ExistingId != Guid.Empty
        && (target.CreatedKey is null || JmapId.IsValidId(target.CreatedKey))
        && (target.ExistingId is null || target.CreatedKey is null);

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

    private MailIdentityMutationCommand ParseIdentityMutationCommand(JsonObject arguments)
    {
        if (arguments.Count != 5 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("ifInState") || !arguments.ContainsKey("creates")
            || !arguments.ContainsKey("updates") || !arguments.ContainsKey("destroys"))
            throw NotRequest("The identity mutation command has an invalid shape.");
        try
        {
            var mutation = JsonSerializer.Deserialize<MailIdentityMutationCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The identity mutation command is missing.");
            if (mutation.Creates is null || mutation.Updates is null || mutation.Destroys is null
                || mutation.Creates.Count + mutation.Updates.Count + mutation.Destroys.Count
                    > _environment.Jmap.MaxObjectsInSet
                || mutation.Creates.Any(item => item is null || item.CreationId is null
                    || !JmapId.IsValidId(item.CreationId))
                || mutation.Creates.Select(item => item.CreationId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Creates.Count
                || mutation.Updates.Any(item => item is null || !ValidIdentityTarget(item.RequestedId, item.Target))
                || mutation.Updates.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Updates.Count
                || mutation.Destroys.Any(item => item is null || !ValidIdentityTarget(item.RequestedId, item.Target))
                || mutation.Destroys.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Destroys.Count)
                throw NotRequest("The identity mutation values are invalid.");
            return mutation;
        }
        catch (JsonException)
        {
            throw NotRequest("The identity mutation command contains invalid values.");
        }
    }

    private static bool ValidIdentityTarget(string? requestedId, MailIdentityTarget? target) =>
        requestedId is not null && target is not null
        && (JmapId.IsValidId(requestedId)
            || requestedId.Length > 1 && requestedId[0] == '#' && JmapId.IsValidId(requestedId[1..]))
        && target.ExistingId != Guid.Empty
        && (target.CreatedKey is null || JmapId.IsValidId(target.CreatedKey))
        && (target.ExistingId is null || target.CreatedKey is null);

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

    private MailPushSubscriptionMutationCommand ParsePushMutationCommand(MailOperationCommand command)
    {
        var arguments = command.Arguments;
        if (arguments.Count != 3 || !arguments.ContainsKey("creates")
            || !arguments.ContainsKey("updates") || !arguments.ContainsKey("destroys"))
            throw NotRequest("The push mutation command has an invalid shape.");
        try
        {
            var mutation = JsonSerializer.Deserialize<MailPushSubscriptionMutationCommand>(arguments,
                StrictReceiptJsonOptions) ?? throw NotRequest("The push mutation command is missing.");
            if (mutation.Creates is null || mutation.Updates is null || mutation.Destroys is null
                || mutation.Creates.Count + mutation.Updates.Count + mutation.Destroys.Count
                    > _environment.Jmap.MaxObjectsInSet
                || mutation.Creates.Any(item => item is null || item.CreationId is null
                    || !JmapId.IsValidId(item.CreationId))
                || mutation.Creates.Select(item => item.CreationId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Creates.Count
                || mutation.Updates.Any(item => item is null || !ValidPushTarget(item.RequestedId, item.Target,
                        command.KnownEntities)
                    || item.Patch is not null && item.Patch.UnknownProperties is null)
                || mutation.Updates.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Updates.Count
                || mutation.Destroys.Any(item => item is null || !ValidPushTarget(item.RequestedId, item.Target,
                    command.KnownEntities))
                || mutation.Destroys.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Destroys.Count)
                throw NotRequest("The push mutation values are invalid.");
            return mutation;
        }
        catch (JsonException)
        {
            throw NotRequest("The push mutation command contains invalid values.");
        }
    }

    private static bool ValidPushTarget(string? requestedId, MailPushSubscriptionTarget? target,
        IReadOnlyDictionary<string, string>? knownEntities)
    {
        if (requestedId is null || target is null || target.ExistingId == Guid.Empty) return false;
        if (requestedId.Length > 1 && requestedId[0] == '#')
        {
            var key = requestedId[1..];
            if (!JmapId.IsValidId(key)) return false;
            if (knownEntities is not null && knownEntities.TryGetValue(key, out var resolved))
                return target.CreatedKey is null && target.ExistingId == ParsedPushId(resolved);
            return target.ExistingId is null && string.Equals(target.CreatedKey, key, StringComparison.Ordinal);
        }
        if (!JmapId.IsValidId(requestedId) || target.CreatedKey is not null) return false;
        return target.ExistingId == ParsedPushId(requestedId);
    }

    private static Guid? ParsedPushId(string value) => JmapId.TryParsePushSubscription(value, out var id)
        ? id : null;

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

    private MailMessageQueryCommand ParseMessageQueryCommand(JsonObject arguments)
    {
        if (arguments.Count != 7 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("criteria") || !arguments.ContainsKey("checkAccountOnly")
            || !arguments.ContainsKey("position") || !arguments.ContainsKey("anchorId")
            || !arguments.ContainsKey("anchorOffset") || !arguments.ContainsKey("limit"))
            throw NotRequest("The message query command has an invalid shape.");
        try
        {
            var query = JsonSerializer.Deserialize<MailMessageQueryCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The message query command is missing.");
            ValidateMessageCriteria(query.Criteria);
            if (query.Position is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L
                || query.AnchorOffset is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L
                || query.AnchorId is not null && !JmapId.IsValidId(query.AnchorId)
                || query.Limit < 0 || query.Limit > _environment.Jmap.MaxObjectsInGet)
                throw NotRequest("The message query window is invalid.");
            return query;
        }
        catch (JsonException)
        {
            throw NotRequest("The message query command contains invalid values.");
        }
    }

    private static MailMessageQueryChangesCommand ParseMessageQueryChangesCommand(JsonObject arguments)
    {
        if (arguments.Count != 5 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("criteria") || !arguments.ContainsKey("checkAccountOnly")
            || !arguments.ContainsKey("sinceState") || !arguments.ContainsKey("maxChanges"))
            throw NotRequest("The message query-changes command has an invalid shape.");
        try
        {
            var query = JsonSerializer.Deserialize<MailMessageQueryChangesCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The message query-changes command is missing.");
            ValidateMessageCriteria(query.Criteria);
            if (query.SinceState is null || query.MaxChanges is < 0 or > 9_007_199_254_740_991L)
                throw NotRequest("The message query-changes values are invalid.");
            return query;
        }
        catch (JsonException)
        {
            throw NotRequest("The message query-changes command contains invalid values.");
        }
    }

    private MailSearchSnippetCommand ParseSearchSnippetCommand(JsonObject arguments)
    {
        if (arguments.Count != 4 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("checkAccountOnly") || !arguments.ContainsKey("messageIds")
            || !arguments.ContainsKey("terms"))
            throw NotRequest("The search snippet command has an invalid shape.");
        try
        {
            var request = JsonSerializer.Deserialize<MailSearchSnippetCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The search snippet command is missing.");
            if (request.MessageIds is null || request.Terms is null
                || request.MessageIds.Count > _environment.Jmap.MaxObjectsInGet
                || request.MessageIds.Any(id => id == Guid.Empty)
                || request.Terms.Any(term => string.IsNullOrEmpty(term)))
                throw NotRequest("The search snippet values are invalid.");
            return request;
        }
        catch (JsonException)
        {
            throw NotRequest("The search snippet command contains invalid values.");
        }
    }

    private static void ValidateMessageCriteria(MailMessageQueryCriteria? criteria)
    {
        if (criteria?.Sort is null || criteria.Sort.Any(item => item is null
            || !Enum.IsDefined(item.Field) || !Enum.IsDefined(item.Collation)
            || (item.Field is MailMessageSortField.HasKeyword
                or MailMessageSortField.AllInThreadHaveKeyword
                or MailMessageSortField.SomeInThreadHaveKeyword) != (item.Keyword is not null)
            || item.Keyword is not null && !JmapEmailCodec.IsValidKeyword(item.Keyword)))
            throw NotRequest("The message query sort is invalid.");
        ValidateMessageFilter(criteria.Filter, 0);
    }

    private static void ValidateMessageFilter(MailMessageFilter? filter, int depth)
    {
        if (filter is null) return;
        if (depth > 64 || !Enum.IsDefined(filter.Operator))
            throw NotRequest("The message query filter is invalid.");
        if (filter.Operator != MailMessageFilterOperator.Condition)
        {
            if (filter.Conditions is null || filter.Terms is not null)
                throw NotRequest("The message query operator is invalid.");
            foreach (var child in filter.Conditions)
                ValidateMessageFilter(child ?? throw NotRequest("The message query child is missing."), depth + 1);
            return;
        }
        if (filter.Conditions is not null || filter.Terms is null)
            throw NotRequest("The message query condition is invalid.");
        foreach (var term in filter.Terms)
        {
            if (term is null || !Enum.IsDefined(term.Field)
                || term.UtcDate is { Kind: not DateTimeKind.Utc }
                || term.Number is < 0 or > 9_007_199_254_740_991L
                || term.Values is not null && term.Values.Any(id => id is null || !JmapId.IsValidId(id))
                || !ValidMessageTermShape(term))
                throw NotRequest("The message query term is invalid.");
        }
    }

    private static bool ValidMessageTermShape(MailMessageFilterTerm term)
    {
        var field = term.Field;
        if (field == MailMessageFilterField.InMailboxOtherThan)
            return term.Values is not null && term.Text is null && term.HeaderText is null
                && term.UtcDate is null && term.Number is null && term.Flag is null;
        if (field is MailMessageFilterField.Before or MailMessageFilterField.After)
            return term.UtcDate is not null && term.Text is null && term.HeaderText is null
                && term.Values is null && term.Number is null && term.Flag is null;
        if (field is MailMessageFilterField.MinSize or MailMessageFilterField.MaxSize)
            return term.Number is not null && term.Text is null && term.HeaderText is null
                && term.Values is null && term.UtcDate is null && term.Flag is null;
        if (field == MailMessageFilterField.HasAttachment)
            return term.Flag is not null && term.Text is null && term.HeaderText is null
                && term.Values is null && term.UtcDate is null && term.Number is null;
        if (term.Text is null || term.Values is not null || term.UtcDate is not null
            || term.Number is not null || term.Flag is not null) return false;
        if (field == MailMessageFilterField.InMailbox && !JmapId.IsValidId(term.Text)) return false;
        if (field is MailMessageFilterField.AllInThreadHaveKeyword
            or MailMessageFilterField.SomeInThreadHaveKeyword
            or MailMessageFilterField.NoneInThreadHaveKeyword
            or MailMessageFilterField.HasKeyword
            or MailMessageFilterField.NotKeyword
            && !JmapEmailCodec.IsValidKeyword(term.Text)) return false;
        return field == MailMessageFilterField.Header || term.HeaderText is null;
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

    private MailContactQueryCommand ParseContactQueryCommand(JsonObject arguments)
    {
        if (arguments.Count != 10 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("accountReferenceParseable")
            || !arguments.ContainsKey("accountReferenceEligible")
            || !arguments.ContainsKey("criteria") || !arguments.ContainsKey("checkAccountOnly")
            || !arguments.ContainsKey("position") || !arguments.ContainsKey("anchorId")
            || !arguments.ContainsKey("anchorCanMatch") || !arguments.ContainsKey("anchorOffset")
            || !arguments.ContainsKey("limit"))
            throw NotRequest("The contact query command has an invalid shape.");
        try
        {
            var query = JsonSerializer.Deserialize<MailContactQueryCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The contact query command is missing.");
            ValidateContactCriteria(query.Criteria);
            if (query.AccountReferenceEligible && !query.AccountReferenceParseable
                || !query.AccountReferenceParseable && query.AccountId != Guid.Empty
                || query.Position is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L
                || query.AnchorOffset is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L
                || query.AnchorId is null && query.AnchorCanMatch
                || query.Limit < 0 || query.Limit > _environment.Jmap.MaxObjectsInGet)
                throw NotRequest("The contact query values are invalid.");
            return query;
        }
        catch (JsonException)
        {
            throw NotRequest("The contact query command contains invalid values.");
        }
    }

    private static MailContactQueryChangesCommand ParseContactQueryChangesCommand(JsonObject arguments)
    {
        if (arguments.Count != 7 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("accountReferenceParseable")
            || !arguments.ContainsKey("accountReferenceEligible")
            || !arguments.ContainsKey("criteria") || !arguments.ContainsKey("checkAccountOnly")
            || !arguments.ContainsKey("sinceState") || !arguments.ContainsKey("maxChanges"))
            throw NotRequest("The contact query-changes command has an invalid shape.");
        try
        {
            var query = JsonSerializer.Deserialize<MailContactQueryChangesCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The contact query-changes command is missing.");
            ValidateContactCriteria(query.Criteria);
            if (query.AccountReferenceEligible && !query.AccountReferenceParseable
                || !query.AccountReferenceParseable && query.AccountId != Guid.Empty
                || query.SinceState is null
                || query.MaxChanges is < 0 or > 9_007_199_254_740_991L)
                throw NotRequest("The contact query-changes values are invalid.");
            return query;
        }
        catch (JsonException)
        {
            throw NotRequest("The contact query-changes command contains invalid values.");
        }
    }

    private MailContactReadCommand ParseContactReadCommand(JsonObject arguments)
    {
        if (arguments.Count != 4 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("accountReferenceParseable")
            || !arguments.ContainsKey("accountReferenceEligible")
            || !arguments.ContainsKey("cardIds"))
            throw NotRequest("The contact read command has an invalid shape.");
        try
        {
            var read = JsonSerializer.Deserialize<MailContactReadCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The contact read command is missing.");
            if (read.AccountReferenceEligible && !read.AccountReferenceParseable
                || !read.AccountReferenceParseable && read.AccountId != Guid.Empty
                || read.CardIds?.Count > _environment.Jmap.MaxObjectsInGet)
                throw NotRequest("The contact read values are invalid.");
            return read;
        }
        catch (JsonException)
        {
            throw NotRequest("The contact read command contains invalid values.");
        }
    }

    private MailContactMutationCommand ParseContactMutationCommand(MailOperationCommand command)
    {
        var arguments = command.Arguments;
        if (arguments.Count != 8 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("accountReferenceParseable")
            || !arguments.ContainsKey("accountReferenceEligible")
            || !arguments.ContainsKey("ifInState") || !arguments.ContainsKey("addressBookAliases")
            || !arguments.ContainsKey("creates") || !arguments.ContainsKey("updates")
            || !arguments.ContainsKey("destroys"))
            throw NotRequest("The contact mutation command has an invalid shape.");
        try
        {
            var mutation = JsonSerializer.Deserialize<MailContactMutationCommand>(arguments,
                StrictReceiptJsonOptions) ?? throw NotRequest("The contact mutation command is missing.");
            if (mutation.AccountReferenceEligible && !mutation.AccountReferenceParseable
                || !mutation.AccountReferenceParseable && mutation.AccountId != Guid.Empty
                || mutation.AddressBookAliases is null || !ValidBookAliases(mutation.AddressBookAliases,
                    command.KnownEntities)
                || mutation.Creates is null || mutation.Updates is null || mutation.Destroys is null
                || mutation.Creates.Count + mutation.Updates.Count + mutation.Destroys.Count
                    > _environment.Jmap.MaxObjectsInSet
                || mutation.Creates.Any(item => item is null || item.CreationId is null
                    || !JmapId.IsValidId(item.CreationId) || !ValidContactCreate(item))
                || mutation.Creates.Select(item => item.CreationId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Creates.Count
                || mutation.Updates.Any(item => item is null || !ValidContactTarget(item.RequestedId,
                    item.Target, command.KnownEntities) || item.Patch is not null && !ValidContactPatch(item.Patch))
                || mutation.Updates.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Updates.Count
                || mutation.Destroys.Any(item => item is null || !ValidContactTarget(item.RequestedId,
                    item.Target, command.KnownEntities))
                || mutation.Destroys.Select(item => item.RequestedId).Distinct(StringComparer.Ordinal).Count()
                    != mutation.Destroys.Count)
                throw NotRequest("The contact mutation values are invalid.");
            return mutation;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw NotRequest("The contact mutation command contains invalid values.");
        }
    }

    private static bool ValidBookAliases(IReadOnlyDictionary<string, Guid> aliases,
        IReadOnlyDictionary<string, string>? knownEntities)
    {
        foreach (var item in aliases)
        {
            if (item.Key.Length < 2 || item.Key[0] != '#' || !JmapId.IsValidId(item.Key[1..])
                || item.Value == Guid.Empty || knownEntities is null
                || !knownEntities.TryGetValue(item.Key[1..], out var value)
                || !string.Equals(value, $"D{item.Value:N}", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool ValidContactCreate(MailContactCreate item)
    {
        if (item.Card is null || item.AddressBookId == Guid.Empty) return false;
        return ApplicationValueCodec.Decode(item.Card) is JsonObject card
            && !card.ContainsKey("id") && !card.ContainsKey("addressBookIds");
    }

    private static bool ValidContactPatch(IReadOnlyList<MailContactPatchEntry> entries)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null || entry.Path is null || entry.Path.Count == 0
                || entry.Path.Any(string.IsNullOrEmpty) || entry.Value is null) return false;
            _ = ApplicationValueCodec.Decode(entry.Value);
            for (var previous = 0; previous < index; previous++)
                if (ContactPatchPrefix(entries[previous].Path, entry.Path)
                    || ContactPatchPrefix(entry.Path, entries[previous].Path)) return false;
        }
        return true;
    }

    private static bool ContactPatchPrefix(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count > right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool ValidContactTarget(string? requestedId, MailContactTarget? target,
        IReadOnlyDictionary<string, string>? knownEntities)
    {
        if (requestedId is null || target is null || target.ExistingId == Guid.Empty) return false;
        if (requestedId.Length > 1 && requestedId[0] == '#')
        {
            var key = requestedId[1..];
            if (!JmapId.IsValidId(key)) return false;
            if (knownEntities is not null && knownEntities.TryGetValue(key, out var resolved))
                return target.CreatedKey is null && target.ExistingId == ParsedContactId(resolved);
            return target.ExistingId is null && string.Equals(target.CreatedKey, key, StringComparison.Ordinal);
        }
        if (!JmapId.IsValidId(requestedId) || target.CreatedKey is not null) return false;
        return target.ExistingId == ParsedContactId(requestedId);
    }

    private static Guid? ParsedContactId(string value) => JmapId.TryParseContactCard(value, out var id)
        ? id : null;

    private MailImportCommand ParseImportCommand(JsonObject arguments)
    {
        if (arguments.Count != 3 || !arguments.ContainsKey("accountId")
            || !arguments.ContainsKey("ifInState") || !arguments.ContainsKey("items"))
            throw NotRequest("The import command has an invalid shape.");
        try
        {
            var import = JsonSerializer.Deserialize<MailImportCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The import command is missing.");
            if (import.Items is null || import.Items.Count > _environment.Jmap.MaxObjectsInSet
                || import.Items.Any(item => item is null || item.CreationId is null
                    || item.Keywords is null || !Enum.IsDefined(item.MailboxIssue)
                    || !Enum.IsDefined(item.KeywordIssue)
                    || item.ReceivedAt is { Kind: not DateTimeKind.Utc }
                    || item.MailboxIssue == MailMessageMailboxIssue.None
                        && (item.MailboxId is null || item.MailboxId == Guid.Empty)
                    || item.KeywordIssue == MailMessageKeywordIssue.None
                        && (item.Keywords.Count > 128 || item.Keywords.Any(keyword =>
                            keyword is null || !JmapEmailCodec.IsValidKeyword(keyword))))
                || import.Items.Select(item => item.CreationId).Distinct(StringComparer.Ordinal).Count()
                    != import.Items.Count)
                throw NotRequest("The import command contains invalid values.");
            return import;
        }
        catch (JsonException)
        {
            throw NotRequest("The import command contains invalid values.");
        }
    }

    private MailCopyCommand ParseCopyCommand(JsonObject arguments)
    {
        if (arguments.Count != 7 || !arguments.ContainsKey("sourceAccountId")
            || !arguments.ContainsKey("targetAccountId") || !arguments.ContainsKey("ifFromInState")
            || !arguments.ContainsKey("ifInState") || !arguments.ContainsKey("destroyOriginal")
            || !arguments.ContainsKey("destroyFromIfInState") || !arguments.ContainsKey("items"))
            throw NotRequest("The copy command has an invalid shape.");
        try
        {
            var copy = JsonSerializer.Deserialize<MailCopyCommand>(arguments, StrictReceiptJsonOptions)
                ?? throw NotRequest("The copy command is missing.");
            if (copy.Items is null || copy.Items.Count > _environment.Jmap.MaxObjectsInSet
                || copy.Items.Any(item => item is null || item.CreationId is null
                    || !Enum.IsDefined(item.MailboxIssue) || !Enum.IsDefined(item.KeywordIssue)
                    || !item.InvalidInitialProperties && item.SourceEmailId is null
                    || item.MailboxIssue == MailMessageMailboxIssue.None
                        && (item.MailboxId is null || item.MailboxId == Guid.Empty)
                    || item.KeywordIssue == MailMessageKeywordIssue.None && item.Keywords is not null
                        && (item.Keywords.Count > 128 || item.Keywords.Any(keyword =>
                            keyword is null || !JmapEmailCodec.IsValidKeyword(keyword)))
                    || item.ReceivedAt is { Kind: not DateTimeKind.Utc })
                || copy.Items.Select(item => item.CreationId).Distinct(StringComparer.Ordinal).Count()
                    != copy.Items.Count)
                throw NotRequest("The copy command contains invalid values.");
            return copy;
        }
        catch (JsonException)
        {
            throw NotRequest("The copy command contains invalid values.");
        }
    }

    private static void ValidateContactCriteria(MailContactQueryCriteria? criteria)
    {
        if (criteria?.Sort is null || criteria.Sort.Any(item => item is null
            || !Enum.IsDefined(item.Field) || !Enum.IsDefined(item.Collation)))
            throw NotRequest("The contact query sort is invalid.");
        ValidateContactFilter(criteria.Filter, 0);
    }

    private static void ValidateContactFilter(MailContactFilter? filter, int depth)
    {
        if (filter is null) return;
        if (depth > 64 || !Enum.IsDefined(filter.Operator)
            || filter.CreatedBefore is { Kind: not DateTimeKind.Utc }
            || filter.CreatedAfter is { Kind: not DateTimeKind.Utc }
            || filter.UpdatedBefore is { Kind: not DateTimeKind.Utc }
            || filter.UpdatedAfter is { Kind: not DateTimeKind.Utc })
            throw NotRequest("The contact query filter is invalid.");
        if (filter.Operator != MailContactFilterOperator.Condition)
        {
            if (filter.Conditions is null || filter.Terms is not null
                || filter.CreatedBefore is not null || filter.CreatedAfter is not null
                || filter.UpdatedBefore is not null || filter.UpdatedAfter is not null)
                throw NotRequest("The contact query operator is invalid.");
            foreach (var child in filter.Conditions)
                ValidateContactFilter(child, depth + 1);
            return;
        }
        if (filter.Conditions is not null || filter.Terms is null
            || filter.Terms.Any(item => item is null || !Enum.IsDefined(item.Field)
                || item.Value is null || item.Field == MailContactFilterField.InAddressBook
                    && !JmapId.IsValidId(item.Value))
            || filter.Terms.Select(item => item.Field).Distinct().Count() != filter.Terms.Count)
            throw NotRequest("The contact query condition is invalid.");
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
            .Concat([MailFeature.Basic, MailFeature.Messages, MailFeature.Submission,
                MailFeature.AutomaticReplies, MailFeature.Contacts]).ToHashSet();
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
