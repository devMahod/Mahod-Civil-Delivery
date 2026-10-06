using System;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Process-local family-assistant admission, shared by all dialogs/batches. No policy or Core change.</summary>
public static class FamilyAssistProviderAdmission
{
    private static int _provider, _dispatch;
    public const string RetryBlocked = "הבקשה הקודמת עדיין מסתיימת אצל שירות העוזר. לא תישלח בקשה נוספת עד שהפעולה המקורית תסתיים. אפשר לבטל/לסגור את החלון; תשובה מאוחרת לא תוחל.";
    public static bool IsBusy => Volatile.Read(ref _provider) != 0 || Volatile.Read(ref _dispatch) != 0;
    internal static bool ProviderBusy => Volatile.Read(ref _provider) != 0;
    public static IFamilyRecognitionProvider Wrap(IFamilyRecognitionProvider provider) => new GuardedProvider(provider);

    internal static IDisposable? TryEnterDispatch()
    {
        if (ProviderBusy || Interlocked.CompareExchange(ref _dispatch, 1, 0) != 0) return null;
        return new Lease(() => Interlocked.Exchange(ref _dispatch, 0));
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) release(); }
    }
    private sealed class GuardedProvider(IFamilyRecognitionProvider inner) : IFamilyRecognitionProvider, IFamilyVisionGate
    {
        // Preserve exactly the old optional gate semantics. Exceptions still reach Core's fail-closed policy handling.
        public bool IsVisionAllowed() => inner is not IFamilyVisionGate gate || gate.IsVisionAllowed();
        public Task<FamilyRankResponse> RankFamiliesAsync(FamilyRankRequest request, CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _provider, 1, 0) != 0)
                return Task.FromException<FamilyRankResponse>(new InvalidOperationException(RetryBlocked));
            var lease = new Lease(() => Interlocked.Exchange(ref _provider, 0));
            // Core can stop waiting sooner than the underlying provider completes.
            // This lease follows the actual Task, including synchronous-before-Task work.
            return Task.Run(async () => {
                try { token.ThrowIfCancellationRequested(); return await inner.RankFamiliesAsync(request, token).ConfigureAwait(false); }
                finally { lease.Dispose(); }
            });
        }
    }
}
