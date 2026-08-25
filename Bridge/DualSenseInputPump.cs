using HidSharp;

namespace DMC5DualSense.Bridge;

internal sealed class DualSenseInputPump : IDisposable
{
    private const int SonyVendorId = 0x054C;
    private static readonly int[] SupportedProductIds = [0x0CE6, 0x0DF2, 0x0E5F];

    private readonly object _gate = new();
    private readonly VirtualXboxInput _virtualInput;
    private readonly Action<string> _log;
    private HidStream? _stream;
    private string _status = "not started";
    private long _validReports;
    private int _lastSystemButtons;

    public DualSenseInputPump(VirtualXboxInput virtualInput, Action<string> log)
    {
        _virtualInput = virtualInput;
        _log = log;
    }

    public bool Connected
    {
        get { lock (_gate) return _stream is not null; }
    }

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public long ValidReports => Interlocked.Read(ref _validReports);

    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Run(cancellationToken), cancellationToken);

    private void Run(CancellationToken cancellationToken)
    {
        var wasConnected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!EnsureConnected())
            {
                if (wasConnected)
                {
                    _log($"DualSense input disconnected: {Status}");
                    wasConnected = false;
                }
                cancellationToken.WaitHandle.WaitOne(500);
                continue;
            }

            if (!wasConnected)
            {
                _log($"Direct DualSense input ready: {Status}");
                wasConnected = true;
            }

            HidStream? stream;
            lock (_gate) stream = _stream;
            if (stream is null) continue;

            try
            {
                var buffer = new byte[64];
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read > 0 && DualSenseInputReport.TryParse(buffer.AsSpan(0, read), out var report))
                {
                    Interlocked.Increment(ref _validReports);
                    LogSystemButtonTransition(buffer, report);
                    _virtualInput.Submit(report);
                }
            }
            catch (TimeoutException)
            {
                // A quiet controller still normally reports at 250 Hz; retrying also
                // handles short USB scheduling stalls without disconnecting the pad.
            }
            catch (Exception ex)
            {
                Disconnect(ex.Message);
            }
        }
    }

    private void LogSystemButtonTransition(byte[] buffer, XboxInputReport report)
    {
        // Log only edges of the four central controls. This makes touchpad input
        // failures diagnosable without adding another 250 lines per second.
        var systemButtons = (buffer[9] & 0x30) | ((buffer[10] & 0x03) << 8);
        if (systemButtons == _lastSystemButtons) return;

        var previous = _lastSystemButtons;
        _lastSystemButtons = systemButtons;
        if (systemButtons == 0 && previous == 0) return;

        _log($"Direct input system buttons: Create={((buffer[9] & 0x10) != 0 ? 1 : 0)}, " +
             $"Options={((buffer[9] & 0x20) != 0 ? 1 : 0)}, " +
             $"PS={((buffer[10] & 0x01) != 0 ? 1 : 0)}, " +
             $"Touchpad={((buffer[10] & 0x02) != 0 ? 1 : 0)}, " +
             $"mapped=0x{report.Buttons:X4}.");
    }

    private bool EnsureConnected()
    {
        lock (_gate)
        {
            if (_stream is not null) return true;

            foreach (var productId in SupportedProductIds)
            {
                foreach (var candidate in DeviceList.Local.GetHidDevices(SonyVendorId, productId))
                {
                    try
                    {
                        if (candidate.GetMaxInputReportLength() < 64) continue;
                        if (!candidate.TryOpen(out var stream)) continue;

                        stream.ReadTimeout = 500;
                        stream.WriteTimeout = 250;
                        _stream = stream;
                        _status = $"VID_{candidate.VendorID:X4}/PID_{candidate.ProductID:X4}, " +
                                  $"input={candidate.GetMaxInputReportLength()} bytes";
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _status = ex.Message;
                    }
                }
            }

            if (_status == "not started") _status = "writable DualSense input interface not found";
            return false;
        }
    }

    private void Disconnect(string reason)
    {
        lock (_gate)
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
            _status = reason;
        }
    }

    public void Dispose() => Disconnect("stopped");
}
