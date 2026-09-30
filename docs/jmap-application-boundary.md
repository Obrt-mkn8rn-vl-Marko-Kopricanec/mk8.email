# JMAP application boundary migration

Gateway parses and validates I-JSON and the outer JMAP request envelope. Its
batch, call, result and reference-selector models exist only in Gateway, not in
the shared durable Contracts assembly. It sends a protocol-neutral
authentication/feature/operation-count admission plan through `mail.plan.validate`, then
one resolved mail operation at a time through `mail.operation.execute.v28`. Worker
returns a typed operation result, domain failures, known entity mappings and an
account profile. Gateway alone sequences the batch, handles Core/echo, and renders
the response envelope and HTTP problems. Worker receives neither the original
API document nor invocations, correlation identifiers or result-reference selectors.

Gateway translates method names, response names and result-reference names into
stable protocol-neutral `MailOperationKind` identifiers. Worker dispatches and
selects handlers using these identifiers, never JMAP method-name strings.
Gateway compares result dependencies using the same identifiers.
Unsupported wire names become a non-executable sentinel; the result-only failure
identifier remains distinct so references to an error response still work. Missing,
wrong-case or undefined internal discriminators fail closed before any mutation.
Handler registration rejects duplicate or invalid identifiers, and primary/additional
result identifiers are checked before commit and on receipt replay. Opaque business
values named `name` or `sourceName` are untouched. This is a coordinated contract
change (`mk8.distributed.v35`), not completion of all JMAP value-shaping extraction.

