# mk8.email

A self-hosted .NET 10 email and groupware server with JMAP as its default client protocol and secure IMAP, POP3, SMTP, ManageSieve, CalDAV, and CardDAV compatibility for traditional clients such as Thunderbird.

JMAP discovery is available at `https://email.mk8n.com/.well-known/jmap` and through each hosted domain's `_jmap._tcp` SRV record. The implementation supports JMAP Core, Mail, Submission, VacationResponse, uploads/downloads, push subscriptions, and event-source state notifications.

Thunderbird autoconfiguration advertises JMAP first for clients that support it, IMAP as the current Thunderbird mail fallback, POP3 as an alternate retrieval protocol, authenticated SMTP submission, and CalDAV/CardDAV groupware endpoints. Each hosted domain publishes RFC 6186 and RFC 6764 SRV discovery, while `/.well-known/caldav` and `/.well-known/carddav` redirect clients to the authenticated DAV service. Password authentication is exposed only after TLS, and both implicit TLS and standards-based upgrade listeners are available for POP3 and SMTP.

Server-side filtering uses durable per-user Sieve scripts during local delivery. ManageSieve is available through STARTTLS on port 4190 and each hosted domain's `_sieve._tcp` SRV record, with script validation, quotas, activation, retrieval, rename, and deletion support.

CalDAV includes scheduling inbox/outbox discovery, authenticated iTIP invitations and responses, local calendar delivery, external iMIP delivery through the mail queue, and recurrence-aware free/busy replies. CardDAV preserves vCard 3.0/4.0 contacts and contact groups.
