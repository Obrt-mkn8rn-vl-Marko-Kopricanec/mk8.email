# Deployment-agnostic tracked source

This repository is public, reusable source, not an operator's deployment record.
Do not commit actual tenant zones/domains, assigned IPs/subnets, Blob account or
endpoint assignments, public keys tied to a deployment, topology allocations,
secret material or rendered configuration. Keep private operator inputs under
the ignored `deploy/secrets/` or ignored internal planning. Do not execute an
installation, certificate, health or activation script merely to audit source.

## Required rendering inputs

`tools/deployment/Mk8DeploymentProfile.psm1` imports a strict version **2** JSON
object directly under ignored `deploy/secrets/`. Version 1 is refused; there are
no migrated site defaults. Missing, duplicate, unknown, mistyped and invalid
properties refuse. It renders an outside-repository, new output directory,
including `compose.yaml`, and leaves source/private profiles unchanged.

| Required properties | Purpose |
| --- | --- |
| `ServerIPv4`, `LanCidr`, `TrustedAdminIPv4`, `PublicIPv4` | Canonical private host/admin and LAN scope, plus independently allocated public address; no classifier fixture is an allocation. |
| `ContainerCidr` | Canonical wholly private container range, disjoint from LAN. |
| `PrimaryDomain`, `MailHostname`, `AdminHostname` | Canonical lower-case ASCII DNS names; distinct service hosts, mail host beneath the primary domain for these DNS templates. |
| `AutoconfigHostname`, `MtaStsHostname` | Explicit acknowledgement of the existing per-domain `autoconfig` convention and protocol-defined `mta-sts` label. |
| `DkimSelector`, `MtaStsPolicyId`, `CertificateAuthority` | Explicit selector, policy revision and CAA issuer; no shipped deployment key. |
| `DmarcReportLocalPart`, `TlsReportLocalPart`, `AdministratorLocalPart`, `PrimaryLocalPart` | Bounded template-safe identities at the supplied domain, not implicitly provisioned accounts. |
| `CompanyId`, `WanInterfaceList` | Explicit safe identifiers, not a fixed company or router interface assignment. |
| `DatabaseHost`, `ObjectStorageContainer` | Explicit database hostname/literal IP and private Blob container, not a presumed service alias or provisioned account. |
| `SshKeyPath`, `KnownHostsPath`, `BackupDestination` | Absolute paths outside the repository; private identity/trust/custody remains operator-owned. |

No shell/XML/JSON/DNS escaping is inferred: injected delimiters/control values
are rejected before copying assets. Required/unknown tokens refuse rendering.
The DNS templates are generic `primary-domain*.zone` files. Obtain DKIM TXT from
the independently owned key's generated per-domain record, never a template key.
Policy templates still require enabled-protocol, content, TLS, owner and exact
configuration review before serving them. Rendering is not installation approval.

The smoke-cleanup CLI now requires `CONFIG MARKER EXPECTED_ENVELOPE_SENDER`.
The shipped service signature is retained but refuses without the explicit
sender. `PurgeQuarantinedSmokeMessageFromSenderAsync` retains exact sender/marker/direction/state,
raw-header, Blob-reference and conditional-delete controls. A synthetic probe
identity does not grant delivery or relay authority.

## Retained literal purposes

The complete tracked-byte audit includes defaults, all variants/templates,
generated/designer files, public docs, tests, scripts and build/package inputs.
Regex matches are candidate scouts, not proof that a string is a deployment
assignment. Record purpose and call graph rather than allowing all test files.

- Loopback and wildcard values express local-only trust/listener semantics or
  owned in-process fixtures. Standard protocol ports are not assigned target
  sockets. Example local database/scanner/HTTP arrangements and product-named
  database roles/container/path defaults remain configurable; they are not
  approved infrastructure, tenant accounts or endpoint allocations.
- RFC private/special-use ranges in the profile classifier define admission,
  not an admin/public subnet. Finite renderer controls use synthetic private
  ranges and numeric public-classification values without opening sockets.
- Numeric public addresses in Web Push URL-admission tests are classification
  inputs only; actual sends use owned fake handlers. Private/documentation
  negatives are not accepted real endpoints. Do not weaken SSRF guards to make
  a documentation range count as public.
- Reserved `.test`, `.invalid`, `.example` and example.com/net/org identities
  are finite parser/authentication/authorization/MIME/discovery fixtures or
  explicitly generic documentation examples, not production defaults. The
  migrated tenant fixture is distinct from existing negative example domains.
- XML/SOAP/EWS/DAV/TRX namespace URIs, schema IDs and fixed protocol labels are
  protocol identities, not network destinations. Dotted code identifiers and
  filenames are not domains; malformed message-set syntax is not an IPv6 bind.
- Official dependency/update URLs identify the .NET/container/OS/antivirus
  producer. Bibliographic/license/project-origin links identify sources or
  copyright, not tenant routing. EICAR bytes remain a scanner-test identity.

These distinctions do not exempt future deployment literals. Repeat the complete
inventory and semantic inspection when changing configuration, examples,
generated output or tests. Do not move a real assignment to another tracked
default/template or silently adopt historical internal material. Source-policy
conformance does not waive failed validation, readiness, security, recovery,
package, certificate, owner or production gates.