`Mailbox/get`, `Thread/get`, `AddressBook/get`, `Identity/get`, `EmailSubmission/get`, `VacationResponse/get`,
`PushSubscription/get` and all seven simple `/changes` methods have fully typed commands/results across this
boundary. Gateway validates its wire arguments, resolves requested IDs and
property selection, and sends account/folder GUIDs to Worker. Worker authorizes
the account, loads folder state and counters, and returns domain snapshots; it
does not parse or render the `Mailbox/get` method body. Gateway constructs the
JMAP list, notFound array, mailbox IDs, rights and filtered properties. The
typed result is saved in the same encrypted per-operation receipt, so retries
render the committed snapshot even if the underlying folder changes later.
For malformed `Mailbox/get` property/ID selections, a typed authorization-only command
preserves account-error precedence without loading folders or initializing state.
For simple `/changes` methods, Gateway validates `accountId`, `sinceState` and `maxChanges`,
then renders the change arrays returned from Worker's authorized state reader. Worker
still applies account, primary-contact-account and lazy-default identity/address-book rules.
For `AddressBook/get`, Gateway validates ID/property selection and renders book
rights; Worker checks the primary contact account, initializes its default book,
and returns domain snapshots. For `Identity/get`, Gateway owns ID/property selection
and address-list rendering; Worker authorizes the account, initializes the default
identity, and returns typed identity snapshots. Gateway also renders vacation
dates/bodies and push-subscription fields while Worker loads Blob-backed bodies
and removes expired subscriptions. For `Thread/get`, Worker returns ordered email
IDs and stored thread keys; Gateway normalizes opaque thread IDs and groups emails
before rendering selected results. For `EmailSubmission/get`, Worker returns
submission and queue-recipient state snapshots; Gateway formats envelopes,
delivery status and dates. `Blob/copy` now sends opaque source IDs to Worker for
account authorization and Azure Blob-backed copying; Gateway renders per-ID
results. `VacationResponse/set` now sends typed field-presence updates after Gateway
checks JMAP patch paths, immutable fields, creation references and per-item wire errors.
Worker authorizes the account, checks state, applies each valid update against the
current Blob-backed response inside the receipt transaction and reports domain
validation outcomes; Gateway renders SetResponse maps and errors. Other JMAP
methods still have Worker-side JSON argument/result shaping and must be extracted.
`EmailSubmission/query` and `/queryChanges` now use typed recursive filters and
comparators. Gateway checks the wire syntax and renders paging, anchor errors and
query deltas; Worker authorizes the account before any deferred filter/sort error,
then filters and sorts submission rows and computes the current state/deltas.
`Mailbox/query` and `/queryChanges` likewise use typed recursive folder filters,
sort comparators and tree-policy flags. Gateway owns argument errors, canonical
anchor and parent-ID interpretation and response rendering; Worker retains
folder hierarchy, subscription and state evaluation.
`ContactCard/copy` now sends typed source/target account references and creation
keys. Gateway owns its wire validation and forbidden-item response; Worker checks
source/target account access, primary Contacts-account eligibility and state.
The current user-scoped Contacts model exposes no second supported account, so
successful cross-account copying remains unavailable as before.
`ContactCard/query` and `/queryChanges` now send typed recursive filters and
sorts. Gateway validates wire criteria, anchors and paging and renders both
responses. Worker authorizes the primary Contacts account, searches stored
JSContact data, and computes state and query deltas without parsing a method body.
`ContactCard/get` now has Gateway-owned ID/property selection, limits and response
rendering. Worker authorizes and reads the stored canonical JSContact projection,
returning typed card identifiers plus its data-model JSON; it does not parse
or render the JMAP get method body.
`Email/import` now has Gateway-owned argument and per-item field validation,
creation-key selection, response/error rendering and ID formatting. Worker
receives typed blob, mailbox, keyword and date values, authorizes account-scoped
blob access, stores the message in Azure Blob-backed content, and returns typed
import outcomes. Its committed receipt retains the created-ID map, preventing a
retry from importing a duplicate message.
`Email/copy` now has Gateway-owned argument and item parsing, JMAP IDs and
primary/secondary response rendering. Worker authorizes both accounts, reads
Azure Blob-backed source content, stores the target message, and optionally
deletes the source with an expunge record. The typed receipt preserves both
the copied-ID map and the source-deletion outcome on retry. When conditional
source deletion fails its state check, Gateway renders the committed copy and
a secondary `stateMismatch` result from the typed outcome.
`Email/query` and `/queryChanges` now use typed recursive message filters and
sort comparators. Gateway owns filter/sort/window validation, deferred error
precedence and response rendering; Worker authorizes the account, reads
Blob-backed message content, evaluates search criteria and computes ordered
pages or state deltas. `SearchSnippet/get` now validates its filter and extracts
search terms in Gateway, then Worker reads Blob-backed MIME and returns only
bounded plain-text subject/preview windows. Gateway applies HTML escaping,
highlighting and the protocol byte limit to those windows.
`Identity/set` now sends typed identity drafts and field patches. Gateway owns
the JMAP set shape, creation references, immutable-field assertions and response
maps. Worker authorizes addresses, updates persisted identities and state, and
returns typed outcomes without parsing a JMAP set body.
`AddressBook/set` now sends typed collection drafts, field patches and default-book
targets. Gateway owns PatchObject syntax, immutable-rights assertions and SetResponse
rendering. Worker enforces the primary Contacts-account rule, collection quotas,
resource-aware deletion and default-book transitions without parsing a JMAP method body.
`PushSubscription/set` now sends typed subscription drafts and field patches.
Gateway owns PatchObject syntax, immutable-property assertions and SetResponse
rendering. Worker applies URL safety, key validation, quotas, expiry caps and
verification state, returning typed outcomes; verification delivery is separately
queued as a post-commit presentation effect.
`ContactCard/set` now transports JSContact document values and decoded patch
operations, not a JMAP method body. Gateway owns the account/reference syntax,
PatchObject parsing and SetResponse normalization maps. Worker applies card
validation, account-wide UID locking, Blob media authorization, quotas and
DAV persistence to those domain values.
`Mailbox/set` now has a typed durable command and typed per-item outcomes. Gateway
validates the method envelope, object maps, identifiers and object-count limit,
and renders SetResponse fields, rights, state and failures from snapshots. Worker
retains the final-state hierarchy/role planner, including atomic swaps and mixed
create/update/destroy requests, and resolves in-call parent creation references.
Its per-item create and PatchObject values are still encoded value trees interpreted
inside the Worker; this is not yet the final API-agnostic value-shaping boundary.
`Email/get` and `Email/parse` now send typed account, ID and projection-option
commands. Gateway validates the property grammar, body-value flags and limits,
and renders ordered results and missing/invalid IDs. Worker authorizes the account,
reads stored MIME or account-scoped Azure Blob content and returns a durable
projection snapshot. MIME parsing and the inner Email value projection still use
the Worker-side JMAP codec; this is an intermediate boundary, not completed
presentation ownership.
`Email/set` now sends typed account, state and per-item mutation commands. Gateway
validates the method envelope, object map/reference syntax and count limit, then
renders SetResponse success/failure maps and created message fields from typed
outcomes. Worker retains account authorization, MIME construction, Azure Blob
storage, mailbox/keyword policy and transactional state changes. Draft and patch
values still arrive as encoded trees interpreted by Worker; the temporary
EmailSubmission/set implicit action also uses a Worker-side response adapter.
These are outstanding API-neutrality boundaries, not final presentation ownership.

