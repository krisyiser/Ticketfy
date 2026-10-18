using System.Net;
using System.Net.Sockets;

namespace Ticketfy.Core.Helpers;

public static class NetworkHelper
{
    public static string GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return "127.0.0.1";
    }
}
