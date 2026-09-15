using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace singC.Models
{
    public class TrafficData
    {
        [JsonPropertyName("UsedGB")]
        public double? UsedGB { get; set; }

        [JsonPropertyName("RemainingGB")]
        public double? RemainingGB { get; set; }

        [JsonPropertyName("ResetTimestamp")]
        public long? ResetTimestamp { get; set; }   // Unix 秒级时间戳

        public bool Cached { get; set; }
        public bool Stale { get; set; }
        public bool Backoff { get; set; }
        public long CacheAgeSeconds { get; set; }
        public long RetryAfterSeconds { get; set; }
        public string? LastError { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonIgnore]
        public bool HasTrafficData => string.IsNullOrEmpty(Error)
            && UsedGB is >= 0 && RemainingGB is >= 0
            && double.IsFinite(UsedGB.Value) && double.IsFinite(RemainingGB.Value)
            && ResetTimestamp is >= 0 and <= 253402300799;
    }


    public class ConnectionInfo : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        private string? _network;
        public string? Network
        {
            get => _network;
            set { _network = value; OnPropertyChanged(); }
        }

        private string? _source;
        public string? Source
        {
            get => _source;
            set { _source = value; OnPropertyChanged(); OnPropertyChanged(nameof(AddressDisplay)); }
        }

        private string? _destination;
        public string? Destination
        {
            get => _destination;
            set { _destination = value; OnPropertyChanged(); OnPropertyChanged(nameof(AddressDisplay)); OnPropertyChanged(nameof(PrimaryDisplay)); OnPropertyChanged(nameof(SecondaryDisplay)); }
        }

        private DateTime _startTime;
        public DateTime StartTime
        {
            get => _startTime;
            set { _startTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(StartTimeDisplay)); }
        }

        private long _uploadBytes;
        public long UploadBytes
        {
            get => _uploadBytes;
            set { _uploadBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(UploadDisplay)); OnPropertyChanged(nameof(TrafficDisplay)); }
        }

        private long _downloadBytes;
        public long DownloadBytes
        {
            get => _downloadBytes;
            set { _downloadBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(DownloadDisplay)); OnPropertyChanged(nameof(TrafficDisplay)); }
        }

        private string? _host;
        public string? Host
        {
            get => _host;
            set { _host = value; OnPropertyChanged(); OnPropertyChanged(nameof(PrimaryDisplay)); OnPropertyChanged(nameof(SecondaryDisplay)); }
        }

        private string? _rule;
        public string? Rule
        {
            get => _rule;
            set { _rule = value; OnPropertyChanged(); }
        }

        // 已有的格式化属性保持不变，但需在相关字段变更时触发通知（已在上面处理）
        public string UploadDisplay => FormatBytes(UploadBytes);
        public string DownloadDisplay => FormatBytes(DownloadBytes);
        public string StartTimeDisplay => StartTime.ToString("HH:mm:ss");
        public string TrafficDisplay => $"↑ {UploadDisplay}   ↓ {DownloadDisplay}";
        public string AddressDisplay => $"{Source} → {Destination}";
        public string PrimaryDisplay => string.IsNullOrWhiteSpace(Host) ? Destination ?? string.Empty : Host!;
        public string SecondaryDisplay => string.IsNullOrWhiteSpace(Host) ? Source ?? string.Empty : $"{Source} → {Destination}";

        public string ToClipboardText() => string.Join(Environment.NewLine,
            $"主机: {PrimaryDisplay}",
            $"源地址: {Source ?? "--"}",
            $"目标地址: {Destination ?? "--"}",
            $"网络: {Network ?? "--"}",
            $"规则: {Rule ?? "--"}",
            $"开始时间: {StartTime:yyyy-MM-dd HH:mm:ss}",
            $"上传: {UploadDisplay}",
            $"下载: {DownloadDisplay}");

        // 用于 ID 唯一比较
        public override bool Equals(object? obj) => obj is ConnectionInfo other && Id == other.Id;
        public override int GetHashCode() => Id?.GetHashCode() ?? 0;

        // 从另一实例复制所有属性，并触发通知
        public bool UpdateFrom(ConnectionInfo other)
        {
            bool changed = false;

            if (!string.Equals(Network, other.Network)) { Network = other.Network; changed = true; }
            if (!string.Equals(Source, other.Source)) { Source = other.Source; changed = true; }
            if (!string.Equals(Destination, other.Destination)) { Destination = other.Destination; changed = true; }
            if (StartTime != other.StartTime) { StartTime = other.StartTime; changed = true; }
            if (UploadBytes != other.UploadBytes) { UploadBytes = other.UploadBytes; changed = true; }
            if (DownloadBytes != other.DownloadBytes) { DownloadBytes = other.DownloadBytes; changed = true; }
            if (!string.Equals(Host, other.Host)) { Host = other.Host; changed = true; }
            if (!string.Equals(Rule, other.Rule)) { Rule = other.Rule; changed = true; }

            return changed;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            double gb = mb / 1024.0;
            return $"{gb:F2} GB";
        }
    }

    public class ConnectionsResponse
    {
        [JsonPropertyName("connections")]
        public required List<Connection> Connections { get; set; }

        [JsonPropertyName("downloadTotal")]
        public required long DownloadTotal { get; set; }

        [JsonPropertyName("uploadTotal")]
        public required long UploadTotal { get; set; }

        [JsonPropertyName("memory")]
        public required long Memory { get; set; }
    }

    // 单个连接信息
    public class Connection
    {
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("chains")]
        public required List<string> Chains { get; set; }

        [JsonPropertyName("download")]
        public required long Download { get; set; }

        [JsonPropertyName("upload")]
        public required long Upload { get; set; }

        [JsonPropertyName("start")]
        public required DateTime Start { get; set; }

        [JsonPropertyName("rule")]
        public required string Rule { get; set; }

        [JsonPropertyName("rulePayload")]
        public required string RulePayload { get; set; }

        [JsonPropertyName("metadata")]
        public required Metadata Metadata { get; set; }
    }

    // 连接的元数据
    public class Metadata
    {
        [JsonPropertyName("destinationIP")]
        public required string DestinationIP { get; set; }

        [JsonPropertyName("destinationPort")]
        public required string DestinationPort { get; set; }

        [JsonPropertyName("dnsMode")]
        public required string DnsMode { get; set; }

        [JsonPropertyName("host")]
        public required string Host { get; set; }

        [JsonPropertyName("network")]
        public required string Network { get; set; }

        [JsonPropertyName("processPath")]
        public required string ProcessPath { get; set; }

        [JsonPropertyName("sourceIP")]
        public required string SourceIP { get; set; }

        [JsonPropertyName("sourcePort")]
        public required string SourcePort { get; set; }

        [JsonPropertyName("type")]
        public required string Type { get; set; }
    }
}