Capability URNs are also Gateway-owned. Gateway maps them into stable `MailFeature`
identifiers; Worker enforces feature admission and method eligibility using only
those identifiers. Required feature fields and undefined enum values fail closed.
Unknown wire capabilities remain distinct from malformed internal identifiers,
and duplicate supported capabilities retain their set semantics. Worker returns
`MailApplicationFailure` kinds and typed resource limits, not JMAP error URNs,
problem titles, HTTP status codes or wire limit names. Gateway rejects inconsistent
failure discriminators before mapping them into protocol problems. Opaque business
values are not rewritten by either mapping.

For a parse failure, Gateway sends only an authentication/capacity check. When the
capability list and call count were structurally valid, this check also carries
those typed preflight values. Worker authenticates and applies its concurrency,
capability and call-count policy before Gateway reveals the parse error. No
business invocation runs in this path.

Gateway also translates top-level result-reference keys and JSON-pointer syntax
into ordered Gateway argument bindings and decoded value selectors. Gateway
resolves these dependencies against completed, normalized results before sending
a business command. The one remaining Worker handler does not register or execute Core/echo. Malformed
references carry a deferred failure at their original binding position, so they
remain invocation errors rather than rejecting the entire batch or changing
which error wins. First-result selection, array wildcard flattening, strict array
indexes, creation identifiers and references to normalized results are preserved.
Internal JSON envelopes use canonical camelCase names and case-sensitive decoding
on both sides, preserving case-distinct properties in opaque application values.
Gateway also recognizes creation-reference tokens in argument keys and values and
provides a typed alias map with each business command. Worker resolves only those
aliases against its current known-entity map, so a create followed by a reference
inside one operation still works without Worker parsing the JMAP `#` marker.
The alias map is required on the v28 command. Opaque non-ID values are not rewritten.

Worker returns typed accounts, feature limits and change maps, never session URLs
or event/push JSON. Gateway renders discovery documents, chooses its public URLs,
and derives session state from that final document. Batch and discovery responses
use the same renderer. Advertised upload/request size limits are the lower of
the backend policy and Gateway's body limit. Advertised concurrent-request
capacity is likewise the lower Gateway/backend limit. A Gateway lease covers the
entire batch, including time between business operations; Worker separately bounds
its active operation concurrency. Authentication still precedes capacity checks,
and capacity/feature/count rejection still precedes a later document parse error. Gateway frames both event-source
changes and typed push verification/change notifications before encryption/send.

This changes the durable operation contracts. The old `jmap.api.process`,
`jmap.batch.execute`, `jmap.batch.execute.v2`, `jmap.batch.execute.v3`, `jmap.batch.execute.v4`,
`jmap.batch.execute.v5`, `jmap.batch.execute.v6`, `jmap.session.get`, `jmap.event.poll`, `jmap.profile.get`,
`jmap.upload`, `jmap.download`, `jmap.changes.poll`, `webpush.send` and
`mail.operation.execute`, `mail.operation.execute.v2`, `mail.operation.execute.v3` and
`mail.operation.execute.v4`, `mail.operation.execute.v5`, `mail.operation.execute.v6` and
`mail.operation.execute.v7`, `mail.operation.execute.v8`,
`mail.operation.execute.v9`, `mail.operation.execute.v10`, `mail.operation.execute.v11`,
`mail.operation.execute.v12`, `mail.operation.execute.v13`, `mail.operation.execute.v14`,
`mail.operation.execute.v15`, `mail.operation.execute.v16`, `mail.operation.execute.v17`,
`mail.operation.execute.v18`, `mail.operation.execute.v19`, `mail.operation.execute.v20`,
`mail.operation.execute.v21`, `mail.operation.execute.v22`, `mail.operation.execute.v23`,
`mail.operation.execute.v24`, `mail.operation.execute.v25`,
`mail.operation.execute.v26`, `mail.operation.execute.v27` operations
are unsupported. The current operations are `mail.plan.validate`,
`mail.operation.execute.v28`, `jmap.profile.get.v2`, `jmap.upload.v2`,
`jmap.download.v2`, `jmap.changes.poll.v2` and `webpush.send.v2`. Old requests must
not be reinterpreted as new empty or incomplete data.
Before upgrading an existing distributed installation, stop admission, drain or
explicitly reconcile old pending/leased operations with their original Worker,
and upgrade both roles before resuming admission. An old Gateway/new Worker or
new Gateway/old Worker pairing is not supported. No live upgrade or deployment
is authorized by this source checkpoint.

