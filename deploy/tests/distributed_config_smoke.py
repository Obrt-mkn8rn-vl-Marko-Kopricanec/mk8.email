#!/usr/bin/env python3
"""Check that the distributed runtime template stays complete and secret-free."""

import json
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
native = json.loads((REPOSITORY / "deploy/native/mk8email.config.json").read_text())
distributed = json.loads(
    (REPOSITORY / "deploy/native/mk8email.distributed.config.example.json").read_text()
)

assert set(distributed) == set(native) | {"Messaging", "ObjectStorage"}
for section, value in native.items():
    assert distributed[section] == value, section

messaging = distributed["Messaging"]
storage = distributed["ObjectStorage"]
assert messaging["Enabled"] is True
assert messaging["EncryptionKeyId"] == "primary"
assert messaging["EncryptionKeyFile"] == "/etc/mk8email/secrets/messaging_encryption_key"
assert "EncryptionKey" not in messaging
assert messaging["MaxPayloadBytes"] >= (4 * native["Limits"]["MaxMessageSizeBytes"] + 2) // 3 + 1_048_576
assert storage["Provider"] == "azure-blob"
assert storage["ConnectionStringFile"] == "/etc/mk8email/secrets/object_storage_connection"
assert "ConnectionString" not in storage
assert storage["CreateContainerIfMissing"] is False

print("Distributed configuration template is complete and secret-free.")
