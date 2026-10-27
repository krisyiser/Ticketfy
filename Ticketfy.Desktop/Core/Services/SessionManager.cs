using System;
using Ticketfy.Core.Models;

namespace Ticketfy.Core.Services;

public interface ISessionManager
{
    UserModel? CurrentCashier { get; }
    bool IsTerminalLocked { get; }
    long SessionVersion { get; }
    event Action<UserModel?>? CashierChanged;
    event Action<bool>? LockStateChanged;

    void SwitchCashier(UserModel newUser);
    void StartSession(UserModel user);
    void ClearSession();
    void LockTerminal();
    void UnlockTerminal();
}

public class SessionManager : ISessionManager
{
    public UserModel? CurrentCashier { get; private set; }
    public bool IsTerminalLocked { get; private set; }
    public long SessionVersion { get; private set; }

    public event Action<UserModel?>? CashierChanged;
    public event Action<bool>? LockStateChanged;

    public void SwitchCashier(UserModel newUser)
    {
        ArgumentNullException.ThrowIfNull(newUser);
        ++SessionVersion;
        CurrentCashier = newUser;
        CashierChanged?.Invoke(newUser);
    }

    public void StartSession(UserModel user)
    {
        IsTerminalLocked = false;
        SwitchCashier(user);
        LockStateChanged?.Invoke(false);
    }

    public void ClearSession()
    {
        ++SessionVersion;
        CurrentCashier = null;
        IsTerminalLocked = false;
        CashierChanged?.Invoke(null);
        LockStateChanged?.Invoke(false);
    }

    public void LockTerminal()
    {
        ++SessionVersion;
        IsTerminalLocked = true;
        LockStateChanged?.Invoke(true);
    }

    public void UnlockTerminal()
    {
        if (CurrentCashier is null) return;
        ++SessionVersion;
        IsTerminalLocked = false;
        LockStateChanged?.Invoke(false);
    }
}