The known superseded operation precondition is enforced: Worker startup and
activation probes reject pending/leased work in either lane, and Gateway remains
live but unready without consuming it. Late leases are preserved for original-role
drain/reconciliation. Gateway activation/candidate upgrades also require a Worker
ping with matching internal contract metadata; prior-release rollback uses the
prior Gateway's own health interpretation. These guards are not release attestation
and do not replace the same-approved-release requirement.

For PostgreSQL-backed mail operations, each authoritative durable request ID
identifies one business receipt at ordinal zero in the `mail.operation` scope.
Separate operations in a batch have separate queue identities. An account-bound advisory transaction lock
serializes retries; the receipt row commits in the same transaction as the method's
business writes. The result, creation-reference state, input fingerprint and any
verification intent are encrypted into Azure Blob storage, never inline in PostgreSQL.
A reclaimed request reuses committed results instead of reapplying those writes.
Uncommitted work is rolled back; an ambiguous commit retains its Blob for reconciliation.
This does not add exactly-once guarantees to every other application operation or
to external HTTP delivery.

Relational push-subscription verification is a durable outbox effect in that receipt,
not an in-memory post-commit callback. One-shot Worker draining publishes the frozen
effect ID to the presentation lane; losing an enqueue acknowledgement retries the
same ID. Failed dispatch retains the intent and schedules a Wake-visible retry.
Wake reads only the two scheduling columns, not receipt content or Blob references.
Backups inventory the encrypted receipt objects and restore rebinds their ETags.
Older verified v2/v3 snapshots gain an empty receipt table during isolated restore.

The native upgrade script now captures the prior receipt-schema/permission state
and probes the prior Wake executable before interruption. With Wake/path/drain stopped,
it prepares the candidate using the explicit Worker configuration, grants only the
two receipt scheduling columns, and probes the candidate Wake before switching the
release link. These are root-operator actions; the Wake identity receives neither
Worker credentials nor schema-management privileges.

Rollback to a pre-receipt Worker removes the newly introduced receipt table only
when it is empty and there are no pending/leased receipt-producing superseded
JMAP or mail operations, including expired
ones. It holds the snapshot/deletion barrier and transaction/table locks while deciding.
A committed receipt or incompatible request refuses legacy rollback, preserves state,
and leaves the Worker units stopped for explicit reconciliation or verified restore.
Receipt-aware rollback keeps the schema, grants and rows. This is a narrow schema/grant
bridge, not reversal of other database or Blob migrations or an admission barrier:
stop admission and coordinate both roles as required above. Privileged two-host
upgrade/rollback rehearsals and final review remain outstanding. Receipt retention
and shared encryption-key rotation also require explicit reconciliation policy;
do not delete receipts while their original requests can retry.

One presentation deadline bounds admission and every sequential operation. A
Gateway-local scope clips each durable queue envelope to that same deadline;
splitting a batch cannot multiply its execution window. An already expired
operation is journaled as a timeout without being queued. Scope nesting cannot
extend the deadline, and parallel requests have independent scopes.

Worker returns a lossless, protocol-neutral value tree. Well-formed text remains
ordinary JSON text to avoid inflating a maximum-size mail body past the transport
limit; malformed text and object-member names use explicit UTF-16 code units,
retaining invalid surrogate units and distinct keys through encrypted queue/receipt JSON. Gateway reconstructs and
normalizes that tree before displaying it or resolving later references. Receipt
results stay raw, not tied to one presentation policy. The value tree is bounded
to 64 levels; internal envelope serializers accommodate its structural overhead
with a 256-level limit. Invalid primary/additional results fail before business
commit, and corrupted replay data fails closed.

This is an intermediate boundary correction, not completion of the API-agnostic
architecture. Per-method value shaping and any remaining protocol-shaped domain
arguments still require the full API-awareness audit. JMAP and the two remaining test projects have
unfinished analyzer gates. Full independent review and deployment remain blocked
until those obligations are closed.
