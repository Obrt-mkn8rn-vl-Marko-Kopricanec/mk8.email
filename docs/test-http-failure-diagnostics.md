# Local HTTP fixture cancellation diagnostics

The PostgreSQL/Azure-backed `CaptureFixture` retains a test-only, 128-event phase
history for client sends/headers/cancellation, Gateway entry/exit, durable journal
attempts/completion and Worker dispatch start/completion/fault. Operations are
mapped to a closed diagnostic category; request identifiers are opaque references.
Evicted event counts are explicit. No request URI, headers, body, credentials,
stored MIME, SQL text, exception messages or connection string is recorded.

Worker-domain async EF calls and Blob put/read/delete calls additionally record
start/returned/fault/cancellation with closed activity labels and opaque span IDs.
Async-local dispatch correlation separates concurrent/nested requests and restores
the caller's context. Setup/background work without that context is not attributed.
The domain-only Blob decorator forwards the original arguments, streams, result,
exception and cancellation token; transport/journal Blob storage is not decorated.
The EF interceptor does not inspect or modify SQL, parameters, rows or errors, and
forwards any existing interception result. Reader-return does not mean rows have
finished materializing. Sync EF, direct Npgsql and the raw transaction-start gate
are outside this hook. These observations distinguish boundaries, not root cause.

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

Run 37863320748 also remains FAILED: initial folder-key reading timed out before
that test began its later competing writer. The accepted point snapshot showed
Mail dispatch in progress and an idle transaction, but no SQL/Blob subphase; the
new I/O observations do not retroactively identify that failure's cause.

Run 37869384595 remains FAILED on the existing alias-refusal client's 15-second
timeout. Its last observed domain BlobPut had started without a matching return
at the collection point. That identifies neither the provider operation nor why
it did not return. Later in-memory phases and the database snapshot are sequential
observations, not an atomic shared timeline.

The domain fixture now adds a test-only Azure SDK `BeforeTransport` policy with
closed container-create/upload/block/block-list/properties/download/delete/other
labels, opaque per-message span, attempt number and numeric returned status.
Only fixed method/query operation selectors are inspected; no URI, SAS, object
name, headers, body or error is retained. Unknown/ambiguous selectors stay Other.
Each SDK transport-policy entry is recorded separately, including existing SDK
retries; the policy forwards the same message/result/exception/token and does not
change retry or network-timeout options. Bus/journal clients are undecorated.

A transport return is not complete response-body consumption, Blob integrity
validation or business commit. A returned retryable status may be followed by an
unobserved retry delay; attempt-start likewise is not proof that bytes reached a
server. Synthetic pause/retry controls verify observation and ownership, not actual
provider root cause. Actual configured-endpoint round trips cover conflict lookup,
read and deletion separately from the real folder-read receipt upload routes.

Framework contract: [HttpClient timeout](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.timeout?view=net-10.0)
and [delegating-handler cancellation](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.delegatinghandler.sendasync?view=net-10.0).
Async EF boundary contract: [EF Core interceptors](https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors).
Pinned Azure.Core 1.55.0 placement: [pipeline builder](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.55.0/sdk/core/Azure.Core/src/Pipeline/HttpPipelineBuilder.cs)
and [BeforeTransport contract](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.55.0/sdk/core/Azure.Core/src/HttpPipelinePosition.cs).
