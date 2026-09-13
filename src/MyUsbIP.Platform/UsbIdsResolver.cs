using MyUsbIP.Abstractions;

namespace MyUsbIP.Platform;

internal sealed class UsbIdsResolver
{
    private readonly string? explicitPath;
    private readonly object sync = new();
    private string? loadedPath;
    private readonly Dictionary<ushort, string> vendors = new();
    private readonly Dictionary<uint, string> products = new();

    public UsbIdsResolver(string? explicitPath = null)
    {
        this.explicitPath = string.IsNullOrWhiteSpace(explicitPath)
            ? null
            : Environment.ExpandEnvironmentVariables(explicitPath);
    }

    public UsbIpDeviceInfo Enrich(UsbIpDeviceInfo device)
    {
        EnsureLoaded();
        vendors.TryGetValue(device.VendorId, out var vendorName);
        products.TryGetValue(ToKey(device.VendorId, device.ProductId), out var productName);

        var manufacturer = IsUnknownVendor(device.Manufacturer) ? vendorName : device.Manufacturer;
        var product = IsUnknownProduct(device.Product) ? productName : device.Product;
        if (IsUnknownProduct(product)) product = $"USB device {device.ProductId:X4}";

        return device with
        {
            Manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? device.Manufacturer : manufacturer,
            Product = product,
        };
    }

    private void EnsureLoaded()
    {
        if (loadedPath is not null) return;
        lock (sync)
        {
            if (loadedPath is not null) return;
            foreach (var path in GetCandidates())
            {
                if (!File.Exists(path)) continue;
                try
                {
                    Parse(path);
                    loadedPath = path;
                    return;
                }
                catch
                {
                    vendors.Clear();
                    products.Clear();
                }
            }
            loadedPath = string.Empty;
        }
    }

    private IEnumerable<string> GetCandidates()
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath;
        yield return Path.Combine(AppContext.BaseDirectory, "usb.ids");
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
                yield return Path.Combine(programFiles, "USBip", "usb.ids");
        }
    }

    private void Parse(string path)
    {
        ushort? currentVendor = null;
        foreach (var raw in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw[0] == '#') continue;
            if (raw[0] != '\t')
            {
                if (raw.Length < 5 || !ushort.TryParse(raw.AsSpan(0, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var vid))
                {
                    currentVendor = null;
                    continue;
                }
                vendors[vid] = raw[4..].Trim();
                currentVendor = vid;
                continue;
            }

            if (raw.Length > 1 && raw[1] == '\t') continue;
            if (currentVendor is null) continue;
            var text = raw.TrimStart('\t');
            if (text.Length < 5 || !ushort.TryParse(text.AsSpan(0, 4), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var pid)) continue;
            products[ToKey(currentVendor.Value, pid)] = text[4..].Trim();
        }
    }

    internal static bool IsUnknownVendor(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value.Contains("unknown vendor", StringComparison.OrdinalIgnoreCase)
           || value.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnknownProduct(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value.Contains("unknown product", StringComparison.OrdinalIgnoreCase)
           || value.Contains("unknown vendor", StringComparison.OrdinalIgnoreCase)
           || value.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static uint ToKey(ushort vid, ushort pid) => ((uint)vid << 16) | pid;
}
