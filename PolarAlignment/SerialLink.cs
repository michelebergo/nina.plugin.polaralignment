using System;
using System.IO.Ports;

namespace NINA.Plugins.PolarAlignment {

    /// <summary>
    /// The wire seam: everything the alignment systems do with a serial port goes through
    /// this interface. Production uses <see cref="SerialPortLink"/> over a real SerialPort;
    /// tests inject a scripted link and assert the exact bytes on the wire. This seam exists
    /// because the driver-current grammar bug (axis-first commands silently ignored by the
    /// firmware) lived for months in the one layer no test could reach.
    /// </summary>
    public interface ISerialLink : IDisposable {
        bool IsOpen { get; }

        void WriteLine(string text);

        string ReadLine();
    }

    internal sealed class SerialPortLink : ISerialLink {
        private readonly SerialPort port;

        public SerialPortLink(SerialPort port) {
            this.port = port;
        }

        public bool IsOpen => port.IsOpen;

        public void WriteLine(string text) => port.WriteLine(text);

        public string ReadLine() => port.ReadLine();

        public void Dispose() => port.Dispose();
    }
}
