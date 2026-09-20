# mk8.email

A self-hosted .NET 10 email server with JMAP as its default client protocol and IMAP/SMTP compatibility for traditional clients.

JMAP discovery is available at `https://email.mk8n.com/.well-known/jmap` and through each hosted domain's `_jmap._tcp` SRV record. The implementation supports JMAP Core, Mail, Submission, VacationResponse, uploads/downloads, push subscriptions, and event-source state notifications.
