#!/usr/bin/env python3
"""Exercise secret-free split-host recovery inventory creation."""

import copy
import hashlib
import json
import stat
import subprocess
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "deploy/scripts/mk8-distributed-recovery-inventory"
TEMPLATE = ROOT / "deploy/native/mk8email.distributed.config.example.json"
ESCROW_EXAMPLE = ROOT / "deploy/native/mk8email.recovery-escrow.example.json"
RELEASE = "a" * 40


def write_private(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    path.chmod(0o600)


def inputs():
    example = json.loads(TEMPLATE.read_text(encoding="utf-8"))
    gateway = copy.deepcopy(example)
    worker = copy.deepcopy(example)
    gateway["Database"]["Username"] = "mk8gateway"
    gateway["Database"]["PasswordFile"] = "/etc/mk8email/secrets/gateway_database_password"
    gateway["OAuth"]["SigningKeyFile"] = None
    gateway["Mfa"]["EncryptionKeyFile"] = None
    gateway["Dkim"]["PrivateKeyPath"] = None
    worker["Database"]["Username"] = "mk8worker"
    worker["Database"]["PasswordFile"] = "/etc/mk8email/secrets/worker_database_password"
    worker["Tls"]["CertificateKeyPath"] = None
    worker["Admin"]["DataProtectionKeyPath"] = None
    escrow = {"schemaVersion": 1, "refs": {
        "shared.messaging.active": "offline://mk8email/messaging/active",
        "worker.oauth-signing": "offline://mk8email/worker/oidc",
        "worker.mfa-encryption": "offline://mk8email/worker/mfa",
        "backup.signing": "offline://mk8email/backup/signing",
        "backup.decryption": "offline://mk8email/backup/decryption",
    }}
    return gateway, worker, escrow


example_escrow = json.loads(ESCROW_EXAMPLE.read_text(encoding="utf-8"))
assert example_escrow["schemaVersion"] == 1
assert set(example_escrow["refs"]) == set(inputs()[2]["refs"])
assert all(reference.startswith("example://") for reference in example_escrow["refs"].values())
assert "mk8-distributed-recovery-inventory" in (
    ROOT / "deploy/scripts/install-mail-stack"
).read_text(encoding="utf-8")
assert "usr/local/sbin/mk8-distributed-recovery-inventory" in (
    ROOT / "deploy/scripts/mk8-backup"
).read_text(encoding="utf-8")
assert "recovery-escrow.example.json" in (
    ROOT / "deploy/tests/backup_restore_smoke"
).read_text(encoding="utf-8")
assert "distributed_recovery_inventory_smoke.py" in (
    ROOT / "deploy/scripts/mk8-build"
).read_text(encoding="utf-8")


def invoke(directory, gateway, worker, escrow, gateway_host="gateway.example",
           gateway_raw=None, gateway_mode=0o600):
    gateway_path = directory / "gateway.json"
    worker_path = directory / "worker.json"
    escrow_path = directory / "escrow.json"
    output = directory / "recovery-inventory.json"
    if gateway_raw is None:
        write_private(gateway_path, gateway)
    else:
        gateway_path.write_text(gateway_raw, encoding="utf-8")
        gateway_path.chmod(0o600)
    gateway_path.chmod(gateway_mode)
    write_private(worker_path, worker)
    write_private(escrow_path, escrow)
    result = subprocess.run(
        [
            sys.executable, str(SCRIPT),
            "--template", str(TEMPLATE),
            "--gateway-config", str(gateway_path),
            "--worker-config", str(worker_path),
            "--escrow-map", str(escrow_path),
            "--gateway-host", gateway_host,
            "--worker-host", "worker.example",
            "--release-commit", RELEASE,
            "--wake-connection-handle", "/etc/mk8email/mk8email.wake.connection",
            "--backup-connection-handle", "/etc/mk8email/secrets/backup_blob_connection",
            "--backup-recipients-handle", "/etc/mk8email/backup/recipients.txt",
            "--backup-verify-handle", "/etc/mk8email/backup/verify.pub",
            "--backup-signing-handle", "/etc/mk8email/secrets/backup_signing_key",
            "--output", str(output),
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    return result, output


with tempfile.TemporaryDirectory(prefix="mk8-recovery-inventory-") as temporary:
    directory = Path(temporary)
    gateway, worker, escrow = inputs()
    result, output = invoke(directory, gateway, worker, escrow)
    assert result.returncode == 0, result.stderr
    document = json.loads(output.read_text(encoding="utf-8"))
    assert stat.S_IMODE(output.stat().st_mode) == 0o600
    assert document["schemaVersion"] == 1
    assert document["releaseCommit"] == RELEASE
    assert len(document["templateSha256"]) == 64
    assert document["roles"]["gateway"]["hostId"] == "gateway.example"
    assert document["roles"]["worker"]["hostId"] == "worker.example"
    assert document["roles"]["gateway"]["sha256"] == hashlib.sha256(
        (directory / "gateway.json").read_bytes()
    ).hexdigest()
    assert document["roles"]["gateway"]["config"]["OAuth"]["SigningKeyFile"] is None
    assert document["roles"]["worker"]["config"]["Tls"]["CertificateKeyPath"] is None
    assert document["roles"]["worker"]["config"]["Admin"]["DataProtectionKeyPath"] is None
    assert document["namespace"]["blobContainer"] == "mk8-email-objects"
    handles = {entry["id"]: entry for entry in document["secretHandles"]}
    assert handles["gateway.database"]["recovery"] == "reissue"
    assert handles["shared.messaging.active"]["recovery"] == "reconcile-then-rotate"
    assert handles["worker.oauth-signing"]["escrowRef"] == "offline://mk8email/worker/oidc"
    assert handles["backup.decryption"]["path"] is None
    original = output.read_bytes()
    second, _ = invoke(directory, gateway, worker, escrow)
    assert second.returncode != 0 and output.read_bytes() == original

for scenario in (
    "inline-gateway-key", "inline-worker-key", "gateway-worker-handle", "worker-tls-handle",
    "worker-admin-handle", "same-host", "missing-escrow", "invalid-escrow", "shared-escrow",
    "namespace-mismatch", "previous-mismatch", "world-readable-config",
    "prior-key-ring",
):
    with tempfile.TemporaryDirectory(prefix="mk8-recovery-inventory-") as temporary:
        directory = Path(temporary)
        gateway, worker, escrow = inputs()
        host = "gateway.example"
        gateway_mode = 0o600
        if scenario == "inline-gateway-key":
            gateway["OAuth"]["SigningKey"] = "must-not-be-copied"
        elif scenario == "inline-worker-key":
            worker["Messaging"]["EncryptionKey"] = "must-not-be-copied"
        elif scenario == "gateway-worker-handle":
            gateway["OAuth"]["SigningKeyFile"] = "/etc/mk8email/secrets/oidc_signing_key.pem"
        elif scenario == "worker-tls-handle":
            worker["Tls"]["CertificateKeyPath"] = "/etc/mk8email/tls/privkey.pem"
        elif scenario == "worker-admin-handle":
            worker["Admin"]["DataProtectionKeyPath"] = "/var/lib/mk8email-admin/data-protection"
        elif scenario == "same-host":
            host = "WORKER.EXAMPLE"
        elif scenario == "missing-escrow":
            del escrow["refs"]["worker.mfa-encryption"]
        elif scenario == "invalid-escrow":
            escrow["refs"]["worker.mfa-encryption"] = "example://not-provisioned"
        elif scenario == "shared-escrow":
            escrow["refs"]["worker.mfa-encryption"] = escrow["refs"]["worker.oauth-signing"]
        elif scenario == "namespace-mismatch":
            worker["ObjectStorage"]["ContainerName"] = "different-container"
        elif scenario == "previous-mismatch":
            worker["Messaging"]["DecryptionKeys"] = [
                {"Id": "old", "KeyFile": "/etc/mk8email/secrets/old_messaging_key"}
            ]
        elif scenario == "world-readable-config":
            gateway_mode = 0o644
        elif scenario == "prior-key-ring":
            previous = {"Id": "old", "KeyFile": "/etc/mk8email/secrets/old_messaging_key"}
            gateway["Messaging"]["DecryptionKeys"] = [previous]
            worker["Messaging"]["DecryptionKeys"] = [previous]
            escrow["refs"]["shared.messaging.previous.old"] = "offline://mk8email/messaging/old"
        result, output = invoke(directory, gateway, worker, escrow, host,
                                gateway_mode=gateway_mode)
        if scenario == "prior-key-ring":
            assert result.returncode == 0, result.stderr
            prior = {entry["id"]: entry for entry in json.loads(output.read_text())["secretHandles"]}
            assert prior["shared.messaging.previous.old"]["recovery"] == "retain-until-reconciled"
        else:
            assert result.returncode != 0, scenario
            assert not output.exists(), scenario

with tempfile.TemporaryDirectory(prefix="mk8-recovery-inventory-") as temporary:
    directory = Path(temporary)
    gateway, worker, escrow = inputs()
    raw = json.dumps(gateway).replace('"Database": {', '"Database": {}, "database": {', 1)
    result, output = invoke(directory, gateway, worker, escrow, gateway_raw=raw)
    assert result.returncode != 0
    assert not output.exists()

print("Split-host recovery inventory rejects secrets and unsafe role boundaries.")
