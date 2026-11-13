using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Auth;

/// <summary>
/// Epic sign-in via the host's login window (an exchange code from Epic's launcher bridge) → `legendary auth --token`.
/// Never uses `auth --import`: it spends EGL's refresh token and logs EGL out.
/// </summary>
internal sealed class AuthService
{
    private readonly ServiceContext _c;
    private int _signingIn;

    public AuthService(ServiceContext context) => _c = context;

    public async Task<AuthState> SignInAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _signingIn, 1) == 1)
        {
            throw new GliderException(Errors.Busy("sign-in"));
        }

        var previous = _c.Hub.State.Auth;
        try
        {
            _c.Hub.Mutate(s => s with { Auth = new AuthState(AuthStatus.SigningIn, null) });
            var code = await _c.Host.AcquireEpicExchangeCodeAsync(ct);
            if (string.IsNullOrWhiteSpace(code))
            {
                _c.Hub.Mutate(s => s with { Auth = previous });
                throw new GliderException(Errors.AuthCancelled());
            }

            return await CompleteAsync(code, ct);
        }
        catch (Exception) when (_c.Hub.State.Auth.Status == AuthStatus.SigningIn)
        {
            _c.Hub.Mutate(s => s with { Auth = previous });
            throw;
        }
        finally
        {
            Volatile.Write(ref _signingIn, 0);
        }
    }

    private async Task<AuthState> CompleteAsync(string code, CancellationToken ct)
    {
        LegendaryAccount account;
        try
        {
            account = await _c.Legendary.SignInWithExchangeTokenAsync(code.Trim(), ct);
        }
        catch (GliderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new GliderException(Errors.AuthFailed(e.Message), e);
        }

        var state = AuthMapping.From(account);
        _c.Hub.Mutate(s => s with { Auth = state });
        _c.Log.Info("auth", "signed in");
        return state;
    }

    public async Task<AuthState> SignOutAsync(CancellationToken ct)
    {
        if (_c.ActiveJob(JobKind.Install, JobKind.Update, JobKind.Repair, JobKind.Import) is { } job && job.Status == JobStatus.Running)
        {
            throw new GliderException(Errors.Busy($"the {job.Kind.ToString().ToLowerInvariant()}"));
        }

        await _c.Legendary.SignOutAsync(ct);
        try
        {
            await _c.Host.ClearLoginSessionAsync();
        }
        catch (Exception e)
        {
            // Legendary's tokens are gone; leftover webview cookies only pre-fill the next login.
            _c.Log.Warn("auth", "clearing login cookies failed", e);
        }

        var state = new AuthState(AuthStatus.SignedOut, null);
        _c.Hub.Mutate(s => s with { Auth = state });
        _c.Log.Info("auth", "signed out");
        return state;
    }

    /// <summary>Pre-warm: offline "is anyone signed in" (no network).</summary>
    public async Task LoadOfflineAsync(CancellationToken ct)
    {
        var account = await _c.Legendary.GetAccountOfflineAsync(ct);
        _c.Hub.Mutate(s => s with { Auth = AuthMapping.From(account) });
    }

    /// <summary>Pre-warm: online token refresh. Network failures keep the offline answer; auth failures mark Expired.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (_c.Hub.State.Auth.Status != AuthStatus.SignedIn)
        {
            return;
        }

        try
        {
            var account = await _c.Legendary.RefreshSessionAsync(ct);
            _c.Hub.Mutate(s => s with { Auth = AuthMapping.From(account) });
        }
        catch (GliderException e) when (e.Error.Code is ErrorCode.AuthExpired or ErrorCode.AuthFailed or ErrorCode.NotSignedIn)
        {
            _c.Log.Warn("auth", $"session refresh rejected: {e.Error.Code}");
            _c.Hub.Mutate(s => s with { Auth = new AuthState(AuthStatus.Expired, s.Auth.Account) });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _c.Log.Warn("auth", "session refresh failed (offline?); keeping the offline sign-in state", e);
        }
    }
}
