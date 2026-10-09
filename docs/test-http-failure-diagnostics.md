# Local HTTP fixture cancellation diagnostics

The PostgreSQL/Azure-backed `CaptureFixture` retains a test-only, 128-event phase
history for client sends/headers/cancellation, Gateway entry/exit, durable journal
attempts/completion and Worker dispatch start/completion/fault. Operations are
mapped to a closed diagnostic category; request identifiers are opaque references.
Evicted event counts are explicit. No request URI, headers, body, credentials,
stored MIME, SQL text, exception messages or connection string is recorded.

When the inner HTTP transport cancels while awaiting response headers, the test
handler keeps the original cancellation and makes one independent, cancellable
diagnostic attempt. Its two-second collection budget is separate from the original
15-second HttpClient deadline. The owned unpooled PostgreSQL connection and command
also have explicit timeout/cancellation limits. The collector is awaited, not
abandoned as an unobserved background task. Provider/OS shutdown responsiveness is
not a guaranteed two-second end-to-end latency SLA.

The snapshot contains queue-state/attempt aggregates, presentation inbound/outbound
journal counts, coarse current-database activity/wait counts and Worker task status.
It is emitted under `MK8_TEST_HTTP_CANCELLED_DIAGNOSTICS` in test standard output;
the existing CI suite evidence retains that output for failed cases. Secondary
nonfatal collection/sink failures do not replace the HTTP exception. Only their
type is reported, not a provider error message. Successful requests collect and
publish no failure snapshot, and no request is retried by this handler.

These are point observations, not a transactionally consistent timeline, a
cross-request cursor, physical durability proof or a guaranteed root-cause report.
Phase history may be truncated, and database unavailability can prevent a snapshot.
Cancellation during HttpClient's later outer content buffering or a caller's
separate response-body read is outside this response-header handler. Hard runner
loss can prevent all capture. Production Gateway/Worker code, protocol authority,
payload storage, durable receipt semantics, assertions and deadlines are unchanged.

The historical MoveFolder CI run 37856065733 remains FAILED on the existing
DeleteFolder root-refusal client's 15-second timeout. New diagnostic controls or
later successful runs do not retroactively explain that failure, establish
stability/SLA, exclude indirect/global regressions or authorize any deployment.

Framework contract: [HttpClient timeout](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.timeout?view=net-10.0)
and [delegating-handler cancellation](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.delegatinghandler.sendasync?view=net-10.0).
