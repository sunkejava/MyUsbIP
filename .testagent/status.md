# CH340 regression quality review

User requirement: `ch340设备远程二次attach异常超时，排查原因及修复`.

## Implementation and acceptance evidence

34 named regression cases in `tests/MyUsbIP.SmokeTests/Ch340RecoverySmokeChecks.cs`, using the existing executable smoke runner. Tests execute pure production policy helpers and callback-driven recovery, with no real Windows native calls or USB changes.

| Requirement | Evidence |
| --- | --- |
| Restart readiness succeeds without cycling an already healthy device | `Recovery_HealthyAfterRestart_DoesNotCycle` asserts Ready and exact restart/wait callback sequence |
| A failed initial readiness check can recover through only the target port, then must check readiness again | `Recovery_UnhealthyAfterRestart_CyclesThenChecksAgain` asserts restart/wait/cycle/wait order and preserved diagnostics |
| Failed recovery cannot be reported as success or authorize capture | `Recovery_CycleFailure_RemainsFailedWithoutSecondWait`, `Recovery_UnhealthyAfterCycle_RejectsCapture` assert false result, failure exception, and no extra wait after failed cycle |
| Cancellation performs no later recovery action | `Recovery_CancelledBeforeStart_PerformsNoAction`, `Recovery_CancelledByRestart_DoesNotWaitOrCycle`, `Recovery_CancelledByFirstWait_DoesNotCycle`, `Recovery_CancelledByCycle_DoesNotWaitAgain`, `Recovery_CancelledByHealthyFirstWait_DoesNotReportReady`, `Recovery_CancelledByFinalWait_DoesNotReportReady` assert cancellation and exact callback prefix |
| Native failures must propagate, without a false success result | `Recovery_RestartException_DoesNotReportSuccessOrContinue`, `Recovery_CycleException_DoesNotReportSuccessOrContinue`, `Recovery_ReadinessException_DoesNotCycle` assert original Win32 exception identity and callback sequence |
| Host readiness requires successful current status query, DN_STARTED, no DN_HAS_PROBLEM | `Readiness_StartedWithoutProblem_IsReady`, `Readiness_NotStarted_IsNotReady`, `Readiness_ProblemFlag_IsNotReady`, `Readiness_StatusQueryFailure_IsNotReady`; `Readiness_ProblemNumberWithoutFlag_IsIgnored` preserves Windows documented semantics for undefined problem output |
| Select only a unique full identity; a short CH340 port ID must never choose an arbitrary same-port device | `Identity_FullExactMatch_SelectsOnlyTarget`, `Identity_UniqueTailAndPortMatch_SelectsTarget`, `Identity_AmbiguousSamePort_DoesNotChooseDevice`, `Identity_WithoutInstance_RequiresUniqueCandidate` |
| A missing identity is absent, and device-ID-only candidates cannot cause invalid tail slicing | `Identity_MissingDevice_IsAbsent`, `Identity_DeviceIdOnlyCandidate_DoesNotThrowOrMatchUnrelatedTail` |
| DriverKey buffer respects DWORD/DWORD/UTF-16 ABI and rejects invalid/truncated/odd/out-of-bounds/unterminated keys | `DriverKey_ValidAbiBuffer_ReadsUtf16AtOffsetEight`, `DriverKey_TruncatedHeader_IsRejected`, `DriverKey_InvalidDeclaredLength_IsRejected`, `DriverKey_MissingTerminator_IsRejected`, `DriverKey_EmptyOrBlankKey_IsRejected` |
| Port recovery requires saved Hub identity, port bounds 1..255 and DriverKey; current key must match or port must be independently confirmed empty | `HubPort_ValidVerifiedTopology_CanCycle`, `HubPort_MissingIdentity_CannotCycle`, `HubPort_PortBounds_AreEnforced`, `HubPort_CurrentDriverKeyMustMatchSavedDevice`, `HubPort_MissingKeyRequiresConfirmedEmptyPort` |

## Test-gap-analysis review

