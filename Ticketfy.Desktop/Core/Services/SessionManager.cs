using System;
using Ticketfy.Core.Models;

namespace Ticketfy.Core.Services;

public interface ISessionManager
{
    UserModel? CurrentCashier { get; }
    bool IsTerminalLocked { get; }
    event Action<UserModel>? CashierChanged;
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

    public event Action<UserModel>? CashierChanged;
    public event Action<bool>? LockStateChanged;

    public void SwitchCashier(UserModel newUser)
    {
        CurrentCashier = newUser;
        CashierChanged?.Invoke(newUser);
    }

    public void StartSession(UserModel user)
    {
        SwitchCashier(user);
    }

    public void ClearSession()
    {
        CurrentCashier = null;
    }

    public void LockTerminal()
    {
        IsTerminalLocked = true;
        LockStateChanged?.Invoke(true);
    }

    public void UnlockTerminal()
    {
        IsTerminalLocked = false;
        LockStateChanged?.Invoke(false);
    }
}
