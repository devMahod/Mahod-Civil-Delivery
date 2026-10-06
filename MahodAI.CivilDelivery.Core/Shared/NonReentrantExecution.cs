using System;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>Suppress synchronous callbacks into an atomic presentation refresh.</summary>
public sealed class NonReentrantExecution
{
    public bool IsActive { get; private set; }
    public bool Run(Action action)
    {
        if (IsActive) return false;
        IsActive = true;
        try { action(); return true; }
        finally { IsActive = false; }
    }
}
