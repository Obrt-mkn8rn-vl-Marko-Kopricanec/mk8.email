# JMAP application boundary migration

Gateway parses and validates I-JSON and the outer JMAP request envelope. It sends
typed capabilities, invocations, correlation identifiers and creation identifiers
through `jmap.batch.execute`; Worker returns typed invocation results and a
revision. Gateway renders the JMAP response envelope and HTTP problem status.
Worker never receives the original API document for this operation.

For a parse failure, Gateway sends only an authentication/capacity check. When the
capability list and call count were structurally valid, this check also carries
those typed preflight values. Worker authenticates and applies its concurrency,
capability and call-count policy before Gateway reveals the parse error. No
business invocation runs in this path.

This changes the durable operation contract. The old `jmap.api.process` operation
is rejected as unsupported; it must not be reinterpreted as a new empty batch.
Before upgrading an existing distributed installation, stop admission, drain or
explicitly reconcile old pending/leased operations with their original Worker,
and upgrade both roles before resuming admission. An old Gateway/new Worker or
new Gateway/old Worker pairing is not supported. No live upgrade or deployment
is authorized by this source checkpoint.

This is an intermediate boundary correction, not completion of the API-agnostic
architecture. Session/public-URL construction, event framing, result-reference
syntax and response character normalization still require further extraction
or a documented application-level representation. JMAP and the two remaining
test projects also have unfinished analyzer gates. Full independent review and
deployment remain blocked until those obligations are closed.
