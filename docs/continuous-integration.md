# Continuous integration boundaries

The `ci` workflow keeps the original `build-test-package` check as the aggregate
release gate. It depends on `validate` and every instance of the three-suite
`test` matrix. It explicitly fails if either prerequisite was failed, cancelled
or skipped; it does not turn those dispositions into a skipped green check.

`validate` retains the locked restore, fatal compiler warnings, all twenty forced
enabled analyzer Rebuilds, both container targets and the operations/profile
checks. Each test suite compiles its own checkout and runs its complete inventory
on a separate runner. Messaging retains PostgreSQL 17, pinned Azurite, backup
tools and its existing hang timeout. Matrix failure does not cancel sibling
suites. No test/assertion deadline or 30-minute job deadline is increased.

Each test job attempts an unconditional upload of its raw TRX and console log,
including on failure. Missing reports cannot count as success. Hard runner loss
or cancellation can still prevent upload; this is not a durability guarantee.

After all prerequisite jobs succeed, the aggregate job downloads only the
current run's SHA-named test evidence. It checks all three reports, including
nonempty counters, every individual outcome and unique correlated test/execution
identities. It then builds its own checkout, publishes CLI/Gateway/Worker/Wake,
validates the archive and uploads the release with the raw reports. No build
directory is transferred between jobs. A successful upload is CI evidence, not
independent package acceptance or deployment authorization.

The previous exact-source run at `f4e71b0` remains cancelled during packaging,
with no uploaded artifact. Its timing was consistent with the old shared
30-minute budget, but the precise cancellation cause was not independently
established. This job separation removes that shared budget without relabelling
the cancelled run, diagnosing historical test timeouts or establishing an SLA.
