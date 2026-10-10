# Inbound-mail readiness

Source acceptance is not inbound readiness, stable runtime, application-package
acceptance or operational permission. Target-specific timetables, hardware,
addresses, tenant names and peer evidence belong in ignored internal planning,
not this public codebase. Optional enterprise/EWS work does not replace mail
readiness and does not authorize public proxy exposure.

## Configuration and discovery

Require explicit canonical mail/admin hostnames, owned domains, listener binds,
ports, private admin networks and public HTTPS base URLs. MX targets must have
direct approved address records, not CNAMEs. Publish IPv6 only after proving the
actual listener and route; selecting an IPv6 bind is not dual-stack proof. PTR is
an address-provider action, separate from the forward zone. SPF must match the
actual outbound providers. DKIM keys/selectors, CAA, reporting mailboxes, DMARC,
MTA-STS and TLS-RPT policy are operator inputs, not values inherited from an old
zone export. Do not alter existing services before an explicitly agreed cutover.

Tracked deployment assets are unrendered examples with required profile tokens.
See [source policy](deployment-source-policy.md) before rendering. Generated
assets and private profiles are not tracked and are not automatically approved
for installation. Static discovery must advertise only actually enabled,
configured services, with exact rendered content and certificate identities.
The MTA-STS enforce template is not an approved served policy.

SMTP inbound, authenticated submission and native IMAP/POP/ManageSieve retain
their own listeners. An HTTP reverse proxy cannot implicitly carry those
transports. RequireAuth protects both configured submission modes before MAIL;
relay permission is independently authenticated. Public JMAP/OAuth/DAV and
streaming SSE need distinct route/deadline controls. Admin, Razor and health
remain private; the supplied public nginx profile does not expose POX/EWS.

Only the explicitly trusted loopback proxy hop has source support. Strip forged
forwarding headers, then prove client-IP/scheme propagation through the actual
proxy path. Separate-guest HTTPS alone does not establish client-IP authority.
Liveness is not readiness: `/health/live`, `/health/ready` and
`/health/application` have different process, SQL-transport and Worker meanings.
Require native TLS/authentication, Blob and durable enqueue/delivery canaries.

## Blob dependency and limits

The adapter pins Azure.Storage.Blobs 12.29.2 and Azure.Core 1.55.0, with API
2026-06-06 and default transfer/retry options, not automatic version negotiation.
Supply the account, endpoint, protected connection-string file, container and
prefix explicitly. A custom DNS host with an account path cannot be assumed to
work with the default SDK URI construction: check actual container/object URIs
before accepting it. Host-style or IP-style alternatives require independently
validated TLS/Host/SNI/account routing. No endpoint is allocated by this guide.

Required immutable uploads use If-None-Match:*, length/SHA256 metadata and quoted
ETag; 409/412 reconciliation requires exact HEAD metadata. Conditional full/range
reads and snapshot-inclusive deletion must refuse replacement/stale references.
Leases, tags, HNS, append/page blobs, CPK and Bearer are not required by this
adapter. Preserve known request lengths, escaped targets, authentication and
conditional fields through every proxy hop; do not infer that an object's total
length is one HTTP body. With the example 64 MiB transport cap, a receipt can be
268,500,992 bytes and the default uploader stages it in blocks. Archive capacity,
retention and whole-volume admission require a separate agreement. Never expire
referenced mail, journal, queues or receipts. An ingress limit is not a quota or
physical-durability guarantee. Loopback/Azurite or injected transport evidence is
not actual-provider/default-HTTPS interoperability.

## Required readiness and recovery gates

Start a usable storage Application and prove its Gateway/client canary, then the
version-compatible database, Wake/Worker/Gateway, deep private mail canaries and
only then explicitly authorized route/MX admission. Preserve exact source,
application artifact, configuration, owner, install, rollback, health and
production gates. Review certificate renewal/reissue before activation.

Independent secret escrow/reissue, coordinated identity-safe key lifecycle and
reconciliation, recovery/upgrade/rollback across two physical hosts, and measured
Gateway restoration remain required. VMs on one parent are not two-host proof.
Use coarse gate-first database coordination and quiesced DDL: old Workers cannot
roll alongside a new schema. Switching binaries does not undo acknowledged
writes; restore must match keys, configuration and forward-only/superseded queues.
Outbound journal refusal can withhold acknowledgement after commit; postcommit
Blob cleanup is not atomic physical erasure. Preserve failed, rejected,
incomplete and unknown histories. A new passing run neither diagnoses earlier
timeouts nor establishes stability, SLA or global/indirect regression exclusion.
