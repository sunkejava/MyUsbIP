# CH340 detach / reattach regression

User requirement: `ch340设备远程二次attach异常超时，排查原因及修复`.

Confirmed server v1.1.17 timeline: release at 10:46:37; recovery-failed (stable=false and missing saved leaf InstanceId) at 10:46:47; session nevertheless released at 10:46:48; second capture at 10:47:08; native error 1167 at 10:49:26. Client detach completion does not imply host stack recovery.

Bounded targets: UsbDkDeviceManager CH340 release/preflight, WindowsDeviceRecovery identity and readiness, WindowsDevicePresence unambiguous matching, target USB hub-port recovery; NativeServer release diagnostics. Existing SDK-style net10.0 executable smoke runner, no test framework package. Tests must use production helpers, fake recovery callbacks, and in-memory identity/status inputs; no real USB, Hub reset, network or PnP changes.

Acceptance: recovery only succeeds after fresh native identity + PnP started/no problem; failed restart and port recovery remain failures; a missing/ambiguous identity cannot be captured; fallback cycles only the saved and verified child port; stale cache cannot authorize StartRedirect; cancellation never performs a later recovery action; no false release-success after failed recovery.
