using System;
using System.Windows.Threading;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Coalesces display-only search refreshes; never writes text, changes focus or captures a row subset.</summary>
internal sealed class QuantitySearchDebouncer
{
    private readonly DispatcherTimer _timer;
    private readonly Action _refresh;
    internal bool IsPending => _timer.IsEnabled;

    internal QuantitySearchDebouncer(Dispatcher dispatcher, Action refresh)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => { _timer.Stop(); _refresh(); };
    }

    internal void Schedule() { _timer.Stop(); _timer.Start(); }
    internal void Cancel() => _timer.Stop();
}
