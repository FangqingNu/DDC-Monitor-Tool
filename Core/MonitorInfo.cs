using System;
using System.Collections.Generic;

namespace DdcMonitorTool.Core
{
    /// <summary>一块显示器的完整画像。所有字段都来自运行时探测，读不到就是 null / Unknown。</summary>
    public sealed class MonitorInfo
    {
        // Windows 侧身份
        public string DeviceName { get; set; } = string.Empty;   // \\.\DISPLAY1
        public string PhysicalDescription { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }

        // 当前模式
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? RefreshHz { get; set; }

        // 拓扑（DisplayConfig）
        public string FriendlyName { get; set; } = string.Empty;
        public string MonitorDevicePath { get; set; } = string.Empty;
        public uint OutputTechnology { get; set; }
        public bool HdrSupported { get; set; }
        public bool HdrEnabled { get; set; }
        public int BitsPerColor { get; set; }

        // EDID
        public EdidRecord? Edid { get; set; }
        public string EdidManufacturer => Edid?.Manufacturer ?? "Unknown";
        public string EdidModel => Edid?.Model ?? "Unknown";

        // DDC/CI
        public bool DdcSupported { get; set; }
        public string? DdcError { get; set; }
        public uint McCapsRaw { get; set; }
        public string? CapabilityString { get; set; }
        public string CapabilityModel { get; set; } = string.Empty;

        public Dictionary<byte, VcpFeature> VcpFeatures { get; } = new();

        public bool TryGet(byte code, out VcpFeature? feature) => VcpFeatures.TryGetValue(code, out feature);

        public bool Supports(byte code) => VcpFeatures.TryGetValue(code, out var f) && f.Supported;

        public int? Value(byte code)
            => VcpFeatures.TryGetValue(code, out var f) && f.Supported ? f.Current : null;

        /// <summary>
        /// 快照索引键：优先用 EDID，换接口/改分辨率都能对上。
        /// 两块型号完全相同的显示器 EDID 也一样，检测阶段会用 SnapshotKeyOverride 拆开。
        /// </summary>
        public string? SnapshotKeyOverride { get; set; }

        public string SnapshotKey
        {
            get
            {
                if (!string.IsNullOrEmpty(SnapshotKeyOverride)) return SnapshotKeyOverride!;
                if (Edid != null && !string.IsNullOrEmpty(Edid.StableKey)) return Edid.StableKey;
                return DeviceName;
            }
        }

        public string DisplayLabel
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(FriendlyName)) return FriendlyName;
                if (!string.IsNullOrWhiteSpace(CapabilityModel)) return CapabilityModel;
                if (Edid != null && Edid.Model != "Unknown")
                    return Edid.Manufacturer != "Unknown" ? $"{Edid.Manufacturer} {Edid.Model}" : Edid.Model;
                if (!string.IsNullOrWhiteSpace(PhysicalDescription)) return PhysicalDescription;
                return "未知显示器";
            }
        }

        public string ManufacturerLine
        {
            get
            {
                var bits = new List<string>();
                if (Edid != null && Edid.Manufacturer != "Unknown") bits.Add(Edid.Manufacturer);
                if (Edid != null && Edid.ProductCode.Length > 0) bits.Add("ID " + Edid.ProductCode);
                if (Edid?.Year is int y) bits.Add($"{y} 年");
                if (bits.Count == 0) bits.Add("EDID 未读到");
                return string.Join(" · ", bits);
            }
        }

        public string ResolutionLabel
            => Width.HasValue && Height.HasValue ? $"{Width} × {Height}" : "未知";

        public string RefreshLabel => RefreshHz.HasValue ? $"{RefreshHz:g} Hz" : "未知";

        public string ConnectionLabel => DisplayTopology.OutputTechnologyName(OutputTechnology);

        public string HdrLabel => !HdrSupported ? "不支持" : HdrEnabled ? "已开启" : "已关闭";

        public override string ToString()
        {
            string primary = IsPrimary ? "（主）" : string.Empty;
            return $"{DisplayLabel}{primary} · {ResolutionLabel} @ {RefreshLabel}";
        }
    }
}
