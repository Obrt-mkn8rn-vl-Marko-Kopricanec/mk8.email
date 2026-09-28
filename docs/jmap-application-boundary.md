# JMAP application boundary migration

Gateway parses and validates I-JSON and the outer JMAP request envelope. It sends
an authentication/feature/count admission plan through `mail.plan.validate`, then
one resolved mail operation at a time through `mail.operation.execute`. Worker
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
change (`mk8.distributed.v8`), not completion of all JMAP value-shaping extraction.

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
a business command. The 38 Worker handlers do not register or execute Core/echo. Malformed
references carry a deferred failure at their original binding position, so they
remain invocation errors rather than rejecting the entire batch or changing
which error wins. First-result selection, array wildcard flattening, strict array
indexes, creation identifiers and references to normalized results are preserved.
Internal JSON envelopes use canonical camelCase names and case-sensitive decoding
on both sides, preserving case-distinct properties in opaque application values.

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
`jmap.upload`, `jmap.download`, `jmap.changes.poll` and `webpush.send` operations
are unsupported. The current operations are `mail.plan.validate`,
`mail.operation.execute`, `jmap.profile.get.v2`, `jmap.upload.v2`,
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
when it is empty and there are no pending/leased v4, v5 or v6 JMAP requests or current mail operations, including expired
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
architecture. Creation-reference resolution and per-method value shaping still
require the full API-awareness audit. Obsolete shared frontend DTOs also need to
leave the Contracts assembly. JMAP and the two remaining test projects have
unfinished analyzer gates. Full independent review and deployment remain blocked
until those obligations are closed.
