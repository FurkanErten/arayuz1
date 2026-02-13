// ConnectionManager.cs
using System;
using System.IO.Ports;
using System.Linq;

namespace arayuz_deneme_1
{
    public sealed class ConnectionManager : IDisposable
    {
        public static ConnectionManager Instance { get; } = new();

        private MavSerialParamDownloader? _downloader;
        public string? CurrentCom { get; private set; }
        public int CurrentBaud { get; private set; }

        private ConnectionManager() { }
        public void Dispose() => Disconnect();

        public void Connect(string comPort, int baud)
        {
            if (string.Equals(CurrentCom, comPort, StringComparison.OrdinalIgnoreCase) &&
                CurrentBaud == baud && _downloader != null) return;

            Disconnect();
            _downloader = new MavSerialParamDownloader();
            _downloader.Start(comPort, baud);
            CurrentCom = comPort; CurrentBaud = baud;
        }

        public void Disconnect()
        {
            try { _downloader?.Dispose(); } catch { }
            _downloader = null; CurrentCom = null; CurrentBaud = 0;
        }

        public bool IsConnected => _downloader != null;

        public static string[] GetSystemComPorts()
            => SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