Bounded review of the new tests against CH340 workflow/readiness/identity/port policy branches. High-risk candidate changes include allowing a failed cycle to count as ready, removing the pre-cycle cancellation check, choosing the first ambiguous candidate, allowing status-query failure, and permitting a changed DriverKey. Each is directly observed by named tests above through result, exception, identity, or callback-order assertions. These are static likely-killed findings; no mutation run has been performed in the shared production workspace.

Review found a distinct device-ID-only candidate bounds path which initially had no coverage. Added `Identity_DeviceIdOnlyCandidate_DoesNotThrowOrMatchUnrelatedTail`; parent fixed the tail slicing guard. Added cancellation tests after restart and cycle so synchronous native callbacks cannot trigger later actions after cancellation. Healthy initial/final readiness results cannot bypass cancellation checks or return Ready after cancellation. No unresolved pure-policy branch gap identified in bounded scope.

Native SetupAPI/Configuration Manager/Hub IOCTL behavior, UsbDk redirect cache/handle lifetime, and actual CH340 detach/reattach timing require integration or physical-machine evidence. This suite does not claim to prove hardware recovery.

The mandatory Roslyn source/test pairing analyzer completed successfully with SDK-local Roslyn assemblies. It classified 52 source files and zero test files because the repository's executable SmokeTests project is not recognized as a conventional test project. This is a detection limitation, not evidence of 52 untested files; the reviewed production-helper-to-smoke-case mapping above is the bounded pairing evidence.

## Assertion-quality review

Loaded `assertion-quality` and the .NET `test-analysis-extensions` reference. Existing smoke convention uses `Check`, `Throws`/`ThrowsAsync` and `CheckCalls` instead of a framework API. All 34 registered cases have observable assertions; no assertion-free, always-true or only-presence happy-path tests. Assertions include exact equality, negative outcomes, collection sequence and side effects, exception type/identity, diagnostic strings, boundary comparisons, and structured ABI inputs. Invalid-key/null-identity checks intentionally assert rejection policy, rather than merely asserting object existence. Workflow tests await every operation and exception assertion.

## Validation

Production API edits and smoke runner wiring are complete. Parent performed the following sequential validation; generator started no competing build.

| Successful command | Result |
| --- | --- |
| `/tmp/myusbip-dotnet/dotnet build MyUsbIP.slnx -c Release -m:1 -p:UseSharedCompilation=false` | Exit 0; 0 errors, 281 preexisting CS1591 documentation warnings; 56.97 seconds |
| `/tmp/myusbip-dotnet/dotnet build tests/MyUsbIP.SmokeTests/MyUsbIP.SmokeTests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false` | Exit 0; 0 warnings, 0 errors; 6.65 seconds |
| `/tmp/myusbip-dotnet/dotnet tests/MyUsbIP.SmokeTests/bin/Release/net10.0/MyUsbIP.SmokeTests.dll` | Exit 0; all 34 named CH340 cases `[PASS]`; `CH340 recovery checks: PASS (34)` and `MyUsbIP smoke tests: PASS` |

These clean results validate the pure policy and callback regression suite, including cancellation, failed readiness, ambiguous identity and verified port safeguards. Physical CH340 repeat attach validation remains separate.

## Release validation

Release code commit: c0efcee66ed9211557a143eb51974bf40690c915. Main CI run 37575288685 succeeded; release CI run 37575289403 completed all five jobs successfully (Windows/Linux build + smoke + publish, both trimming audits, release). Both platform logs include `CH340 recovery checks: PASS (34)` and `MyUsbIP smoke tests: PASS`. Windows setup verification passed NativeAOT help/options, local and embedded offline packages, missing/corrupted payload rejection.

Published v1.1.18 has exactly eight non-draft, non-prerelease assets. Downloaded all eight and matched byte counts and GitHub SHA256 digests; six Windows package checksums also match checksums.sha256. Parsed both actual online PE embedded manifests: version 1.1.18, fixed download URL, package name, size and SHA256 all bind to the corresponding published Windows ZIP. Extracted Linux CLI from published archive and executed its supported no-argument help path successfully. No physical Windows driver installation or CH340 recovery was performed in automated validation.
