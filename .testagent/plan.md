# CH340 regression plan

1. Ch340RecoveryWorkflow: callback-driven restart → strict readiness → verified saved port-cycle → strict readiness. Test successful restart without cycle, failed readiness with successful cycle, cycle failure, readiness failure after cycle, cancellation, and native action failure.
2. WindowsDevicePresence: unique exact/suffix matching; ambiguous same-port CH340s must not pick the first device. Test unique, duplicate, missing, exact full identity, and conservative physical presence.
3. WindowsDeviceRecovery.IsReadyStatus: CR_SUCCESS, DN_STARTED, no DN_HAS_PROBLEM; test all rejected statuses and healthy status.
4. WindowsUsbHubPortRecovery: capture exact hub path + address + driver-key match before Redirect; no capture = no cycle. Test driver-key buffer parsing and hub-port policy without native calls.
5. UsbDkDeviceManager: fresh enumeration and strict identity/status preflight; release failures throw; no cache fallback on capture. Verify integration by source review and Windows/Linux CI smoke/build; physical CH340 timing remains a real-machine acceptance check.
6. Run executable smoke tests and full build, then test-gap-analysis / assertion-quality review, recording final clean results in status.md.
