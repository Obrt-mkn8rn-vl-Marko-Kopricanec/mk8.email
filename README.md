# mk8.email

A self-hosted .NET 10 email server with JMAP as its default client protocol and secure IMAP, POP3, and SMTP compatibility for traditional clients such as Thunderbird.

JMAP discovery is available at `https://email.mk8n.com/.well-known/jmap` and through each hosted domain's `_jmap._tcp` SRV record. The implementation supports JMAP Core, Mail, Submission, VacationResponse, uploads/downloads, push subscriptions, and event-source state notifications.

Thunderbird autoconfiguration advertises IMAP as the primary incoming protocol, POP3 as an alternate retrieval protocol, and authenticated SMTP submission. Password authentication is exposed only after TLS, and both implicit TLS and standards-based upgrade listeners are available for POP3 and SMTP.
