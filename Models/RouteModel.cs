using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace singC.Models
{
    public class RouteRule : INotifyPropertyChanged
    {
        // 原有属性（保持不变）
        private string _action = string.Empty;
        public string Action { get => _action; set { _action = value; OnPropertyChanged(); } }

        private string _protocol = string.Empty;
        public string Protocol { get => _protocol; set { _protocol = value; OnPropertyChanged(); } }

        private string _network = string.Empty;
        public string Network { get => _network; set { _network = value; OnPropertyChanged(); } }

        private int? _port;
        public int? Port
        {
            get => _port;
            set { _port = value; OnPropertyChanged(); OnPropertyChanged(nameof(PortDouble)); }
        }
        private string _ipCidr = string.Empty;
        public string IpCidr
        {
            get => _ipCidr;
            set { _ipCidr = value; OnPropertyChanged(); }
        }

        // 新增：精确域名（支持逗号分隔多个）
        private string _domain = string.Empty;
        public string Domain
        {
            get => _domain;
            set { _domain = value; OnPropertyChanged(); }
        }
        // 用于 NumberBox 绑定的 double 属性
        public double PortDouble
        {
            get => _port.HasValue ? (double)_port.Value : 0.0; // 或 double.NaN
            set
            {
                if (value > 0)
                    Port = (int)value;
                else
                    Port = null;
                OnPropertyChanged();
            }
        }

        private bool _ipIsPrivate;
        public bool IpIsPrivate { get => _ipIsPrivate; set { _ipIsPrivate = value; OnPropertyChanged(); } }

        private string _outbound = string.Empty;
        public string Outbound { get => _outbound; set { _outbound = value; OnPropertyChanged(); } }

        private string _ruleSet = string.Empty;
        public string RuleSet { get => _ruleSet; set { _ruleSet = value; OnPropertyChanged(); } }

        private string _domainSuffix = string.Empty;
        public string DomainSuffix { get => _domainSuffix; set { _domainSuffix = value; OnPropertyChanged(); } }

        // INotifyPropertyChanged 实现
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
