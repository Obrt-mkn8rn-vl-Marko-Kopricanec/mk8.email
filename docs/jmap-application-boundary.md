# JMAP application boundary migration

Gateway parses and validates I-JSON and the outer JMAP request envelope. It sends
typed capabilities, invocations, correlation identifiers and creation identifiers
through `jmap.batch.execute.v3`; Worker returns typed invocation results and an
account profile. Gateway renders the JMAP response envelope and HTTP problem status.
Worker never receives the original API document for this operation.

For a parse failure, Gateway sends only an authentication/capacity check. When the
capability list and call count were structurally valid, this check also carries
those typed preflight values. Worker authenticates and applies its concurrency,
capability and call-count policy before Gateway reveals the parse error. No
business invocation runs in this path.

Gateway also translates top-level result-reference keys and JSON-pointer syntax
into ordered application argument bindings and decoded value selectors. Worker
only resolves these typed dependencies against completed results. Malformed
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
the backend policy and Gateway's body limit. Gateway frames both event-source
changes and typed push verification/change notifications before encryption/send.

This changes the durable operation contracts. The old `jmap.api.process`,
`jmap.batch.execute`, `jmap.batch.execute.v2`, `jmap.session.get`, `jmap.event.poll`
and `webpush.send` operations are unsupported. The new operations are `jmap.batch.execute.v3`,
`jmap.profile.get`, `jmap.changes.poll` and `webpush.send.v2`. Old requests must
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
and do not replace the same-approved-release requirement or business replay receipts.

This is an intermediate boundary correction, not completion of the API-agnostic
architecture. Method-response character normalization still requires further
extraction or a documented application-level representation. Creation-reference
resolution and per-method value shaping also require the full API-awareness audit;
typed outer batches alone do not establish the complete boundary. JMAP and the two remaining
test projects also have unfinished analyzer gates. Full independent review and
deployment remain blocked until those obligations are closed.
