# 部署说明

## Windows 服务端

前置条件：

1. 安装 usbipd-win，并确认 `usbipd.exe` 可执行。
2. 以管理员权限运行 MyUsbIP。
3. 防火墙允许 TCP 3240，建议仅允许可信局域网/VPN 网段。

常用验证：

```bat
usbipd list
myusbip server list
myusbip server diag
```

共享设备：

```bat
myusbip server share 2-3
```

## Windows 客户端

前置条件：

1. 安装 usbip-win/VHCI 或兼容虚拟 USB Host Controller。
2. `usbip.exe` 可执行。
3. 管理员权限。

```bat
myusbip client list 192.168.1.100
myusbip client attach 192.168.1.100 2-3
myusbip client diag
```

## Linux 服务端

安装 usbip 工具。不同发行版包名可能不同，例如 Debian/Ubuntu 通常来自 `linux-tools`/`usbip` 相关包。

加载服务端模块：

```bash
sudo modprobe usbip_host
sudo usbip list -l
sudo myusbip server list
```

共享：

```bash
sudo myusbip server share 1-2
```

## Linux 客户端

```bash
sudo modprobe vhci_hcd
sudo myusbip client list 192.168.1.100
sudo myusbip client attach 192.168.1.100 1-2
```

## 守护进程

`myusbipd` 读取 `appsettings.json`。

### 自动共享指定 UKey

```json
{
  "AutoShareRules": [
    {
      "VendorId": "096E",
      "ProductId": "0303",
      "Enabled": true
    }
  ],
  "ManagedConnections": []
}
```

### 客户端固定连接

```json
{
  "AutoShareRules": [],
  "ManagedConnections": [
    {
      "Host": "192.168.1.100",
      "BusId": "2-3",
      "Port": 3240,
      "Enabled": true
    }
  ]
}
```

运行：

```text
myusbipd appsettings.json
```

## systemd 示例

```ini
[Unit]
Description=MyUsbIP Daemon
After=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/myusbip
ExecStart=/usr/bin/dotnet /opt/myusbip/MyUsbIP.Daemon.dll /etc/myusbip/appsettings.json
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
```

## Windows 服务化

v1.0 的 Daemon 本身是标准控制台宿主，推荐用 Windows `sc.exe` 配合服务包装器、NSSM/WinSW，或者集成到现有 Windows Service 宿主。核心运行时类库不依赖控制台，可直接在 `BackgroundService` 中调用。

## 网络

标准 USB/IP 默认 TCP 3240。不要直接暴露到公网。跨公网场景建议使用 WireGuard、Tailscale、ZeroTier、IPSec 或其他可信隧道。
