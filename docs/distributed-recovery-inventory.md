# Split-host recovery inventory

The ordinary encrypted off-site archive contains mail data, not infrastructure
private keys or runtime credentials. Keep the recovery inventory produced here in
an independently administered operator store, outside that data archive and off
the Gateway host. The inventory contains versioned, non-secret copies of both role
configurations, their exact input SHA-256 digests, file-handle paths, and opaque
escrow references. It never opens a secret-handle file.

Prepare complete, strict-JSON role configurations from the template in the
exact approved release, not from a potentially compromised Gateway. The tracked
source template is `deploy/native/mk8email.distributed.config.example.json`.
Use distinct Gateway and
Worker hosts and PostgreSQL roles. Configure credentials only with `*File`
handles, never inline values. In the Gateway copy, set
`OAuth.SigningKeyFile`, `Mfa.EncryptionKeyFile`, and `Dkim.PrivateKeyPath` to
`null`. In the Worker copy, set `Tls.CertificateKeyPath` and
`Admin.DataProtectionKeyPath` to `null`. Place the
actual Worker-only OAuth, MFA, DKIM and archive private keys only on the Worker
or in independent escrow; do not provision them on Gateway. Keep backup signing
and decryption authority inaccessible to Gateway.
Keep the operator-side role configuration copies and escrow-reference map at
mode `0600` in a private directory; the generator deliberately refuses more
permissive input files.

Copy `deploy/native/mk8email.recovery-escrow.example.json` into a private
operator directory and replace each deliberately invalid `example://` value
with a real `offline://` or `vault://` *reference*, never the secret itself.
Add `shared.messaging.previous.<key-id>` entries for every retained decryption
key and `worker.dkim-private` when DKIM signing is enabled. The generator checks
the syntax and completeness of those references, not that escrow is reachable.

Run the installed `mk8-distributed-recovery-inventory` on a trusted operator
host with the two role configuration copies and private escrow-reference map:

```text
mk8-distributed-recovery-inventory \
  --template /usr/local/share/mk8email/distributed/config.example.json \
  --gateway-config /secure/operator/gateway.config.json \
  --worker-config /secure/operator/worker.config.json \
  --escrow-map /secure/operator/escrow-refs.json \
  --gateway-host gateway.example.net --worker-host worker.example.net \
  --release-commit EXACT_APPROVED_40_CHARACTER_GIT_SHA \
  --wake-connection-handle /etc/mk8email/mk8email.wake.connection \
  --backup-connection-handle /etc/mk8email/secrets/backup_blob_connection \
  --backup-recipients-handle /etc/mk8email/backup/recipients.txt \
  --backup-verify-handle /etc/mk8email/backup/verify.pub \
  --backup-signing-handle /etc/mk8email/secrets/backup_signing_key \
  --output /secure/operator/recovery-inventory.v1.json
```

The output directory must be owned by the caller and mode `0700`. The tool
creates the manifest at mode `0600` without overwriting an existing file. It
rejects inline credential fields, case-colliding JSON keys, unexpected or
missing configuration fields, wrong-role private-key handles, a co-located
host label, mismatched shared namespace/key-ring metadata, and missing escrow
references. A passing manifest is an inventory, not live proof of backend
privilege separation or of the named escrow's contents. Keep it versioned with
the exact approved release and verify its digest before a recovery drill.

For a compromised Gateway, discard the host and revoke its PostgreSQL and
Azure Blob-compatible credentials; reissue its TLS private key/certificates and
Gateway-only credentials. Retrieve Worker-only and archive keys independently.
The shared messaging key is exposed to Gateway by the current transport, so
retain old keys until encrypted queue, receipt, and object records are
reconciled, then rotate in a coordinated role upgrade. Do not delete old keys
merely because a replacement Gateway starts. Validate both role configurations,
restricted database privileges, Wake, durable request/reply flow, mail protocol
listeners, and a deep end-to-end health probe during an isolated two-host
restore.

The current native units use the same OS identity if both roles are installed
on one host; this inventory therefore requires distinct host labels for the
safer disposable-Gateway posture. It does not attest that the hosts actually
differ, nor does it inventory dynamic domain TLS/DKIM keys. Real mk8.sava
Blob compatibility, shared-key rotation/reconciliation, independently
retrievable escrow, a timed two-host restoration, and a 24-hour replacement
claim remain unproven. Do not activate or deploy based on this inventory alone.
The supplied release commit is recorded, not cryptographically attested by the
generator; verify the packaged source and template against that commit separately.
