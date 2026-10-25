# Explicit public runtime suite: temporary loopback fixtures only, no installed model.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$tests=@(
    'gateway/test-agent-sessions.mjs','gateway/test-context-limits.mjs',
    'gateway/test-conversations.mjs','gateway/test-delete.mjs','gateway/test-media.mjs',
    'gateway/test-runtime.mjs','gateway/test-strata.mjs','gateway/test-strata-benchmark.mjs',
    'gateway/test-token-metrics.mjs','worker/test-stream.mjs'
) | ForEach-Object { Join-Path $root ('runtime/'+$_) }
& node --test @tests
if ($LASTEXITCODE -ne 0) { throw 'Runtime tests failed' }
