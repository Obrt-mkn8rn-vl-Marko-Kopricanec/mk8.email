# mk8.email architecture

mk8.email is split into a continuously available presentation plane and an independently deployable application plane.

## Runtime boundary

- **Gateway** owns every network listener and presentation concern: TLS, HTTP, JMAP, DAV, OAuth endpoints, SMTP, IMAP, POP3, ManageSieve, protocol parsing, response rendering, connection lifetime, and durable inbound/outbound traffic recording.
- **Application Worker** owns email and groupware use cases and domain policy. It consumes durable requests, publishes durable responses, and remains dormant on PostgreSQL `LISTEN` while no work is available. It must not reference ASP.NET Core or expose a network API.
- **Contracts** contains transport-neutral request, response, journal, and storage contracts. It has no ASP.NET Core, Entity Framework Core, Npgsql, or Azure SDK dependency.
- **Messaging** implements the encrypted PostgreSQL control plane. Requests survive an absent worker, are claimed with leases and `FOR UPDATE SKIP LOCKED`, and use PostgreSQL notifications only as wake-up hints; durable state remains authoritative.
- **Storage** implements the large-object data plane through the Azure Blob Storage protocol. The adapter accepts an injected `BlobServiceClient`, so production can target Azure or mk8.sava's Azure Blob-compatible endpoint without leaking an Azure SDK dependency into Application logic.

Gateway and Application Worker may use different hosts. They share only the PostgreSQL messaging database, the configured Azure Blob-compatible object store, compatible encryption keys, and the contracts assembly.

## Payload persistence

PostgreSQL stores queue state, immutable object references, lengths, ciphertext SHA-256 digests, encryption metadata, and small control envelopes. Payloads over the configured inline threshold are encrypted with AES-256-GCM before upload and are stored only through the Azure Blob protocol. The database constraint permits either an inline ciphertext or an `azure-blob` reference, never both.

Object writes are conditional and immutable. Reads are constrained to the recorded ETag and verified against the recorded length and ciphertext digest before authenticated decryption. A missing, replaced, truncated, or tampered object fails closed.

Message metadata is authenticated as AES-GCM associated data but remains visible in PostgreSQL. Callers must keep credentials, message bodies, attachments, and other secrets in the encrypted payload, not in metadata.

JMAP upload blobs now follow this boundary as reference-only rows. Application Worker externalizes legacy inline uploads before it begins consuming requests, serializes that migration across hosts, and then enables a PostgreSQL constraint that forbids inline JMAP blob bytes. Account quota updates use PostgreSQL advisory transaction locks, while object deletion follows the enclosing JMAP method's commit or rollback outcome.

Raw mail queue messages, including their MIME attachments, also use reference-only Azure Blob-compatible objects. A serialized startup migration preserves legacy rows before enabling a database constraint that forbids inline queue content. SMTP and JMAP submission upload the immutable object before committing queue metadata, queue processing verifies its length and SHA-256 digest, and retention cleanup deletes the object only after the corresponding row deletion commits. PostgreSQL notifications wake the queue worker for new or rescheduled work; the bounded fallback exists only to recover missed notifications and expired leases.

CalDAV and CardDAV resource bodies now use reference-only Azure Blob-compatible objects as well. The shared storage service verifies every read against the PostgreSQL length and SHA-256 digest, and DAV and JMAP Contacts writes share commit-aware replacement and deletion cleanup. A serialized startup migration externalizes legacy resource bytes before enabling a database constraint that forbids inline DAV bodies; it runs for DAV or JMAP Contacts deployments because both protocols share the same resources.

Stored mailbox messages and attachments must follow the same storage boundary. Their migration from legacy database byte arrays remains a later rollout checkpoint and must complete before the distributed architecture is deployable.

## Availability semantics

PostgreSQL `LISTEN`/`NOTIFY` wakes a resident worker without polling. A bounded fallback scan recovers missed notifications and expired leases. This provides logical sleep while idle; physical scale-to-zero requires an external supervisor capable of starting a worker when durable work appears.

Neither PostgreSQL notifications nor an in-process connection is required for correctness. A Gateway can enqueue while every Application Worker is offline, and a later worker on another machine can claim and complete the request.

`mk8.email.Application.Worker` is the ASP.NET-free executable for this role. It validates only worker-owned configuration, initializes the messaging schema, waits on the durable request channel, creates one dependency-injection scope per claimed operation, renews leases while work is active, and publishes a durable response or sanitized failure. Its typed operation set covers administration, authentication, MFA, OAuth authorization, token lifecycle, OpenID Connect identity, public signing-key projection, and JMAP email/groupware use cases without sharing Application assemblies or a database context with Gateway.

Gateway owns the administration UI and the complete OAuth/OIDC HTTP surface. OAuth presentation traffic is durably captured on ingress and egress even for static discovery responses and application-unavailable responses; application-bound operations also receive their own correlated encrypted request/reply records. OAuth signing and MFA encryption secrets are loaded only by Application Worker, while Gateway receives only public key material through the asynchronous control plane.

Gateway also owns the complete JMAP HTTP surface: session discovery, API requests, upload, ranged download, authentication-header parsing, response rendering, and event-source connection lifetime. The JMAP application library has no ASP.NET dependency and runs only behind typed durable operations in Application Worker. JMAP presentation requests and ordinary responses are recorded as encrypted HTTP envelopes. Event streams record the response start and every emitted SSE chunk before it is sent, while each Gateway/Application exchange is independently journaled at the internal boundary. The legacy combined CLI no longer serves JMAP.

The legacy combined CLI remains only while DAV, SMTP, IMAP, POP3, and ManageSieve callers are migrated checkpoint by checkpoint. It no longer hosts OAuth or JMAP. Its remaining presence keeps this branch non-deployable until the final boundary tests prove that Gateway is the only presentation host and Application Worker is the only application-logic host.
