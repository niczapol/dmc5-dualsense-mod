using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace DMC5DualSense.Bridge;

internal sealed class VirtualXboxInput : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<byte, byte> _feedback;
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private string _status = "disabled";
    private long _submittedReports;

    public VirtualXboxInput(Action<byte, byte> feedback)
    {
        _feedback = feedback;
    }

    public bool Started
    {
        get { lock (_gate) return _controller is not null; }
    }

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public long SubmittedReports => Interlocked.Read(ref _submittedReports);

    public bool Start()
    {
        lock (_gate)
        {
            if (_controller is not null) return true;

            try
            {
                _client = new ViGEmClient();
                _controller = _client.CreateXbox360Controller();
                _controller.AutoSubmitReport = false;
                _controller.FeedbackReceived += OnFeedbackReceived;
                _controller.Connect();
                // ViGEm reports the XInput user index asynchronously. Querying it
                // immediately can throw even though the virtual pad is healthy.
                _status = "virtual Xbox 360 connected";
                return true;
            }
            catch (Exception ex)
            {
                _status = ex.ToString();
                DisconnectNoThrow();
                return false;
            }
        }
    }

    public bool Submit(XboxInputReport report)
    {
        lock (_gate)
        {
            if (_controller is null) return false;

            try
            {
                _controller.SetButtonsFull(report.Buttons);
                _controller.SetAxisValue(Xbox360Axis.LeftThumbX, report.LeftThumbX);
                _controller.SetAxisValue(Xbox360Axis.LeftThumbY, report.LeftThumbY);
                _controller.SetAxisValue(Xbox360Axis.RightThumbX, report.RightThumbX);
                _controller.SetAxisValue(Xbox360Axis.RightThumbY, report.RightThumbY);
                _controller.SetSliderValue(Xbox360Slider.LeftTrigger, report.LeftTrigger);
                _controller.SetSliderValue(Xbox360Slider.RightTrigger, report.RightTrigger);
                _controller.SubmitReport();
                Interlocked.Increment(ref _submittedReports);
                return true;
            }
            catch (Exception ex)
            {
                _status = ex.Message;
                return false;
            }
        }
    }

    private void OnFeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs args)
    {
        _feedback(args.LargeMotor, args.SmallMotor);
    }

    private void DisconnectNoThrow()
    {
        if (_controller is not null)
        {
            try { _controller.FeedbackReceived -= OnFeedbackReceived; } catch { }
            try { _controller.ResetReport(); } catch { }
            try { _controller.SubmitReport(); } catch { }
            try { _controller.Disconnect(); } catch { }
            _controller = null;
        }

        try { _client?.Dispose(); } catch { }
        _client = null;
        _feedback(0, 0);
    }

    public void Dispose()
    {
        lock (_gate) DisconnectNoThrow();
    }
}
