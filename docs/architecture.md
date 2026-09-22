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

All mail attachments and other large persisted application objects follow this same storage boundary. Their migration from legacy database byte arrays is a separate rollout checkpoint and must complete before the distributed architecture is deployable.

## Availability semantics

PostgreSQL `LISTEN`/`NOTIFY` wakes a resident worker without polling. A bounded fallback scan recovers missed notifications and expired leases. This provides logical sleep while idle; physical scale-to-zero requires an external supervisor capable of starting a worker when durable work appears.

Neither PostgreSQL notifications nor an in-process connection is required for correctness. A Gateway can enqueue while every Application Worker is offline, and a later worker on another machine can claim and complete the request.

`mk8.email.Application.Worker` is the ASP.NET-free executable for this role. It validates only worker-owned configuration, initializes the messaging schema, waits on the durable request channel, creates one dependency-injection scope per claimed operation, renews leases while work is active, and publishes a durable response or sanitized failure. Its typed operation set covers administration, authentication, MFA, OAuth authorization, token lifecycle, OpenID Connect identity, and public signing-key projection without sharing Application assemblies or a database context with Gateway.

Gateway owns the administration UI and the complete OAuth/OIDC HTTP surface. OAuth presentation traffic is durably captured on ingress and egress even for static discovery responses and application-unavailable responses; application-bound operations also receive their own correlated encrypted request/reply records. OAuth signing and MFA encryption secrets are loaded only by Application Worker, while Gateway receives only public key material through the asynchronous control plane.

The legacy combined CLI remains only while JMAP, DAV, SMTP, IMAP, POP3, and ManageSieve callers are migrated checkpoint by checkpoint. It no longer hosts OAuth. Its remaining presence keeps this branch non-deployable until the final boundary tests prove that Gateway is the only presentation host and Application Worker is the only application-logic host.
